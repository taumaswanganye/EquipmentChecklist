using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

/// <summary>
/// Daily background worker that walks the <c>OperatorCompetencies</c>
/// table and fires renewal reminders + expiry audit events.
///
/// <para>Schedule: runs once a day at 06:00 UTC (≈ 08:00 SAST), which is
/// usually before the first shift hands over so a supervisor can act on
/// the reminder before the operator turns up. On startup the worker
/// computes the time-to-next-06:00 and waits, then loops daily.</para>
///
/// <para>Idempotency: each competency row carries three nullable
/// <c>Reminder*SentAt</c> timestamps. The worker checks the appropriate
/// column before sending so re-runs the same day (e.g. server restart,
/// quick redeploy) don't re-send. The columns are persisted with the
/// timestamp so the audit trail also shows when the reminder fired.</para>
///
/// <para>Three bands:</para>
/// <list type="bullet">
///   <item><description><b>≤30 days, &gt;7 days, Reminder30 not set</b>
///   — send the 30-day reminder, stamp Reminder30DaysSentAt.</description></item>
///   <item><description><b>≤7 days, &gt;0 days, Reminder7 not set</b>
///   — send the 7-day reminder, stamp Reminder7DaysSentAt.</description></item>
///   <item><description><b>≤0 days, ExpiryNotice not set</b> — send the
///   expiry notice, stamp ExpiryNoticeSentAt, write a
///   <c>competency.expired</c> audit row.</description></item>
/// </list>
/// </summary>
public class CompetencyExpiryWorker : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CompetencyExpiryWorker> _log;

    /// <summary>Hour of day (UTC) the worker fires. 06:00 UTC ≈ 08:00 SAST.</summary>
    private const int RunHourUtc = 6;

    public CompetencyExpiryWorker(IServiceProvider services, ILogger<CompetencyExpiryWorker> log)
    {
        _services = services;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("CompetencyExpiryWorker starting; first run pending");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Sleep until the next 06:00 UTC.
            var now      = DateTime.UtcNow;
            var nextRun  = new DateTime(now.Year, now.Month, now.Day, RunHourUtc, 0, 0, DateTimeKind.Utc);
            if (nextRun <= now) nextRun = nextRun.AddDays(1);
            var wait = nextRun - now;
            _log.LogInformation("CompetencyExpiryWorker next run at {NextRun} (in {Wait})", nextRun, wait);

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // A failure in one day's pass must not crash the host —
                // log, wait until tomorrow, try again.
                _log.LogError(ex, "CompetencyExpiryWorker pass failed");
            }
        }
    }

    /// <summary>
    /// Public so a manual "run now" admin button (future enhancement)
    /// or a unit test can invoke it directly without spinning up the
    /// hosted service.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db          = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var email       = scope.ServiceProvider.GetRequiredService<EmailService>();
        var audit       = scope.ServiceProvider.GetRequiredService<AuditService>();
        // DB-backed settings — admins changing Mine.MineManagerEmail or
        // Mine.SheOfficerEmail in Admin → Settings take effect on the next
        // worker pass (i.e. tomorrow morning) without a restart.
        var config      = scope.ServiceProvider.GetRequiredService<ConfigurationService>();
        // In-app notifications fire alongside emails — operators on a
        // phone don't read work email, but they DO see the bell badge.
        var notifs      = scope.ServiceProvider.GetRequiredService<NotificationService>();

        var now = DateTime.UtcNow;

        // Pull every active competency expiring within 30 days OR
        // already expired without a notice yet.
        var due = await db.OperatorCompetencies
            .Include(c => c.Operator)
            .Where(c => c.IsActive
                     && (c.ExpiresAt <= now.AddDays(30)))
            .ToListAsync(ct);

        if (due.Count == 0)
        {
            _log.LogInformation("CompetencyExpiryWorker: nothing to do");
            return;
        }

        // Recipients beyond the operator themselves are global per the
        // admin's scoping choices — supervisor (denormalised via the
        // OperatorSupervisorAssignments table), mine manager, SHE officer.
        // Recipients beyond the operator + supervisor — pulled from the
        // AppSettings table so admins can rotate the addresses live. The
        // Email.ManagerEmail key acts as the fallback when Mine.MineManagerEmail
        // is blank (one-human-two-roles mines).
        var sheOfficer  = await config.GetAsync("Mine.SheOfficerEmail", ct);
        var mineManager = await config.GetAsync("Mine.MineManagerEmail", ct)
                       ?? await config.GetAsync("Email.ManagerEmail", ct);

        // Cache supervisor lookup so we don't re-query for the same
        // operator multiple times in this pass.
        var supervisorLookup = new Dictionary<string, (string? email, string? name)>();

        async Task<(string? email, string? name)> SupervisorOfAsync(string operatorId)
        {
            if (supervisorLookup.TryGetValue(operatorId, out var hit)) return hit;
            var sup = await db.OperatorSupervisorAssignments
                .Where(a => a.OperatorId == operatorId && a.IsActive)
                .OrderByDescending(a => a.AssignedAt)
                .Select(a => new { a.Supervisor.Email, a.Supervisor.FullName })
                .FirstOrDefaultAsync(ct);
            var pair = (sup?.Email, sup?.FullName);
            supervisorLookup[operatorId] = pair;
            return pair;
        }

        int sent30 = 0, sent7 = 0, sentExpiry = 0;
        foreach (var comp in due)
        {
            ct.ThrowIfCancellationRequested();

            var op           = comp.Operator;
            var machineLabel = comp.MachineType.ToString();
            var daysLeft     = (int)Math.Ceiling((comp.ExpiresAt - now).TotalDays);

            // Build the recipient list ONCE per competency.
            var (supEmail, supName) = await SupervisorOfAsync(comp.OperatorId);
            var recipients = new List<(string Email, string Name)>();
            if (!string.IsNullOrWhiteSpace(op.Email))       recipients.Add((op.Email!, op.FullName));
            if (!string.IsNullOrWhiteSpace(supEmail))       recipients.Add((supEmail!, supName ?? "Supervisor"));
            if (!string.IsNullOrWhiteSpace(mineManager))    recipients.Add((mineManager!, "Mine Manager"));
            if (!string.IsNullOrWhiteSpace(sheOfficer))     recipients.Add((sheOfficer!, "SHE Officer"));

            try
            {
                if (comp.ExpiresAt <= now)
                {
                    // Expired — send notice + audit event ONCE.
                    if (comp.ExpiryNoticeSentAt.HasValue) continue;

                    foreach (var r in recipients)
                    {
                        await email.SendCompetencyExpiredAsync(
                            toEmail: r.Email, toName: r.Name,
                            operatorName: op.FullName,
                            machineType:  machineLabel,
                            certificateNumber: comp.CertificateNumber,
                            expiredAt: comp.ExpiresAt);
                    }
                    comp.ExpiryNoticeSentAt = now;
                    sentExpiry++;

                    // In-app notification so the operator's mobile bell
                    // bumps the next time they open the app. The body
                    // uses past-tense "expired" since the date has passed.
                    try
                    {
                        await notifs.PushAsync(
                            userId: comp.OperatorId,
                            kind:   NotificationKinds.CompetencyExpired,
                            title:  $"⛔ Competency expired — {machineLabel}",
                            body:   $"Your certificate for {machineLabel} expired on {comp.ExpiresAt:yyyy-MM-dd}. " +
                                    "Contact your administrator to renew.");
                    }
                    catch { /* notification failure must not block the email pass */ }

                    try
                    {
                        await audit.LogAsync(
                            action:     AuditActions.CompetencyExpired,
                            targetType: "OperatorCompetency",
                            targetId:   comp.Id,
                            payload:    new
                            {
                                operatorId   = comp.OperatorId,
                                operatorName = op.FullName,
                                machineType  = machineLabel,
                                certificate  = comp.CertificateNumber,
                                expiredAt    = comp.ExpiresAt
                            },
                            ct: ct);
                    }
                    catch { /* audit failures shouldn't block the email pass */ }
                }
                else if (daysLeft <= 7)
                {
                    if (comp.Reminder7DaysSentAt.HasValue) continue;
                    foreach (var r in recipients)
                    {
                        await email.SendCompetencyExpiringAsync(
                            r.Email, r.Name, op.FullName, machineLabel,
                            comp.CertificateNumber, comp.ExpiresAt, daysLeft);
                    }
                    try
                    {
                        await notifs.PushAsync(
                            userId: comp.OperatorId,
                            kind:   NotificationKinds.CompetencyExpiring,
                            title:  $"⚠ {machineLabel} competency expires in {daysLeft} day{(daysLeft == 1 ? "" : "s")}",
                            body:   $"Your certificate for {machineLabel} expires on {comp.ExpiresAt:yyyy-MM-dd}. " +
                                    "Renew it now to avoid being blocked from operating.");
                    }
                    catch { }
                    comp.Reminder7DaysSentAt = now;
                    sent7++;
                }
                else if (daysLeft <= 30)
                {
                    if (comp.Reminder30DaysSentAt.HasValue) continue;
                    foreach (var r in recipients)
                    {
                        await email.SendCompetencyExpiringAsync(
                            r.Email, r.Name, op.FullName, machineLabel,
                            comp.CertificateNumber, comp.ExpiresAt, daysLeft);
                    }
                    try
                    {
                        await notifs.PushAsync(
                            userId: comp.OperatorId,
                            kind:   NotificationKinds.CompetencyExpiring,
                            title:  $"⏳ {machineLabel} competency expires in {daysLeft} days",
                            body:   $"Your certificate for {machineLabel} expires on {comp.ExpiresAt:yyyy-MM-dd}. " +
                                    "Plan your renewal now.");
                    }
                    catch { }
                    comp.Reminder30DaysSentAt = now;
                    sent30++;
                }
            }
            catch (Exception ex)
            {
                // Per-competency failure: skip it for this pass, will be
                // picked up tomorrow if the underlying issue resolves.
                _log.LogError(ex, "CompetencyExpiryWorker: failed for competency {Id}", comp.Id);
            }
        }

        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex)
        {
            _log.LogError(ex, "CompetencyExpiryWorker: failed to persist reminder timestamps");
        }

        _log.LogInformation(
            "CompetencyExpiryWorker pass complete — 30d: {Sent30}, 7d: {Sent7}, expired: {SentExpiry}",
            sent30, sent7, sentExpiry);
    }
}
