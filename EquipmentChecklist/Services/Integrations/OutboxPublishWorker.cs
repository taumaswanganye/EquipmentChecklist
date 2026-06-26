using System.Text.Json;
using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services.Integrations;

/// <summary>
/// Drains the <c>OutboxMessages</c> table. Wakes up every
/// <see cref="PollInterval"/>, pulls a batch of Draft rows whose
/// <c>NextAttemptAt</c> has elapsed, dispatches each through
/// <see cref="IIntegrationPublisher"/>, and updates the row's state:
///
/// <list type="bullet">
///   <item><description><b>Success</b> → Status = Sent, ProcessedAt = now.</description></item>
///   <item><description><b>Transient failure</b> → Status stays Draft,
///   AttemptCount++, LastError captured, NextAttemptAt =
///   now + 2^attempt seconds (capped at 1 hour).</description></item>
///   <item><description><b>Permanent failure</b> (AttemptCount >= MaxRetries)
///   → Status = DeadLetter, ProcessedAt = now, audit event written.
///   Admin reviews dead-letter rows on the Outbox admin page.</description></item>
/// </list>
///
/// <para><b>Why hosted scoped scope:</b> a <c>BackgroundService</c> is a
/// singleton. <c>ApplicationDbContext</c> + <c>IIntegrationPublisher</c>
/// are scoped. We create a fresh DI scope per poll pass so each pass gets
/// its own clean DbContext (avoids change-tracker pollution between passes
/// and lets the publisher resolve its own scoped deps cleanly).</para>
/// </summary>
public class OutboxPublishWorker : BackgroundService
{
    private readonly IServiceScopeFactory       _scopes;
    private readonly ILogger<OutboxPublishWorker> _log;

    /// <summary>How often the worker wakes to look for Draft rows.
    /// 5 s is the default — fast enough that SAP sees defects within
    /// a few seconds, slow enough that the DB isn't getting hammered
    /// for an empty table.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How many rows the worker pulls per pass. A small batch
    /// keeps each pass fast (so a publish failure doesn't tie up the
    /// worker for too long) but big enough to drain a backlog quickly
    /// after a long outage.</summary>
    private const int BatchSize = 50;

    /// <summary>Max retries before a row is moved to DeadLetter. With
    /// exponential backoff capped at 1 h, 5 retries spans roughly 32
    /// seconds — enough to ride out a short broker hiccup, short enough
    /// that genuinely broken messages stop wasting cycles. Overridable
    /// via the <c>Outbox.MaxRetries</c> AppSetting.</summary>
    private const int DefaultMaxRetries = 5;

    public OutboxPublishWorker(IServiceScopeFactory scopes,
                               ILogger<OutboxPublishWorker> log)
    {
        _scopes = scopes;
        _log    = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("OutboxPublishWorker started (interval {Interval}s)",
            PollInterval.TotalSeconds);

        // Tiny grace delay on boot so DB migrations + seed run first.
        try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DrainOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Catch-all so a single bad pass doesn't kill the worker.
                // The next pass will retry whatever was Draft.
                _log.LogError(ex, "OutboxPublishWorker pass failed");
            }

            try { await Task.Delay(PollInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    /// <summary>One drain pass — public for tests that want to invoke a
    /// single pass deterministically instead of waiting for the timer.</summary>
    public async Task DrainOnceAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db         = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var publisher  = scope.ServiceProvider.GetRequiredService<IIntegrationPublisher>();
        // Phase 7.3 — resolve the Key Control publisher from the same scope
        // so it gets its own scoped ConfigurationService + HttpClientFactory.
        // NoOpKeyControlPublisher is the default registration when
        // KeyControl.Enabled is false (or unconfigured).
        var keyControl = scope.ServiceProvider.GetRequiredService<IKeyControlPublisher>();
        var config     = scope.ServiceProvider.GetRequiredService<ConfigurationService>();

        var maxRetries = await config.GetIntAsync("Outbox.MaxRetries", DefaultMaxRetries, ct);

        var now = DateTime.UtcNow;
        var batch = await db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Draft
                     && (m.NextAttemptAt == null || m.NextAttemptAt <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return;

        _log.LogDebug("OutboxPublishWorker draining {Count} message(s)", batch.Count);

        foreach (var row in batch)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DispatchAsync(publisher, keyControl, row, ct);
                row.Status        = OutboxStatus.Sent;
                row.ProcessedAt   = DateTime.UtcNow;
                row.LastError     = null;
                row.NextAttemptAt = null;
            }
            catch (Exception ex)
            {
                row.AttemptCount++;
                row.LastError = ex.Message.Length > 1900
                                  ? ex.Message[..1900] + "…"
                                  : ex.Message;

                if (row.AttemptCount >= maxRetries)
                {
                    // Terminal — stop trying. The admin reviews + maybe
                    // retries manually from the Outbox admin page (which
                    // resets Status=Draft, AttemptCount=0).
                    row.Status        = OutboxStatus.DeadLetter;
                    row.ProcessedAt   = DateTime.UtcNow;
                    row.NextAttemptAt = null;
                    _log.LogError(ex,
                        "Outbox message {Id} ({Type}/{Agg}) DEAD-LETTERED after {Attempts} attempts",
                        row.Id, row.MessageType, row.AggregateId, row.AttemptCount);

                    // Audit the dead-letter so DMR can trace the lost integration.
                    var audit = scope.ServiceProvider.GetService<AuditService>();
                    if (audit != null)
                    {
                        try
                        {
                            await audit.LogAsync(
                                "outbox.dead_lettered",
                                targetType: row.AggregateType,
                                targetId:   long.TryParse(row.AggregateId, out var aid) ? aid : null,
                                payload:    new {
                                    outboxId   = row.Id,
                                    messageType= row.MessageType,
                                    attempts   = row.AttemptCount,
                                    lastError  = row.LastError
                                },
                                ct:         ct);
                        }
                        catch { /* audit must never break the worker */ }
                    }
                }
                else
                {
                    // Exponential backoff: 2s, 4s, 8s, 16s, 32s … capped 1h.
                    var delaySec = Math.Min(
                        Math.Pow(2, row.AttemptCount),
                        TimeSpan.FromHours(1).TotalSeconds);
                    row.NextAttemptAt = DateTime.UtcNow.AddSeconds(delaySec);
                    _log.LogWarning(
                        "Outbox message {Id} publish failed (attempt {Attempt}/{Max}); next try at {NextAttempt} — {Error}",
                        row.Id, row.AttemptCount, maxRetries, row.NextAttemptAt, ex.Message);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Route an outbox row to the correct strongly-typed publisher
    /// method. Add a new case here for every new MessageType — keeping
    /// the dispatch explicit avoids reflection magic and makes "what
    /// events does this system emit?" answerable by reading one switch.
    /// </summary>
    private static async Task DispatchAsync(
        IIntegrationPublisher publisher,
        IKeyControlPublisher  keyControl,
        OutboxMessage         row,
        CancellationToken     ct)
    {
        switch (row.MessageType)
        {
            case "DefectOrderCreated":
                var payload = JsonSerializer.Deserialize<IntegrationDefectPayload>(row.PayloadJson)
                    ?? throw new InvalidOperationException(
                        $"Outbox row {row.Id} has unparseable DefectOrderCreated payload.");
                await publisher.PublishDefectOrderCreatedAsync(payload, ct);
                break;

            // ── Phase 7.3 — Key Control physical interlock ───────────────
            // ImmobiliseAsync is fired by ChecklistService.SubmitAsync when a
            // NO-GO submission lands. UnlockAsync is fired by
            // AdminController.ClearMachine when the admin returns the
            // machine to service. Both share the same payload shape so the
            // outbox message type is the only thing the dispatcher needs to
            // route on.
            case KeyControlMessageTypes.Immobilise:
                {
                    var kc = JsonSerializer.Deserialize<KeyControlPayload>(row.PayloadJson)
                        ?? throw new InvalidOperationException(
                            $"Outbox row {row.Id} has unparseable {KeyControlMessageTypes.Immobilise} payload.");
                    await keyControl.ImmobiliseAsync(kc, ct);
                    break;
                }

            case KeyControlMessageTypes.Unlock:
                {
                    var kc = JsonSerializer.Deserialize<KeyControlPayload>(row.PayloadJson)
                        ?? throw new InvalidOperationException(
                            $"Outbox row {row.Id} has unparseable {KeyControlMessageTypes.Unlock} payload.");
                    await keyControl.UnlockAsync(kc, ct);
                    break;
                }

            default:
                throw new NotSupportedException(
                    $"Unknown outbox MessageType: '{row.MessageType}'. " +
                    $"Add a switch case in OutboxPublishWorker.DispatchAsync.");
        }
    }
}
