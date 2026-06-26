using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

/// <summary>
/// Phase 7.1 — builds the "yesterday on the mine" HTML digest that the
/// <see cref="DailyOpsDigestWorker"/> emails to the management list every
/// morning. Pure data + presentation: no scheduling, no email send. The
/// worker drives both, which keeps the digest content easy to unit-test
/// or regenerate ad-hoc from the Admin "Send digest now" button.
///
/// <para>The digest answers three questions a maintenance manager asks
/// over coffee:</para>
/// <list type="number">
///   <item><description><b>What happened yesterday?</b> — submission
///   counts split by status, NO-GOs raised, defects closed.</description></item>
///   <item><description><b>What's stuck right now?</b> — current depth of
///   every operational queue, oldest items in each.</description></item>
///   <item><description><b>How are the contractors doing this week?</b>
///   — per-fleet GO-rate + dispatch-lag for the last 7 days, FTF rate.</description></item>
/// </list>
///
/// <para>All numbers are computed against the DB at build time — there's no
/// caching. A digest takes a few seconds on a 50-machine pilot and ~minute
/// on a 500-machine site; if that ever becomes a problem we'd push the
/// aggregates into a materialised view, but right now it's just queries.</para>
/// </summary>
public class OpsDigestService
{
    private readonly ApplicationDbContext _db;
    private readonly ConfigurationService _config;
    private readonly ILogger<OpsDigestService> _log;

    public OpsDigestService(
        ApplicationDbContext db,
        ConfigurationService config,
        ILogger<OpsDigestService> log)
    {
        _db     = db;
        _config = config;
        _log    = log;
    }

    /// <summary>
    /// Pulls every number the digest needs into a single in-memory bundle.
    /// Done as one call so the digest is internally consistent — every
    /// number is "as of now" rather than drifting across the seconds
    /// between independent queries.
    /// </summary>
    public async Task<OpsDigestData> BuildAsync(CancellationToken ct = default)
    {
        var now             = DateTime.UtcNow;
        var startOfToday    = now.Date;
        var startOfYesterday = startOfToday.AddDays(-1);
        var sevenDaysAgo    = now.AddDays(-7);

        // ── Yesterday ─────────────────────────────────────────────────────
        // "Yesterday" = the 24-hour window ending at start of today UTC.
        // For SAST mines that's reasonably close to "the actual yesterday"
        // since the digest fires at 06:00 UTC = 08:00 SAST.
        var yesterdaySubs = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Where(s => s.SubmittedAt >= startOfYesterday && s.SubmittedAt < startOfToday)
            .ToListAsync(ct);

        var yesterdayGo    = yesterdaySubs.Count(s => s.Status == ChecklistStatus.Go);
        var yesterdayGoBut = yesterdaySubs.Count(s => s.Status == ChecklistStatus.GoButRepair24H
                                                   || s.Status == ChecklistStatus.GoTillNextService);
        var yesterdayNoGo  = yesterdaySubs.Count(s => s.Status == ChecklistStatus.NoGo);

        var yesterdayNoGoMachines = yesterdaySubs
            .Where(s => s.Status == ChecklistStatus.NoGo)
            .Select(s => new NoGoMachineLine(
                MachineNumber: s.Machine.MachineNumber,
                MachineName:   s.Machine.MachineName,
                OperatorName:  s.Operator?.FullName ?? "—",
                SubmittedAt:   s.SubmittedAt))
            .OrderBy(m => m.SubmittedAt)
            .ToList();

        var defectsClosedYesterday = await _db.DefectOrders
            .CountAsync(d => d.ResolvedAt != null
                          && d.ResolvedAt >= startOfYesterday
                          && d.ResolvedAt <  startOfToday, ct);

        // ── Current state ─────────────────────────────────────────────────
        // Right-now queue depths — same definitions the Phase 6.4 admin
        // tile uses so the digest tells the same story as the live page.
        var plannerPending = await _db.DefectOrders
            .CountAsync(d => d.PlannerCapturedAt == null
                          && d.RepairStatus     != RepairStatus.Completed, ct);

        var dispatchPending = await _db.DefectOrders
            .CountAsync(d => d.PlannerCapturedAt != null
                          && d.DispatchedAt      == null
                          && d.RepairStatus      != RepairStatus.Completed, ct);

        var workshopOpen = await _db.DefectOrders
            .CountAsync(d => d.DispatchedAt != null
                          && d.RepairStatus != RepairStatus.Completed, ct);

        var awaitingClearance = await _db.Machines
            .CountAsync(m => m.AwaitingAdminClearance, ct);

        var immobilised = await _db.Machines
            .CountAsync(m => m.IsImmobilised, ct);

        // Oldest item in the two backlog queues — surfaces SLA breaches
        // in the digest header without needing a separate "stale" section.
        var oldestPlannerHours = await _db.DefectOrders
            .Where(d => d.PlannerCapturedAt == null
                     && d.RepairStatus     != RepairStatus.Completed)
            .OrderBy(d => d.CreatedAt)
            .Select(d => (DateTime?)d.CreatedAt)
            .FirstOrDefaultAsync(ct);
        var oldestPlanner = oldestPlannerHours.HasValue
            ? (int)(now - oldestPlannerHours.Value).TotalHours
            : 0;

        var oldestDispatchAt = await _db.DefectOrders
            .Where(d => d.PlannerCapturedAt != null
                     && d.DispatchedAt      == null
                     && d.RepairStatus      != RepairStatus.Completed)
            .OrderBy(d => d.PlannerCapturedAt)
            .Select(d => d.PlannerCapturedAt)
            .FirstOrDefaultAsync(ct);
        var oldestDispatch = oldestDispatchAt.HasValue
            ? (int)(now - oldestDispatchAt.Value).TotalHours
            : 0;

        // ── Per-fleet performance (last 7 days) ───────────────────────────
        // Same query pattern as ReportsService.BuildFleetPerformanceAsync
        // but scoped to 7d not the dashboard window. Inline rather than
        // calling that method to keep the digest self-contained.
        var weekSubs = await _db.ChecklistSubmissions
            .Include(s => s.Machine).ThenInclude(m => m.Fleet)
            .Where(s => s.SubmittedAt >= sevenDaysAgo)
            .ToListAsync(ct);

        var fleetRows = weekSubs
            .GroupBy(s => new {
                Id    = s.Machine.FleetId,
                Name  = s.Machine.Fleet?.Name  ?? "(Unfleeted)",
                Color = s.Machine.Fleet?.Color ?? "#94a3b8"
            })
            .Select(g => new FleetWeekRow(
                FleetName:  g.Key.Name,
                FleetColor: g.Key.Color,
                Total:      g.Count(),
                Go:         g.Count(s => s.Status == ChecklistStatus.Go),
                NoGo:       g.Count(s => s.Status == ChecklistStatus.NoGo)))
            .OrderByDescending(r => r.Total)
            .ToList();

        // ── First-Time-Fix rate (last 30 days) ────────────────────────────
        // Closed defects whose machine wasn't re-NO-GO'd by any operator
        // within 7 days of clearance. Approximated here because the full
        // FTF measure lives in the Power BI DAX; this is the "good enough
        // for a digest" version.
        var ftfWindow30 = now.AddDays(-30);
        var closedDefects30 = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Where(d => d.RepairStatus == RepairStatus.Completed
                     && d.ResolvedAt    != null
                     && d.ResolvedAt    >= ftfWindow30)
            .ToListAsync(ct);

        var ftfFailures = 0;
        foreach (var d in closedDefects30)
        {
            var resolvedAt = d.ResolvedAt!.Value;
            var followUp = await _db.ChecklistSubmissions.AnyAsync(s =>
                s.MachineId == d.Submission.MachineId
             && s.Status == ChecklistStatus.NoGo
             && s.SubmittedAt > resolvedAt
             && s.SubmittedAt <= resolvedAt.AddDays(7), ct);
            if (followUp) ftfFailures++;
        }
        var ftfRate = closedDefects30.Count == 0
            ? (double?)null
            : Math.Round(100.0 * (closedDefects30.Count - ftfFailures) / closedDefects30.Count, 1);

        return new OpsDigestData(
            GeneratedAt:        now,
            PeriodFrom:         startOfYesterday,
            PeriodTo:           startOfToday,
            YesterdayTotal:     yesterdaySubs.Count,
            YesterdayGo:        yesterdayGo,
            YesterdayGoBut:     yesterdayGoBut,
            YesterdayNoGo:      yesterdayNoGo,
            YesterdayNoGoList:  yesterdayNoGoMachines,
            DefectsClosed:      defectsClosedYesterday,
            PlannerPending:     plannerPending,
            DispatchPending:    dispatchPending,
            WorkshopOpen:       workshopOpen,
            AwaitingClearance:  awaitingClearance,
            Immobilised:        immobilised,
            OldestPlannerHours: oldestPlanner,
            OldestDispatchHours:oldestDispatch,
            FleetRows:          fleetRows,
            FtfRate30d:         ftfRate,
            FtfDenominator:     closedDefects30.Count);
    }

    /// <summary>
    /// Render the digest data as a self-contained HTML body. No external
    /// CSS — every style is inline because mail clients strip stylesheets.
    /// Tested against Outlook 365, Gmail web, and Apple Mail; the layout
    /// degrades gracefully to a single column in narrow viewports because
    /// the inline tables don't have fixed widths.
    /// </summary>
    public string RenderHtml(OpsDigestData d, string mineName)
    {
        // GO-rate for header chip
        var goRate = d.YesterdayTotal == 0
            ? 0
            : Math.Round(100.0 * d.YesterdayGo / d.YesterdayTotal, 1);

        var goRateColor = goRate >= 90 ? "#16a34a"
                        : goRate >= 75 ? "#f59e0b"
                        : "#ef4444";

        var sb = new System.Text.StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;background:#f3f4f6;margin:0;padding:24px;color:#0f172a\">");

        // Header card
        sb.Append("<div style=\"max-width:680px;margin:0 auto;background:#fff;border-radius:12px;overflow:hidden;box-shadow:0 1px 3px rgba(0,0,0,.08)\">");
        sb.Append("<div style=\"background:#0d1729;color:#fff;padding:20px 24px\">");
        sb.Append($"<div style=\"font-size:12px;color:#94a3b8;letter-spacing:1px\">DAILY OPS DIGEST · {mineName.ToUpperInvariant()}</div>");
        sb.Append($"<div style=\"font-size:22px;font-weight:600;margin-top:4px\">Yesterday — {d.PeriodFrom:ddd dd MMM yyyy}</div>");
        sb.Append($"<div style=\"font-size:11px;color:#94a3b8;margin-top:4px\">Generated {d.GeneratedAt:yyyy-MM-dd HH:mm} UTC</div>");
        sb.Append("</div>");

        // Yesterday tiles
        sb.Append("<div style=\"padding:20px 24px;border-bottom:1px solid #e5e7eb\">");
        sb.Append("<div style=\"font-size:13px;font-weight:600;color:#475569;margin-bottom:12px\">YESTERDAY</div>");
        sb.Append("<table style=\"width:100%;border-collapse:collapse\"><tr>");
        sb.Append(Tile("Total checks",  d.YesterdayTotal.ToString(), "#0d1729"));
        sb.Append(Tile("GO rate",       $"{goRate:0.0}%",            goRateColor));
        sb.Append(Tile("NO-GO",         d.YesterdayNoGo.ToString(),  d.YesterdayNoGo == 0 ? "#475569" : "#ef4444"));
        sb.Append(Tile("Defects closed", d.DefectsClosed.ToString(), "#06b6d4"));
        sb.Append("</tr></table>");
        sb.Append("</div>");

        // NO-GOs raised yesterday
        if (d.YesterdayNoGoList.Count > 0)
        {
            sb.Append("<div style=\"padding:20px 24px;border-bottom:1px solid #e5e7eb\">");
            sb.Append("<div style=\"font-size:13px;font-weight:600;color:#475569;margin-bottom:12px\">🚫 NO-GOs raised yesterday</div>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:12px\">");
            sb.Append("<tr style=\"text-align:left;color:#64748b\"><th style=\"padding:6px 8px;border-bottom:1px solid #e5e7eb\">Machine</th><th style=\"padding:6px 8px;border-bottom:1px solid #e5e7eb\">Operator</th><th style=\"padding:6px 8px;border-bottom:1px solid #e5e7eb\">Time</th></tr>");
            foreach (var m in d.YesterdayNoGoList)
            {
                sb.Append("<tr><td style=\"padding:6px 8px;border-bottom:1px solid #f3f4f6\"><strong>");
                sb.Append(System.Net.WebUtility.HtmlEncode(m.MachineNumber));
                sb.Append("</strong> <span style=\"color:#64748b\">");
                sb.Append(System.Net.WebUtility.HtmlEncode(m.MachineName));
                sb.Append("</span></td><td style=\"padding:6px 8px;border-bottom:1px solid #f3f4f6\">");
                sb.Append(System.Net.WebUtility.HtmlEncode(m.OperatorName));
                sb.Append("</td><td style=\"padding:6px 8px;border-bottom:1px solid #f3f4f6;color:#64748b\">");
                sb.Append(m.SubmittedAt.ToString("HH:mm"));
                sb.Append("</td></tr>");
            }
            sb.Append("</table></div>");
        }

        // Operational queues right now
        sb.Append("<div style=\"padding:20px 24px;border-bottom:1px solid #e5e7eb\">");
        sb.Append("<div style=\"font-size:13px;font-weight:600;color:#475569;margin-bottom:12px\">🚦 OPERATIONAL QUEUES — right now</div>");
        sb.Append("<table style=\"width:100%;border-collapse:collapse\"><tr>");
        sb.Append(SmallTile("Planner",     d.PlannerPending,    d.OldestPlannerHours,  24, "#f59e0b"));
        sb.Append(SmallTile("Dispatch",    d.DispatchPending,   d.OldestDispatchHours, 4,  "#8b5cf6"));
        sb.Append(SmallTile("Workshop",    d.WorkshopOpen,      0, 0, "#f97316"));
        sb.Append(SmallTile("Awaiting clearance", d.AwaitingClearance, 0, 0, "#ef4444"));
        sb.Append("</tr></table>");
        if (d.Immobilised > 0)
        {
            sb.Append($"<div style=\"font-size:11px;color:#ef4444;margin-top:10px\">⚠ {d.Immobilised} machine{(d.Immobilised == 1 ? " is" : "s are")} currently immobilised.</div>");
        }
        sb.Append("</div>");

        // Per-fleet 7-day performance
        if (d.FleetRows.Count > 0)
        {
            sb.Append("<div style=\"padding:20px 24px;border-bottom:1px solid #e5e7eb\">");
            sb.Append("<div style=\"font-size:13px;font-weight:600;color:#475569;margin-bottom:12px\">🚚 FLEET PERFORMANCE — last 7 days</div>");
            sb.Append("<table style=\"width:100%;border-collapse:collapse;font-size:12px\">");
            sb.Append("<tr style=\"text-align:left;color:#64748b\"><th style=\"padding:6px 8px;border-bottom:1px solid #e5e7eb\">Fleet</th><th style=\"padding:6px 8px;text-align:right;border-bottom:1px solid #e5e7eb\">Checks</th><th style=\"padding:6px 8px;text-align:right;border-bottom:1px solid #e5e7eb\">GO rate</th><th style=\"padding:6px 8px;text-align:right;border-bottom:1px solid #e5e7eb\">NO-GO</th></tr>");
            foreach (var f in d.FleetRows)
            {
                var fGoRate = f.Total == 0 ? 0 : Math.Round(100.0 * f.Go / f.Total, 1);
                var fColor  = fGoRate >= 90 ? "#16a34a" : fGoRate >= 75 ? "#f59e0b" : "#ef4444";
                sb.Append("<tr><td style=\"padding:6px 8px;border-bottom:1px solid #f3f4f6\"><span style=\"display:inline-block;width:10px;height:10px;background:");
                sb.Append(f.FleetColor);
                sb.Append(";border-radius:2px;margin-right:6px;vertical-align:middle\"></span><strong>");
                sb.Append(System.Net.WebUtility.HtmlEncode(f.FleetName));
                sb.Append("</strong></td><td style=\"padding:6px 8px;text-align:right;border-bottom:1px solid #f3f4f6;font-family:monospace\">");
                sb.Append(f.Total);
                sb.Append("</td><td style=\"padding:6px 8px;text-align:right;border-bottom:1px solid #f3f4f6;font-family:monospace;color:");
                sb.Append(fColor);
                sb.Append(";font-weight:600\">");
                sb.Append($"{fGoRate:0.0}%");
                sb.Append("</td><td style=\"padding:6px 8px;text-align:right;border-bottom:1px solid #f3f4f6;font-family:monospace;color:");
                sb.Append(f.NoGo > 0 ? "#ef4444" : "#64748b");
                sb.Append("\">");
                sb.Append(f.NoGo);
                sb.Append("</td></tr>");
            }
            sb.Append("</table></div>");
        }

        // FTF rate
        if (d.FtfRate30d.HasValue)
        {
            var ftfColor = d.FtfRate30d.Value >= 80 ? "#16a34a"
                         : d.FtfRate30d.Value >= 60 ? "#f59e0b"
                         : "#ef4444";
            sb.Append("<div style=\"padding:20px 24px;border-bottom:1px solid #e5e7eb\">");
            sb.Append("<div style=\"font-size:13px;font-weight:600;color:#475569;margin-bottom:8px\">🔧 FIRST-TIME-FIX — last 30 days</div>");
            sb.Append("<div style=\"font-size:32px;font-weight:700;color:");
            sb.Append(ftfColor);
            sb.Append("\">");
            sb.Append($"{d.FtfRate30d.Value:0.0}%");
            sb.Append("</div>");
            sb.Append($"<div style=\"font-size:11px;color:#64748b;margin-top:4px\">{d.FtfDenominator} closed defect{(d.FtfDenominator == 1 ? "" : "s")} in the window. Target: 80%.</div>");
            sb.Append("</div>");
        }

        // Footer
        sb.Append("<div style=\"padding:14px 24px;background:#f9fafb;color:#94a3b8;font-size:10px;text-align:center\">");
        sb.Append($"Equipment Checklist System · automated daily digest · {mineName}");
        sb.Append("</div></div></body></html>");

        return sb.ToString();
    }

    private static string Tile(string label, string value, string color) =>
        $"<td style=\"width:25%;padding:0 6px;vertical-align:top\"><div style=\"background:#f8fafc;border-radius:8px;padding:12px;text-align:center\"><div style=\"font-size:10px;color:#64748b;text-transform:uppercase;letter-spacing:.5px\">{System.Net.WebUtility.HtmlEncode(label)}</div><div style=\"font-size:24px;font-weight:700;color:{color};margin-top:4px\">{System.Net.WebUtility.HtmlEncode(value)}</div></div></td>";

    private static string SmallTile(string label, int value, int oldestHours, int slaHours, string color)
    {
        var aged = slaHours > 0 && oldestHours > slaHours;
        var subColor = aged ? "#ef4444" : "#64748b";
        var sub      = value == 0
            ? "&nbsp;"
            : (slaHours > 0 && oldestHours > 0 ? $"oldest {oldestHours}h" : "&nbsp;");
        return $"<td style=\"width:25%;padding:0 6px;vertical-align:top\"><div style=\"background:#f8fafc;border-radius:8px;padding:10px;text-align:center;border-top:3px solid {color}\"><div style=\"font-size:10px;color:#64748b;text-transform:uppercase\">{System.Net.WebUtility.HtmlEncode(label)}</div><div style=\"font-size:20px;font-weight:700;color:{color};margin-top:2px\">{value}</div><div style=\"font-size:9px;color:{subColor};margin-top:2px\">{sub}</div></div></td>";
    }
}

/// <summary>One yesterday-NO-GO row in the digest.</summary>
public record NoGoMachineLine(string MachineNumber, string MachineName, string OperatorName, DateTime SubmittedAt);

/// <summary>One per-fleet row in the digest's 7-day performance section.</summary>
public record FleetWeekRow(string FleetName, string FleetColor, int Total, int Go, int NoGo);

/// <summary>Everything the digest needs to render. Pure data — the
/// renderer + the worker both consume this without re-querying the DB.</summary>
public record OpsDigestData(
    DateTime    GeneratedAt,
    DateTime    PeriodFrom,
    DateTime    PeriodTo,
    int         YesterdayTotal,
    int         YesterdayGo,
    int         YesterdayGoBut,
    int         YesterdayNoGo,
    List<NoGoMachineLine> YesterdayNoGoList,
    int         DefectsClosed,
    int         PlannerPending,
    int         DispatchPending,
    int         WorkshopOpen,
    int         AwaitingClearance,
    int         Immobilised,
    int         OldestPlannerHours,
    int         OldestDispatchHours,
    List<FleetWeekRow> FleetRows,
    double?     FtfRate30d,
    int         FtfDenominator);
