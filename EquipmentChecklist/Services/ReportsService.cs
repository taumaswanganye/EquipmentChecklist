using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

/// <summary>
/// Builds the KPI payload behind <c>/Admin/Reports</c>. Everything that the
/// dashboard view renders — KPI cards, trend lines, top-N tables — comes from
/// one call to <see cref="BuildDashboardAsync"/>.
///
/// <para>Design notes:</para>
/// <list type="bullet">
///   <item><description>The service does all the SQL. The view just reads the
///   resulting <see cref="ReportsDashboard"/> and hands the array literals to
///   Chart.js. Keeps the view dumb and the SQL discoverable in one place.</description></item>
///   <item><description>Series are pre-padded to cover EVERY day in the
///   range, even ones with zero events. Otherwise Chart.js draws gaps where
///   a sparse week of submissions looks misleadingly flat.</description></item>
///   <item><description>Date math runs against <c>DateTime.UtcNow</c> — the
///   submissions are stored in UTC. Localising labels is a UI concern.</description></item>
/// </list>
/// </summary>
public class ReportsService
{
    private readonly ApplicationDbContext _db;

    public ReportsService(ApplicationDbContext db) => _db = db;

    /// <summary>
    /// Compute every dashboard figure for the supplied window.
    /// <paramref name="from"/> and <paramref name="to"/> default to "last 30
    /// days" if either is null, matching the existing Reports action.
    /// </summary>
    public async Task<ReportsDashboard> BuildDashboardAsync(DateTime? from, DateTime? to)
    {
        // Same defaults the Reports action has used since day one.
        var fromUtc = (from ?? DateTime.UtcNow.Date.AddDays(-30)).Date;
        var toUtc   = (to   ?? DateTime.UtcNow.Date).Date.AddDays(1).AddTicks(-1);

        // ── Source rows ────────────────────────────────────────────────────
        // Two passes: submissions in the window, and ALL open defect orders
        // (aging is computed against today, not the window).
        var subs = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items)
            .Where(s => s.SubmittedAt >= fromUtc && s.SubmittedAt <= toUtc)
            .ToListAsync();

        var openDefects = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Where(d => d.RepairStatus != RepairStatus.Completed)
            .ToListAsync();

        // ── KPI counters ───────────────────────────────────────────────────
        var kpis = new ReportsKpis
        {
            TotalSubmissions = subs.Count,
            GoCount          = subs.Count(s => s.Status == ChecklistStatus.Go),
            GoButCount       = subs.Count(s => s.Status == ChecklistStatus.GoButRepair24H ||
                                               s.Status == ChecklistStatus.GoTillNextService),
            NoGoCount        = subs.Count(s => s.Status == ChecklistStatus.NoGo),
            RejectedCount    = subs.Count(s => s.Status == ChecklistStatus.Rejected),
            DefectCount      = subs.Sum(s => s.Items.Count(i => i.Status == ItemStatus.Defect)),
            OpenDefects      = openDefects.Count,
            AwaitingParts    = openDefects.Count(d => d.RepairStatus == RepairStatus.AwaitingParts),
            ImmobilisedMachines = await _db.Machines.CountAsync(m => m.IsImmobilised)
        };

        // GO rate: percentage of submissions that came back clean. The most
        // common single number a mine manager will glance at.
        kpis.GoRatePct = kpis.TotalSubmissions == 0
            ? 0
            : Math.Round(100.0 * kpis.GoCount / kpis.TotalSubmissions, 1);

        // ── Daily series (pre-padded so Chart.js doesn't leave gaps) ──────
        var byDay        = BuildDailySeries(fromUtc.Date, toUtc.Date);
        var subsByDay    = subs.GroupBy(s => s.SubmittedAt.Date).ToDictionary(g => g.Key, g => g.Count());
        var defectsByDay = subs.GroupBy(s => s.SubmittedAt.Date)
                               .ToDictionary(g => g.Key,
                                             g => g.Sum(s => s.Items.Count(i => i.Status == ItemStatus.Defect)));

        var submissionTrend = byDay.Select(d => new TrendPoint(
            d, subsByDay.GetValueOrDefault(d, 0))).ToList();

        var defectTrend = byDay.Select(d => new TrendPoint(
            d, defectsByDay.GetValueOrDefault(d, 0))).ToList();

        // ── Top-N tables ───────────────────────────────────────────────────
        // Machines with the most defects — the obvious "what needs attention"
        // list for a maintenance lead.
        var topMachines = subs
            .SelectMany(s => s.Items.Where(i => i.Status == ItemStatus.Defect)
                                    .Select(_ => s.Machine))
            .GroupBy(m => m.Id)
            .Select(g => new MachineLeaderboardRow
            {
                MachineId     = g.Key,
                MachineNumber = g.First().MachineNumber,
                MachineName   = g.First().MachineName,
                DefectCount   = g.Count(),
                Immobilised   = g.First().IsImmobilised
            })
            .OrderByDescending(r => r.DefectCount)
            .Take(10)
            .ToList();

        // Operators with the most submissions — feeds the "who's actually
        // doing pre-use checks" picture.
        var topOperators = subs
            .GroupBy(s => new { s.OperatorId, s.Operator.FullName, s.Operator.EmployeeNumber })
            .Select(g => new OperatorLeaderboardRow
            {
                OperatorId       = g.Key.OperatorId,
                OperatorName     = g.Key.FullName ?? "—",
                EmployeeNumber   = g.Key.EmployeeNumber ?? "",
                TotalSubmissions = g.Count(),
                GoCount          = g.Count(s => s.Status == ChecklistStatus.Go),
                DefectCount      = g.Sum(s => s.Items.Count(i => i.Status == ItemStatus.Defect))
            })
            .OrderByDescending(r => r.TotalSubmissions)
            .Take(10)
            .ToList();

        // Open defect aging — buckets matched to MHSA reporting habits.
        var nowUtc = DateTime.UtcNow;
        var aging = new DefectAging
        {
            UnderOneDay  = openDefects.Count(d => (nowUtc - d.CreatedAt).TotalDays <  1),
            OneToThree   = openDefects.Count(d => (nowUtc - d.CreatedAt).TotalDays >= 1 &&
                                                  (nowUtc - d.CreatedAt).TotalDays <  3),
            ThreeToSeven = openDefects.Count(d => (nowUtc - d.CreatedAt).TotalDays >= 3 &&
                                                  (nowUtc - d.CreatedAt).TotalDays <  7),
            OverSeven    = openDefects.Count(d => (nowUtc - d.CreatedAt).TotalDays >= 7)
        };

        return new ReportsDashboard
        {
            From            = fromUtc,
            To              = toUtc,
            Kpis            = kpis,
            SubmissionTrend = submissionTrend,
            DefectTrend     = defectTrend,
            TopMachines     = topMachines,
            TopOperators    = topOperators,
            Aging           = aging
        };
    }

    /// <summary>Yields every UTC midnight from <paramref name="from"/> to
    /// <paramref name="to"/> inclusive, so the trend series always covers
    /// the whole window — even days where zero submissions came in.</summary>
    private static IEnumerable<DateTime> BuildDailySeries(DateTime from, DateTime to)
    {
        for (var d = from; d <= to; d = d.AddDays(1))
            yield return d;
    }
}

// ── ViewModel shapes ───────────────────────────────────────────────────────
//
// All public so the Razor view can bind to them directly. They're kept in
// the same file because they're only meaningful in concert; splitting them
// out would multiply files without aiding navigation.

public class ReportsDashboard
{
    public DateTime From            { get; set; }
    public DateTime To              { get; set; }
    public ReportsKpis Kpis         { get; set; } = new();
    public List<TrendPoint> SubmissionTrend  { get; set; } = new();
    public List<TrendPoint> DefectTrend      { get; set; } = new();
    public List<MachineLeaderboardRow>  TopMachines  { get; set; } = new();
    public List<OperatorLeaderboardRow> TopOperators { get; set; } = new();
    public DefectAging Aging        { get; set; } = new();
}

public class ReportsKpis
{
    public int    TotalSubmissions   { get; set; }
    public int    GoCount            { get; set; }
    public int    GoButCount         { get; set; }
    public int    NoGoCount          { get; set; }
    public int    RejectedCount      { get; set; }
    public int    DefectCount        { get; set; }
    public int    OpenDefects        { get; set; }
    public int    AwaitingParts      { get; set; }
    public int    ImmobilisedMachines{ get; set; }
    public double GoRatePct          { get; set; }
}

public record TrendPoint(DateTime Date, int Count);

public class MachineLeaderboardRow
{
    public int    MachineId     { get; set; }
    public string MachineNumber { get; set; } = "";
    public string MachineName   { get; set; } = "";
    public int    DefectCount   { get; set; }
    public bool   Immobilised   { get; set; }
}

public class OperatorLeaderboardRow
{
    public string OperatorId       { get; set; } = "";
    public string OperatorName     { get; set; } = "";
    public string EmployeeNumber   { get; set; } = "";
    public int    TotalSubmissions { get; set; }
    public int    GoCount          { get; set; }
    public int    DefectCount      { get; set; }
}

public class DefectAging
{
    /// <summary>Open less than 24h. Healthy.</summary>
    public int UnderOneDay  { get; set; }
    /// <summary>1–3 days. Still inside normal turnaround for parts ordering.</summary>
    public int OneToThree   { get; set; }
    /// <summary>3–7 days. Warrants escalation.</summary>
    public int ThreeToSeven { get; set; }
    /// <summary>Over a week. Audit risk.</summary>
    public int OverSeven    { get; set; }
}
