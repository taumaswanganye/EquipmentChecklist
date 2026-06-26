using EquipmentChecklist.Data;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

/// <summary>
/// Phase 7.1 — daily background worker that builds + emails the operations
/// digest to the management list every morning.
///
/// <para>Schedule: runs once a day at the hour specified by the
/// <c>Ops.DailyDigest.HourUtc</c> setting (default 06:00 UTC ≈ 08:00 SAST).
/// Same wake-up pattern as <see cref="CompetencyExpiryWorker"/> — compute
/// time-to-next-target-hour, await, loop daily.</para>
///
/// <para>Recipients come from the comma-separated <c>Ops.DailyDigest.Recipients</c>
/// setting. If that's empty, falls back to <c>Mine.MineManagerEmail</c> +
/// <c>Mine.SheOfficerEmail</c> so the digest doesn't silently disappear
/// before the admin has wired it up.</para>
///
/// <para>The whole worker is no-op'd by <c>Ops.DailyDigest.Enabled = false</c>.
/// We still wake up daily to re-check the flag (a flip mid-day takes effect
/// tomorrow morning).</para>
///
/// <para>Idempotency: an <c>ops.daily_digest_sent</c> audit row is written
/// each pass with the recipient count + send-success count in the payload.
/// Re-runs the same day (server restart at 06:01) will re-send — that's
/// acceptable for an ops digest (it's informational, not transactional)
/// and noticeable enough that a duplicate gets flagged operationally.</para>
/// </summary>
public class DailyOpsDigestWorker : BackgroundService
{
    private readonly IServiceProvider                _services;
    private readonly ILogger<DailyOpsDigestWorker>   _log;

    public DailyOpsDigestWorker(IServiceProvider services, ILogger<DailyOpsDigestWorker> log)
    {
        _services = services;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("DailyOpsDigestWorker starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            int hour;
            try
            {
                using var scope  = _services.CreateScope();
                var config       = scope.ServiceProvider.GetRequiredService<ConfigurationService>();
                var hourStr      = await config.GetAsync("Ops.DailyDigest.HourUtc", stoppingToken);
                hour             = int.TryParse(hourStr, out var h) && h >= 0 && h <= 23 ? h : 6;
            }
            catch
            {
                hour = 6;
            }

            var now      = DateTime.UtcNow;
            var nextRun  = new DateTime(now.Year, now.Month, now.Day, hour, 0, 0, DateTimeKind.Utc);
            if (nextRun <= now) nextRun = nextRun.AddDays(1);
            var wait = nextRun - now;
            _log.LogInformation("DailyOpsDigestWorker next run at {NextRun} (in {Wait})", nextRun, wait);

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Daily-digest failures must not crash the host.
                _log.LogError(ex, "DailyOpsDigestWorker pass failed");
            }
        }
    }

    /// <summary>
    /// Public entrypoint — invoked by the schedule loop above AND by the
    /// Admin "Send digest now" button. Returns the number of recipients
    /// successfully emailed (caller can surface this back to the admin).
    /// </summary>
    public async Task<DigestSendResult> RunOnceAsync(CancellationToken ct = default)
    {
        using var scope   = _services.CreateScope();
        var config        = scope.ServiceProvider.GetRequiredService<ConfigurationService>();
        var digestService = scope.ServiceProvider.GetRequiredService<OpsDigestService>();
        var email         = scope.ServiceProvider.GetRequiredService<EmailService>();
        var audit         = scope.ServiceProvider.GetRequiredService<AuditService>();
        var mine          = scope.ServiceProvider.GetRequiredService<MineSettings>();

        // Honour the kill switch.
        var enabled = (await config.GetAsync("Ops.DailyDigest.Enabled", ct))?.Trim().ToLowerInvariant();
        if (enabled == "false" || enabled == "0" || enabled == "no")
        {
            _log.LogInformation("DailyOpsDigestWorker: disabled via setting; skipping");
            return new DigestSendResult(Sent: 0, Failed: 0, Skipped: true, Recipients: new List<string>());
        }

        var recipients = await ResolveRecipientsAsync(config, ct);
        if (recipients.Count == 0)
        {
            _log.LogWarning("DailyOpsDigestWorker: no recipients configured (set Ops.DailyDigest.Recipients or Mine.MineManagerEmail)");
            return new DigestSendResult(Sent: 0, Failed: 0, Skipped: true, Recipients: new List<string>());
        }

        // Build the digest data + render the HTML body ONCE (not per
        // recipient) — it's the same content for everyone.
        var data    = await digestService.BuildAsync(ct);
        var mineName = string.IsNullOrEmpty(mine.Name) ? "Equipment Checklist" : mine.Name;
        var html    = digestService.RenderHtml(data, mineName);
        var subject = $"[{mineName}] Daily ops digest — {data.PeriodFrom:ddd dd MMM yyyy}";

        int sent = 0, failed = 0;
        foreach (var addr in recipients)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ok = await email.SendOpsDigestAsync(
                    toEmail: addr, toName: addr, subject: subject, htmlBody: html);
                if (ok) sent++; else failed++;
            }
            catch (Exception ex)
            {
                failed++;
                _log.LogError(ex, "DailyOpsDigestWorker: failed to send to {Email}", addr);
            }
        }

        // Audit row regardless of outcome so the digest sends are visible
        // in /Admin/Audit even if every email failed (SMTP down etc.).
        await audit.LogAsync(
            action:     "ops.daily_digest_sent",
            payload:    new {
                period_from         = data.PeriodFrom,
                period_to           = data.PeriodTo,
                recipients          = recipients.Count,
                sent,
                failed,
                yesterday_total     = data.YesterdayTotal,
                yesterday_nogo      = data.YesterdayNoGo,
                planner_pending     = data.PlannerPending,
                dispatch_pending    = data.DispatchPending
            }, ct: ct);

        _log.LogInformation(
            "DailyOpsDigestWorker: sent={Sent} failed={Failed} recipients={Count}",
            sent, failed, recipients.Count);

        return new DigestSendResult(Sent: sent, Failed: failed, Skipped: false, Recipients: recipients);
    }

    /// <summary>
    /// Build the final recipient list. Priority order:
    ///   1. <c>Ops.DailyDigest.Recipients</c> — comma/semicolon-separated.
    ///   2. Fallback: <c>Mine.MineManagerEmail</c> + <c>Mine.SheOfficerEmail</c>
    ///      so the digest doesn't silently no-op on a fresh install.
    /// Duplicates and whitespace are normalised away.
    /// </summary>
    private static async Task<List<string>> ResolveRecipientsAsync(
        ConfigurationService config, CancellationToken ct)
    {
        var explicitList = await config.GetAsync("Ops.DailyDigest.Recipients", ct);
        var raw = new List<string>();

        if (!string.IsNullOrWhiteSpace(explicitList))
        {
            raw.AddRange(explicitList.Split(new[] { ',', ';', '\n' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else
        {
            var mgr = await config.GetAsync("Mine.MineManagerEmail", ct);
            var she = await config.GetAsync("Mine.SheOfficerEmail", ct);
            if (!string.IsNullOrWhiteSpace(mgr)) raw.Add(mgr.Trim());
            if (!string.IsNullOrWhiteSpace(she)) raw.Add(she.Trim());
        }

        return raw
            .Where(r => r.Contains('@'))
            .Select(r => r.ToLowerInvariant())
            .Distinct()
            .ToList();
    }
}

/// <summary>What the worker reports back when invoked manually. The Admin
/// "Send digest now" action uses this to render a success message.</summary>
public record DigestSendResult(int Sent, int Failed, bool Skipped, List<string> Recipients);
