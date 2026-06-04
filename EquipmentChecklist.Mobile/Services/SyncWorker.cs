using System.Text.Json;
using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Background worker that drains <see cref="SubmissionQueue"/> and
/// <see cref="ActionQueue"/> against the server whenever the app can actually
/// reach the API.
///
/// <para>Three triggers fire a drain pass:</para>
/// <list type="bullet">
///   <item><description><c>Connectivity.ConnectivityChanged</c> — device just got
///   on Wi-Fi/LTE. Always the first signal on a phone leaving a dead zone.</description></item>
///   <item><description><see cref="ApiHealth.Changed"/> — the server became
///   reachable WITHOUT a connectivity change. This catches the case where the
///   device had Wi-Fi the whole time but the API was down (server restart,
///   VPN drop, captive portal, DNS hiccup). A connectivity listener alone
///   would never wake up for this.</description></item>
///   <item><description><see cref="AuthService.SignedIn"/> — the user just
///   signed in. Without this, an app that boots online but pre-sign-in fires
///   its single connectivity-triggered drain attempt against a worker that
///   immediately bails on <c>!IsSignedIn</c>, and nothing re-triggers it after
///   sign-in completes.</description></item>
/// </list>
///
/// <para>One singleton instance per app. <c>MauiProgram</c> resolves it after
/// <c>builder.Build()</c> so the listeners are wired up before any page
/// renders — operators don't have to open the dashboard for a drained queue
/// to start flushing.</para>
/// </summary>
public class SyncWorker : IDisposable
{
    private readonly SubmissionQueue      _queue;
    private readonly ActionQueue          _actions;
    private readonly AuditQueue?          _audit;
    private readonly ApiClient            _api;
    private readonly AuthService          _auth;
    private readonly ApiHealth            _health;
    private readonly NotificationService? _notifications;
    private readonly SemaphoreSlim        _runLock = new(1, 1);
    private bool                          _disposed;

    /// <summary>Fires after a drain pass completes (success or otherwise) so
    /// the UI can refresh the badge.</summary>
    public event Action? DrainCompleted;

    /// <summary>Fires when a prune pass dropped one or more rows because they
    /// exceeded the queue age limit. UI subscribers (currently the
    /// <c>StalePruneToaster</c>) show a one-shot warning so the operator
    /// knows old work didn't ship.</summary>
    public event Action<StalePruneInfo>? PrunedStale;

    /// <summary>
    /// Hard cap on how long a row may sit in the submission / action queues.
    /// Older rows reference machines, templates, or assignments that have
    /// almost certainly drifted by now — keeping them around buys nothing
    /// except SQLite bloat. Sixty days matches the operational expectation
    /// at the mines we ship to.
    /// </summary>
    public static readonly TimeSpan MAX_QUEUE_AGE = TimeSpan.FromDays(60);

    /// <summary>
    /// Audit gets a longer leash because the loss is "we didn't capture
    /// local-timing detail" rather than "the operator's work didn't make
    /// it to the server" — the latter is more user-facing.
    /// </summary>
    public static readonly TimeSpan MAX_AUDIT_AGE = TimeSpan.FromDays(90);

    /// <summary>Throttles prune passes so we don't hammer SQLite — once per
    /// hour is plenty given the only scenario producing stale rows is a
    /// phone offline for weeks.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);
    private DateTime _lastPruneAt = DateTime.MinValue;

    public SyncWorker(SubmissionQueue queue, ActionQueue actions,
                      ApiClient api, AuthService auth, ApiHealth health,
                      AuditQueue? audit = null,
                      NotificationService? notifications = null)
    {
        _queue         = queue;
        _actions       = actions;
        _audit         = audit;
        _api           = api;
        _auth          = auth;
        _health        = health;
        _notifications = notifications;

        // ── Trigger sources ────────────────────────────────────────────────
        Connectivity.ConnectivityChanged += OnConnectivityChanged;
        _health.Changed                  += OnHealthChanged;
        _auth.SignedIn                   += OnSignedIn;

        // Best-effort drain on startup. Both gates must already be in place:
        // device thinks it has internet AND we already have a session.
        if (Connectivity.NetworkAccess == NetworkAccess.Internet && _auth.IsSignedIn)
            _ = Task.Run(DrainAsync);
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        // Only drain on a positive flip — losing internet should NOT trigger
        // a pointless server call that will just queue the work back up.
        if (e.NetworkAccess == NetworkAccess.Internet)
            _ = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Fires when <see cref="ApiHealth.IsOnline"/> transitions. Without this
    /// hook a server-side recovery (API came back) goes unnoticed by the
    /// drainer until the user happens to bounce their connection.
    /// </summary>
    private void OnHealthChanged()
    {
        if (_health.IsOnline)
            _ = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Fires when the user signs in. Picks up the case where the app boots
    /// online but the connectivity-triggered drain already bailed on
    /// <c>!IsSignedIn</c> before the user finished authenticating.
    /// </summary>
    private void OnSignedIn()
    {
        if (Connectivity.NetworkAccess == NetworkAccess.Internet)
            _ = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Try to push every queued submission and every queued supervisor/mechanic
    /// action. Stops on the first network error or auth rejection — the next
    /// connectivity event re-triggers us.
    /// </summary>
    public async Task DrainAsync()
    {
        if (!await _runLock.WaitAsync(0)) return;  // another drain is in flight
        try
        {
            if (!_auth.IsSignedIn) return;          // no JWT yet → nothing to do

            // Bounded retention runs first so we don't waste a drain pass
            // re-trying rows older than MAX_QUEUE_AGE. Throttled internally
            // so back-to-back drain triggers don't re-prune.
            await PruneStaleQueuesAsync();

            // Order matters: submissions before actions (user's actual work
            // beats supervisor/mechanic follow-ups), and audit last because
            // it's the lowest-priority telemetry — important to keep, but
            // we never block a user's primary work behind shipping audit rows.
            await DrainSubmissionsAsync();
            await DrainActionsAsync();
            await DrainAuditAsync();
        }
        finally
        {
            _runLock.Release();
            DrainCompleted?.Invoke();
        }
    }

    // ── Bounded retention ───────────────────────────────────────────────────
    //
    // Prevents the three local queues (submissions, actions, audit) from
    // growing unbounded on a device that's been offline for months —
    // vacation, lost phone, retired-but-still-running. Without this, SQLite
    // grows until the app is sluggish or until a queue row gets corrupt and
    // we can't even read the others.
    //
    // Throttled: only runs once per <see cref="PruneInterval"/> regardless
    // of how many DrainAsync calls fire (connectivity flips can fire several
    // in quick succession).
    private async Task PruneStaleQueuesAsync()
    {
        if (DateTime.UtcNow - _lastPruneAt < PruneInterval) return;
        _lastPruneAt = DateTime.UtcNow;

        PruneResult subPrune, actPrune, audPrune;
        try { subPrune = await _queue.PruneOldAsync(MAX_QUEUE_AGE); }
        catch { subPrune = PruneResult.Empty; }
        try { actPrune = await _actions.PruneOldAsync(MAX_QUEUE_AGE); }
        catch { actPrune = PruneResult.Empty; }
        try { audPrune = _audit is null ? PruneResult.Empty
                                        : await _audit.PruneOldAsync(MAX_AUDIT_AGE); }
        catch { audPrune = PruneResult.Empty; }

        var total = subPrune.Dropped + actPrune.Dropped + audPrune.Dropped;
        if (total > 0)
        {
            // Fire the event from a thread-pool task so a subscriber that
            // does heavy work (e.g. JS interop for a toast) can't block the
            // drain pipeline.
            var summary = new StalePruneInfo(
                SubmissionsDropped: subPrune.Dropped,
                ActionsDropped:     actPrune.Dropped,
                AuditDropped:       audPrune.Dropped,
                MaxAge:             MAX_QUEUE_AGE);
            _ = Task.Run(() => PrunedStale?.Invoke(summary));
        }
    }

    // ── Operator submissions ────────────────────────────────────────────────
    private async Task DrainSubmissionsAsync()
    {
        var items = await _queue.GetAllAsync();
        foreach (var item in items)
        {
            SyncSubmissionRequest? req = null;
            try { req = JsonSerializer.Deserialize<SyncSubmissionRequest>(item.JsonPayload); }
            catch { /* corrupted row → drop and continue */ }

            if (req == null)
            {
                await _queue.RemoveAsync(item.Id);
                continue;
            }

            try
            {
                var result = await _api.SubmitAsync(req);
                if (result != null)
                {
                    await _queue.RemoveAsync(item.Id);  // success → forget it
                }
                else
                {
                    // Non-2xx that didn't throw — usually 401 (token expired).
                    // No point hammering the server until the user signs in again.
                    await _queue.MarkRetryAsync(item.Id, "Server rejected (auth expired?)");
                    break;
                }
            }
            catch (HttpRequestException ex)
            {
                // Network blip mid-drain. Bail out — connectivity events
                // will fire us again the next time we have signal.
                await _queue.MarkRetryAsync(item.Id, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                // Other failure (serialization, 500). Bump retry, move on.
                await _queue.MarkRetryAsync(item.Id, ex.Message);
            }
        }
    }

    // ── Supervisor + Mechanic actions ───────────────────────────────────────

    /// <summary>
    /// After this many failed attempts we give up on a row and drop it — the
    /// server probably has a persistent problem we can't recover from from the
    /// device. The LastError column captures the most recent message for ops.
    /// </summary>
    private const int MAX_RETRIES = 5;

    private async Task DrainActionsAsync()
    {
        var items = await _actions.GetAllAsync();
        // Tracks whether ANY permanent outcome fired this pass. When it does,
        // the server has just written a ConflictRejected notification for the
        // losing actor (see SyncController.SupervisorSignOffApi / MechanicClaim
        // / etc) and the SignalR push might have raced our HTTP response — so
        // we explicitly refresh the unread count after the drain finishes.
        // Without this, the user only sees the conflict notice on their next
        // navigation, which can be ten minutes later.
        bool anyPermanent = false;

        foreach (var item in items)
        {
            // Hard ceiling: don't loop forever on a row the server keeps refusing.
            if (item.RetryCount >= MAX_RETRIES)
            {
                await _actions.RemoveAsync(item.Id);
                continue;
            }

            try
            {
                var outcome = await ExecuteAsync(item);
                switch (outcome.Kind)
                {
                    case DrainOutcomeKind.Success:
                        await _actions.RemoveAsync(item.Id);
                        break;

                    case DrainOutcomeKind.Permanent:
                        // 4xx: the server has a definitive "no" for this row
                        // (submission already signed off, defect already claimed
                        // by someone else, etc). Drop it — retrying can't change
                        // the answer.
                        await _actions.RemoveAsync(item.Id);
                        anyPermanent = true;
                        break;

                    case DrainOutcomeKind.Transient:
                        // 5xx: server returned an error but the call shape is
                        // valid. Keep the row, bump the retry, try the next one
                        // — we don't break the whole drain on a single 500.
                        await _actions.MarkRetryAsync(item.Id, outcome.Error);
                        break;
                }
            }
            catch (HttpRequestException ex)
            {
                // Still offline mid-drain — bail and wait for the next event.
                await _actions.MarkRetryAsync(item.Id, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                // Unexpected (serialization, deserialization). Treat as transient
                // so we get another shot after a fix / restart.
                await _actions.MarkRetryAsync(item.Id, ex.Message);
            }
        }

        // If at least one row in this pass came back as Permanent — i.e. a
        // 4xx — the server almost certainly just wrote a ConflictRejected
        // notification for us. Refresh the unread count so the bell badge
        // ticks up immediately instead of waiting for the next SignalR push
        // or page navigation. Best-effort: a network failure here just
        // leaves the badge stale until the next refresh.
        if (anyPermanent && _notifications is not null)
        {
            try { await _notifications.RefreshUnreadCountAsync(); }
            catch { /* offline / transient — UI will catch up later */ }
        }
    }

    private enum DrainOutcomeKind { Success, Permanent, Transient }
    private record DrainOutcome(DrainOutcomeKind Kind, string? Error)
    {
        public static DrainOutcome FromApi(ApiResult r)
        {
            if (r.Ok)          return new(DrainOutcomeKind.Success,   null);
            if (r.IsTransient) return new(DrainOutcomeKind.Transient, r.Error);
            // Any 4xx, or status==null on the fall-through, gets treated as
            // permanent — null status means the call never reached its happy
            // path and isn't worth retrying without manual intervention.
            return new(DrainOutcomeKind.Permanent, r.Error);
        }
    }

    // ── Audit drain ─────────────────────────────────────────────────────────
    //
    // Ships queued audit events to /api/sync/audit in batches of 100. Stops
    // immediately on any HTTP failure — audit is not allowed to spin in a
    // tight loop hammering the server. Rows are deleted only on a confirmed
    // 2xx; 4xx and 5xx both leave the rows in place so the audit trail can
    // never silently lose history.
    //
    // The 100-row batch matches what the server tolerates without timing out
    // under cold-cache conditions. Server caps at 500, so we stay well under.
    private const int AUDIT_BATCH = 100;

    private async Task DrainAuditAsync()
    {
        if (_audit is null) return;

        while (true)
        {
            var batch = await _audit.TakeBatchAsync(AUDIT_BATCH);
            if (batch.Count == 0) return;

            var dtos = batch.Select(AuditQueue.ToDto).ToList();
            ApiResult result;
            try
            {
                result = await _api.PostAuditBatchAsync(dtos);
            }
            catch (HttpRequestException)
            {
                // Network died — bail; the next trigger will retry.
                return;
            }

            if (result.Ok)
            {
                await _audit.DeleteAsync(batch.Select(b => b.Id));
                // Loop: if we just drained a full batch, there might be more.
                if (batch.Count < AUDIT_BATCH) return;
                continue;
            }

            // 4xx (permanent) — server rejected this batch. Don't drop the
            // rows; an admin can replay manually after the fix lands. Log
            // and bail to avoid hammering.
            // 5xx (transient) — leave rows + bail; next trigger retries.
            return;
        }
    }

    /// <summary>
    /// Dispatch one queued action to the matching ApiClient call. The returned
    /// <see cref="DrainOutcome"/> tells the drainer whether the row should be
    /// removed (success / permanent) or kept (transient).
    /// </summary>
    private async Task<DrainOutcome> ExecuteAsync(ActionQueue.QueuedAction item)
    {
        switch (item.ActionType)
        {
            case ActionQueue.ActionKinds.SignOff:
            {
                var p = JsonSerializer.Deserialize<SignOffPayload>(item.PayloadJson);
                if (p == null) return new(DrainOutcomeKind.Permanent, "corrupt payload");
                return DrainOutcome.FromApi(
                    await _api.SupervisorSignOffAsync(item.TargetId, p.Resolution, p.Signature));
            }

            case ActionQueue.ActionKinds.Reject:
            {
                var p = JsonSerializer.Deserialize<RejectPayload>(item.PayloadJson);
                if (p == null) return new(DrainOutcomeKind.Permanent, "corrupt payload");
                return DrainOutcome.FromApi(
                    await _api.SupervisorRejectAsync(item.TargetId, p.Reason, p.MechanicId));
            }

            case ActionQueue.ActionKinds.Claim:
                return DrainOutcome.FromApi(await _api.MechanicClaimAsync(item.TargetId));

            case ActionQueue.ActionKinds.OrderPart:
            {
                var p = JsonSerializer.Deserialize<OrderPartPayload>(item.PayloadJson);
                if (p == null) return new(DrainOutcomeKind.Permanent, "corrupt payload");
                return DrainOutcome.FromApi(
                    await _api.MechanicOrderPartAsync(item.TargetId, p.PartRequired, p.PartNumber));
            }

            case ActionQueue.ActionKinds.Complete:
            {
                var p = JsonSerializer.Deserialize<CompletePayload>(item.PayloadJson);
                if (p == null) return new(DrainOutcomeKind.Permanent, "corrupt payload");
                return DrainOutcome.FromApi(
                    await _api.MechanicCompleteAsync(item.TargetId, p.Notes, p.Signature));
            }

            default:
                // Unknown discriminator — drop the row so we don't loop forever
                // on it after a stale-app rollback.
                return new(DrainOutcomeKind.Permanent, $"unknown action: {item.ActionType}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Connectivity.ConnectivityChanged -= OnConnectivityChanged;
        _health.Changed                  -= OnHealthChanged;
        _auth.SignedIn                   -= OnSignedIn;
        _runLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Payload shapes (must match ActionQueue.Enqueue* anonymous objects) ─
    private class SignOffPayload   { public int    Resolution { get; set; } public string Signature { get; set; } = ""; }
    private class RejectPayload    { public string Reason     { get; set; } = ""; public string MechanicId { get; set; } = ""; }
    private class OrderPartPayload { public string PartRequired { get; set; } = ""; public string? PartNumber { get; set; } }
    private class CompletePayload  { public string? Notes      { get; set; } public string Signature { get; set; } = ""; }
}
