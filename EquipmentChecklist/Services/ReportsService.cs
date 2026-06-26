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

        // ── Competency section (mirrors Power BI Page 5) ──────────────────
        // Aggregates over OperatorCompetencies + ASP.NET user table to
        // produce the same numbers the SHE-officer dashboard renders. Kept
        // here rather than calling CompetencyService.GetRosterAsync because
        // we want the aggregate buckets (compliance %, lapsed count) which
        // the roster doesn't expose directly.
        var competency = await BuildCompetencySectionAsync();

        // ── Configuration / policy audit section (Power BI Page 6) ────────
        var configAudit = await BuildConfigAuditSectionAsync();

        // ── Phase 6.4 + 6.5 — operational queues + workshop load ──────────
        // Both are computed against CURRENT state (not the date window),
        // because the question "what's stuck right now?" doesn't change
        // based on the From/To filter.
        var operationalQueues = await BuildOperationalQueuesSectionAsync();
        var artisanLoad       = await BuildArtisanLoadAsync();

        // ── Phase 6.10 — fleet performance split ──────────────────────────
        // Scoped to the same window as the other KPIs because the
        // question "how is each contractor doing?" is naturally a
        // period question, not a current-state one.
        var fleetPerformance = await BuildFleetPerformanceAsync(subs, fromUtc, toUtc);

        return new ReportsDashboard
        {
            From              = fromUtc,
            To                = toUtc,
            Kpis              = kpis,
            SubmissionTrend   = submissionTrend,
            DefectTrend       = defectTrend,
            TopMachines       = topMachines,
            TopOperators      = topOperators,
            Aging             = aging,
            Competency        = competency,
            ConfigAudit       = configAudit,
            OperationalQueues = operationalQueues,
            ArtisanLoad       = artisanLoad,
            FleetPerformance  = fleetPerformance
        };
    }

    /// <summary>
    /// Phase 6.10 — per-fleet performance split. Buckets submissions and
    /// defects by the operating machine's Fleet. Machines without a fleet
    /// (or operating under "Belfast Direct"/floating) go into a synthetic
    /// "(Unfleeted)" group so the totals match the period KPIs.
    /// </summary>
    private async Task<List<FleetPerformanceRow>> BuildFleetPerformanceAsync(
        List<ChecklistSubmission> windowSubs,
        DateTime fromUtc,
        DateTime toUtc)
    {
        // Load every fleet once (small table) + active machines so we can
        // count machines per fleet even for fleets that had no submissions
        // this window — the fleet still exists, just had zero activity.
        var fleets = await _db.Fleets
            .Where(f => f.IsActive)
            .OrderBy(f => f.Name)
            .ToListAsync();

        var machinesByFleet = await _db.Machines
            .Where(m => m.IsActive)
            .GroupBy(m => m.FleetId)
            .Select(g => new { FleetId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.FleetId ?? -1, x => x.Count);

        // Open defects by fleet — current state, not window-scoped (the
        // table is meant to show "where do active issues live right now?"
        // alongside the period throughput numbers).
        var openByFleet = await (
            from d in _db.DefectOrders
            join s in _db.ChecklistSubmissions on d.SubmissionId equals s.Id
            join m in _db.Machines             on s.MachineId    equals m.Id
            where d.RepairStatus != RepairStatus.Completed
            group d by m.FleetId into g
            select new { FleetId = g.Key, Count = g.Count() }
        ).ToDictionaryAsync(x => x.FleetId ?? -1, x => x.Count);

        // Avg resolution time — completed defects in the window, dispatched
        // → resolved. Pulled raw + diff in memory for provider portability
        // (same reason as the Artisan-load query).
        var closures = await (
            from d in _db.DefectOrders
            join s in _db.ChecklistSubmissions on d.SubmissionId equals s.Id
            join m in _db.Machines             on s.MachineId    equals m.Id
            where d.RepairStatus == RepairStatus.Completed
               && d.ResolvedAt != null
               && d.ResolvedAt >= fromUtc && d.ResolvedAt <= toUtc
               && d.DispatchedAt != null
            select new {
                FleetId      = m.FleetId,
                DispatchedAt = d.DispatchedAt,
                ResolvedAt   = d.ResolvedAt
            }
        ).ToListAsync();

        var avgResByFleet = closures
            .GroupBy(c => c.FleetId ?? -1)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => (x.ResolvedAt!.Value - x.DispatchedAt!.Value).TotalHours).Average());

        // Group the in-memory windowSubs by the machine's FleetId.
        var subsByFleet = windowSubs
            .GroupBy(s => s.Machine.FleetId ?? -1)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rows = new List<FleetPerformanceRow>();

        foreach (var f in fleets)
        {
            var grp        = subsByFleet.TryGetValue(f.Id, out var g) ? g : new List<ChecklistSubmission>();
            var total      = grp.Count;
            var go         = grp.Count(s => s.Status == ChecklistStatus.Go);
            var gobut      = grp.Count(s => s.Status == ChecklistStatus.GoButRepair24H
                                         || s.Status == ChecklistStatus.GoTillNextService);
            var nogo       = grp.Count(s => s.Status == ChecklistStatus.NoGo);

            rows.Add(new FleetPerformanceRow
            {
                FleetId          = f.Id,
                FleetName        = f.Name,
                FleetColor       = string.IsNullOrEmpty(f.Color) ? "#475569" : f.Color,
                MachineCount     = machinesByFleet.TryGetValue(f.Id, out var mc) ? mc : 0,
                Submissions      = total,
                GoCount          = go,
                GoButCount       = gobut,
                NoGoCount        = nogo,
                GoRatePct        = total == 0 ? 0 : Math.Round(100.0 * go / total, 1),
                OpenDefects      = openByFleet.TryGetValue(f.Id, out var od) ? od : 0,
                AvgResolutionHrs = avgResByFleet.TryGetValue(f.Id, out var ar) ? Math.Round(ar, 1) : 0
            });
        }

        // Synthetic "(Unfleeted)" row for machines with FleetId = null.
        // We compute it whether or not there are any so an empty zero-row
        // doesn't suddenly appear / disappear between page loads.
        var unfleetSubs   = subsByFleet.TryGetValue(-1, out var u) ? u : new List<ChecklistSubmission>();
        var unfleetTotal  = unfleetSubs.Count;
        if (unfleetTotal > 0 || (machinesByFleet.TryGetValue(-1, out var unfleetMc) && unfleetMc > 0))
        {
            var unfleetGo  = unfleetSubs.Count(s => s.Status == ChecklistStatus.Go);
            rows.Add(new FleetPerformanceRow
            {
                FleetId          = null,
                FleetName        = "(Unfleeted)",
                FleetColor       = "#94a3b8",
                MachineCount     = machinesByFleet.TryGetValue(-1, out var muc) ? muc : 0,
                Submissions      = unfleetTotal,
                GoCount          = unfleetGo,
                GoButCount       = unfleetSubs.Count(s => s.Status == ChecklistStatus.GoButRepair24H
                                                       || s.Status == ChecklistStatus.GoTillNextService),
                NoGoCount        = unfleetSubs.Count(s => s.Status == ChecklistStatus.NoGo),
                GoRatePct        = unfleetTotal == 0 ? 0 : Math.Round(100.0 * unfleetGo / unfleetTotal, 1),
                OpenDefects      = openByFleet.TryGetValue(-1, out var uod) ? uod : 0,
                AvgResolutionHrs = avgResByFleet.TryGetValue(-1, out var uar) ? Math.Round(uar, 1) : 0
            });
        }

        return rows;
    }

    /// <summary>
    /// Phase 6.4 — depth + age of every operational queue in the workflow.
    /// All numbers are "right now", not aggregated against the window —
    /// when an admin looks at this tile they want to know what's stuck
    /// at this instant, not what the average was last month.
    /// </summary>
    private async Task<OperationalQueuesSection> BuildOperationalQueuesSectionAsync()
    {
        var now = DateTime.UtcNow;

        // Planner queue — defects awaiting capture.
        var plannerPending = await _db.DefectOrders
            .Where(d => d.PlannerCapturedAt == null
                     && d.RepairStatus     != RepairStatus.Completed)
            .Select(d => new { d.CreatedAt })
            .ToListAsync();

        // Planner submissions queue (Phase 4.B) — clean GO + signed-off
        // GO-BUT awaiting shift-log capture. 48h window matches the
        // queue's own filter on /Planner.
        var captureCutoff = now.AddHours(-48);
        var plannerSubsPending = await _db.ChecklistSubmissions
            .CountAsync(s => s.CapturedAt == null
                          && s.SubmittedAt >= captureCutoff
                          && (s.Status == ChecklistStatus.Go
                              || (s.Status == ChecklistStatus.GoButRepair24H
                                  && s.SupervisorSignedAt != null)
                              || (s.Status == ChecklistStatus.GoTillNextService
                                  && s.SupervisorSignedAt != null)));

        // Control Room queue — captured-by-Planner but not yet dispatched.
        var dispatchPending = await _db.DefectOrders
            .Where(d => d.PlannerCapturedAt != null
                     && d.DispatchedAt      == null
                     && d.RepairStatus      != RepairStatus.Completed)
            .Select(d => new { d.PlannerCapturedAt })
            .ToListAsync();

        // Phase 7.6 — Control Room acknowledge inbox depth.
        var ackCutoff = now.AddHours(-48);
        var acknowledgeInbox = await _db.ChecklistSubmissions
            .CountAsync(s => s.AcknowledgedAt == null
                          && s.SubmittedAt >= ackCutoff
                          && s.SupervisorSignedAt != null
                          && (s.Status == ChecklistStatus.Go
                              || s.Status == ChecklistStatus.GoButRepair24H
                              || s.Status == ChecklistStatus.GoTillNextService));

        // Workshop queue — dispatched and in-progress (or awaiting parts).
        var workshopOpen = await _db.DefectOrders
            .CountAsync(d => d.DispatchedAt != null
                          && d.RepairStatus != RepairStatus.Completed);

        // Admin clearance queue — machines artisan finished but admin
        // hasn't signed off yet (back-to-service gate).
        var awaitingClearance = await _db.Machines
            .CountAsync(m => m.AwaitingAdminClearance);

        // Supervisor queue — submissions awaiting supervisor approval.
        // Phase 7.5 tightened this to also include unapproved clean GO
        // (per the user's strict-approval reading of the no-defect-route
        // spec). NO-GOs still go straight to Planner without supervisor
        // sign-off (Phase 4.7 awareness-only model).
        // 48h window matches the Supervisor page's own Quick Approve query.
        var supCutoff = now.AddHours(-48);
        var supervisorPending = await _db.ChecklistSubmissions
            .CountAsync(s => s.SupervisorSignedAt == null
                          && (s.Status == ChecklistStatus.GoButRepair24H
                              || s.Status == ChecklistStatus.GoTillNextService
                              || (s.Status == ChecklistStatus.Go && s.SubmittedAt >= supCutoff)));

        // Age of the oldest items in the two highest-friction queues —
        // tells the admin "is the queue old or is it freshly arrived?".
        int oldestPlanner  = plannerPending.Count == 0
                                ? 0
                                : (int)plannerPending.Max(d => (now - d.CreatedAt).TotalHours);
        int oldestDispatch = dispatchPending.Count == 0 || !dispatchPending.Any(d => d.PlannerCapturedAt.HasValue)
                                ? 0
                                : (int)dispatchPending
                                       .Where(d => d.PlannerCapturedAt.HasValue)
                                       .Max(d => (now - d.PlannerCapturedAt!.Value).TotalHours);

        return new OperationalQueuesSection
        {
            PlannerPendingDefects       = plannerPending.Count,
            PlannerPendingSubmissions   = plannerSubsPending,
            ControlRoomQueue            = dispatchPending.Count,
            ControlRoomAcknowledgeInbox = acknowledgeInbox,
            WorkshopOpenDefects         = workshopOpen,
            AwaitingAdminClearance      = awaitingClearance,
            SupervisorPendingSignOff    = supervisorPending,
            OldestPlannerDefectHours    = oldestPlanner,
            OldestDispatchHours         = oldestDispatch
        };
    }

    /// <summary>
    /// Phase 6.5 — per-Artisan workshop load. For every Mechanic-role
    /// user (active), compute current open-defect load + 7-day throughput
    /// + average closure time. The view sorts descending by OpenLoad so
    /// the over-loaded artisans surface at the top.
    /// </summary>
    private async Task<List<ArtisanLoadRow>> BuildArtisanLoadAsync()
    {
        var since7d = DateTime.UtcNow.AddDays(-7);

        // Pull every active Mechanic via the IdentityUserRole table.
        // Two-step rather than a single join projection so .Include
        // attaches cleanly — Include after a select(u) projection is
        // unreliable across EF Core versions.
        var mechanicRoleId = await _db.Roles
            .Where(r => r.Name == "Mechanic")
            .Select(r => r.Id)
            .FirstOrDefaultAsync();
        if (mechanicRoleId == null) return new List<ArtisanLoadRow>();

        var mechanicUserIds = await _db.UserRoles
            .Where(ur => ur.RoleId == mechanicRoleId)
            .Select(ur => ur.UserId)
            .ToListAsync();

        var artisans = await _db.Users
            .Include(u => u.Fleet)
            .Where(u => u.IsActive && mechanicUserIds.Contains(u.Id))
            .ToListAsync();

        if (artisans.Count == 0) return new List<ArtisanLoadRow>();

        // Current open load + awaiting-parts split.
        var openLoad = await _db.DefectOrders
            .Where(d => d.AssignedMechanicId != null
                     && d.RepairStatus != RepairStatus.Completed)
            .GroupBy(d => d.AssignedMechanicId!)
            .Select(g => new {
                ArtisanId      = g.Key,
                Total          = g.Count(),
                AwaitingParts  = g.Count(d => d.RepairStatus == RepairStatus.AwaitingParts)
            })
            .ToDictionaryAsync(x => x.ArtisanId, x => x);

        // 7-day throughput + average closure time. Closure time = the
        // period the artisan actually had the jobcard on their plate
        // (DispatchedAt → ResolvedAt). We project raw timestamps and
        // do the diff in memory so the query stays provider-portable
        // (EF.Functions.DateDiffHour is SQL Server only; Npgsql would
        // throw at runtime).
        var closures = await _db.DefectOrders
            .Where(d => d.AssignedMechanicId != null
                     && d.RepairStatus == RepairStatus.Completed
                     && d.ResolvedAt    != null
                     && d.ResolvedAt    >= since7d)
            .Select(d => new {
                ArtisanId    = d.AssignedMechanicId!,
                DispatchedAt = d.DispatchedAt,
                ResolvedAt   = d.ResolvedAt
            })
            .ToListAsync();

        var closureByArtisan = closures
            .GroupBy(c => c.ArtisanId)
            .ToDictionary(g => g.Key, g => {
                var withTimes = g.Where(x => x.DispatchedAt.HasValue && x.ResolvedAt.HasValue)
                                 .Select(x => (x.ResolvedAt!.Value - x.DispatchedAt!.Value).TotalHours)
                                 .ToList();
                return new {
                    Count = g.Count(),
                    Avg   = withTimes.Count == 0 ? 0 : withTimes.Average()
                };
            });

        var rows = artisans.Select(u => new ArtisanLoadRow
        {
            ArtisanId       = u.Id,
            ArtisanName     = u.FullName,
            EmployeeNumber  = u.EmployeeNumber ?? "",
            FleetName       = u.Fleet?.Name,
            FleetColor      = u.Fleet?.Color,
            OpenLoad        = openLoad.TryGetValue(u.Id, out var o) ? o.Total         : 0,
            AwaitingParts   = openLoad.TryGetValue(u.Id, out var p) ? p.AwaitingParts : 0,
            ClosedLast7d    = closureByArtisan.TryGetValue(u.Id, out var c) ? c.Count             : 0,
            AvgClosureHours = closureByArtisan.TryGetValue(u.Id, out var a) ? Math.Round(a.Avg, 1): 0
        })
        .OrderByDescending(r => r.OpenLoad)
        .ThenBy(r => r.ArtisanName, StringComparer.OrdinalIgnoreCase)
        .ToList();

        return rows;
    }

    /// <summary>
    /// Snapshot of operator-competency health right now. Mirrors the SQL
    /// view <c>vw_kpi_competency_health</c> plus the renewal queue and the
    /// per-machine-type bench depth.
    /// </summary>
    private async Task<CompetencySection> BuildCompetencySectionAsync()
    {
        var now      = DateTime.UtcNow;
        var in7Days  = now.AddDays(7);
        var in30Days = now.AddDays(30);

        // Pull everything once — the table is small (one row per certificate
        // ever issued) so a full scan beats round-tripping for each bucket.
        var all = await _db.OperatorCompetencies
            .Include(c => c.Operator)
            .ToListAsync();

        // Group by operator and reduce to the WORST current status. One
        // expired licence on file flags the whole person — the
        // "least-compliant" rule the dashboard uses. Operators with zero
        // rows don't appear here at all.
        var worstByOperator = all
            .Where(c => c.OperatorId != null)
            .GroupBy(c => c.OperatorId!)
            .Select(g =>
            {
                // Rank: 1 = Revoked (worst), 2 = Expired, 3 = Expiring 7d,
                // 4 = Expiring 30d, 5 = Valid. MIN across the operator's
                // rows is the worst.
                int RankOf(OperatorCompetency c)
                {
                    if (!c.IsActive)              return 1;
                    if (c.ExpiresAt < now)        return 2;
                    if (c.ExpiresAt < in7Days)    return 3;
                    if (c.ExpiresAt < in30Days)   return 4;
                    return 5;
                }
                return g.Min(RankOf);
            }).ToList();

        var total            = worstByOperator.Count;
        var fullyValid       = worstByOperator.Count(r => r == 5);
        var expiring30       = worstByOperator.Count(r => r == 4);
        var expiring7        = worstByOperator.Count(r => r == 3);
        var expired          = worstByOperator.Count(r => r == 2);
        var revoked          = worstByOperator.Count(r => r == 1);
        // Compliance = fully valid + those whose worst is still ≥ 30 days
        // out. They're allowed to operate today; expired / revoked drop the
        // score. This matches the Power BI Compliance % measure exactly.
        var compliancePct = total == 0
            ? 0.0
            : Math.Round(100.0 * (fullyValid + expiring30) / total, 1);

        // Renewal queue — every active certificate within 30 days of
        // expiry, oldest first. Already-expired ones land at the top.
        var queue = all
            .Where(c => c.IsActive && c.ExpiresAt <= in30Days)
            .OrderByDescending(c => c.ExpiresAt < now)        // expired first
            .ThenBy(c => c.ExpiresAt)                          // then soonest
            .Take(25)
            .Select(c =>
            {
                int days   = (int)Math.Floor((c.ExpiresAt - now).TotalDays);
                string st  = !c.IsActive          ? "Revoked"
                          :   c.ExpiresAt < now   ? "Expired"
                          :   c.ExpiresAt < in7Days  ? "Expiring 7d"
                          :   c.ExpiresAt < in30Days ? "Expiring 30d"
                          :                            "Valid";
                return new RenewalQueueRow
                {
                    OperatorName      = c.Operator?.FullName ?? "—",
                    EmployeeNumber    = c.Operator?.EmployeeNumber ?? "",
                    MachineType       = c.MachineType,
                    CertificateNumber = c.CertificateNumber,
                    IssuedBy          = c.IssuedBy,
                    ExpiresAt         = c.ExpiresAt,
                    DaysToExpiry      = days,
                    Status            = st
                };
            }).ToList();

        // Per-machine-type bench depth. Counts active certs only — revoked
        // doesn't count toward "do we have FEL operators on shift".
        var byType = all
            .Where(c => c.IsActive)
            .GroupBy(c => c.MachineType)
            .Select(g => new CompetencyTypeRow
            {
                MachineType    = g.Key,
                Active         = g.Count(),
                Valid          = g.Count(c => c.ExpiresAt >= in30Days),
                Expiring30     = g.Count(c => c.ExpiresAt >= in7Days  && c.ExpiresAt < in30Days),
                Expiring7      = g.Count(c => c.ExpiresAt >= now      && c.ExpiresAt < in7Days),
                Expired        = g.Count(c => c.ExpiresAt < now)
            })
            .OrderByDescending(r => r.Active)
            .ToList();

        return new CompetencySection
        {
            OperatorsWithAny    = total,
            OperatorsFullyValid = fullyValid,
            OperatorsExpiring30 = expiring30,
            OperatorsExpiring7  = expiring7,
            OperatorsExpired    = expired,
            OperatorsRevoked    = revoked,
            CompliancePct       = compliancePct,
            RenewalsDue30d      = all.Count(c => c.IsActive
                                              && c.ExpiresAt >= now
                                              && c.ExpiresAt <= in30Days),
            LapsedCertificates  = all.Count(c => c.IsActive && c.ExpiresAt < now),
            Queue               = queue,
            ByMachineType       = byType
        };
    }

    /// <summary>
    /// Configuration + policy audit. Filters AuditEvents on the
    /// settings/admin/user/competency action codes — same WHERE clause as
    /// the SQL view <c>vw_fact_settings_changes</c>.
    /// </summary>
    private async Task<ConfigAuditSection> BuildConfigAuditSectionAsync()
    {
        // Hard-coded so the Reports page doesn't accidentally start
        // including operational submission/defect actions. If a new policy-
        // change constant is added in DTOs.AuditActions, append it here.
        var actionCodes = new[]
        {
            "settings.changed", "settings.reset",
            "admin.created", "admin.deactivated",
            "user.deactivated", "user.reactivated", "user.password_reset",
            "competency.added", "competency.revoked", "competency.renewed"
        };

        var now    = DateTime.UtcNow;
        var since30 = now.AddDays(-30);
        var since90 = now.AddDays(-90);

        // One scan over the bounded action codes — typical mine will have
        // a few hundred of these per quarter, so we materialise them and
        // bucket in-memory.
        var rows = await _db.AuditEvents
            .Where(a => actionCodes.Contains(a.Action) && a.OccurredAtServer >= since90)
            .OrderByDescending(a => a.OccurredAtServer)
            .Take(500)
            .ToListAsync();

        string CategoryOf(string action)
        {
            if (action.StartsWith("settings."))   return "Configuration";
            if (action.StartsWith("admin."))      return "Admin policy";
            if (action.StartsWith("user."))       return "User access";
            if (action.StartsWith("competency.")) return "Competency";
            return "Other";
        }

        var byCategory = rows
            .GroupBy(r => CategoryOf(r.Action))
            .Select(g => new CategoryCount { Category = g.Key, Count = g.Count() })
            .OrderByDescending(c => c.Count)
            .ToList();

        var recent = rows.Take(30).Select(a => new ConfigAuditRow
        {
            OccurredAt = a.OccurredAtServer,
            ActorName  = a.ActorName ?? "—",
            ActorRole  = a.ActorRole ?? "—",
            Action     = a.Action,
            Category   = CategoryOf(a.Action),
            TargetType = a.TargetType ?? "",
            TargetId   = a.TargetId?.ToString() ?? "",
            DeviceKind = a.DeviceKind,
            IpAddress  = a.IpAddress ?? ""
        }).ToList();

        return new ConfigAuditSection
        {
            Changes30d        = rows.Count(r => r.OccurredAtServer >= since30),
            Changes90d        = rows.Count,
            AdminPolicy30d    = rows.Count(r => r.OccurredAtServer >= since30
                                             && r.Action.StartsWith("admin.")),
            UserAccess30d     = rows.Count(r => r.OccurredAtServer >= since30
                                             && r.Action.StartsWith("user.")),
            CompetencyEvents30d = rows.Count(r => r.OccurredAtServer >= since30
                                             && r.Action.StartsWith("competency.")),
            DistinctActors90d = rows.Where(r => !string.IsNullOrEmpty(r.ActorUserId))
                                    .Select(r => r.ActorUserId)
                                    .Distinct()
                                    .Count(),
            LastChange        = rows.Count == 0 ? (DateTime?)null : rows.Max(r => r.OccurredAtServer),
            ByCategory        = byCategory,
            Recent            = recent
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

    /// <summary>Operator competency health — mirrors Power BI Page 5.</summary>
    public CompetencySection Competency { get; set; } = new();

    /// <summary>Settings + admin-policy audit — mirrors Power BI Page 6.</summary>
    public ConfigAuditSection ConfigAudit { get; set; } = new();

    /// <summary>Phase 6.4 — depth of every operational queue in the workflow,
    /// computed against current state (not the date window). Lets the admin
    /// see "is anything stuck right now?" at a glance.</summary>
    public OperationalQueuesSection OperationalQueues { get; set; } = new();

    /// <summary>Phase 6.5 — per-Artisan workshop load: current open defects
    /// + 7-day throughput. Surfaces over-loaded artisans the dispatcher
    /// should rebalance away from.</summary>
    public List<ArtisanLoadRow> ArtisanLoad { get; set; } = new();

    /// <summary>Phase 6.10 — per-fleet performance split (Mota-Engil vs
    /// Moolmans vs Belfast Direct etc.). Scoped to the dashboard date
    /// window so a fleet manager can see "how is each contractor doing
    /// this month?".</summary>
    public List<FleetPerformanceRow> FleetPerformance { get; set; } = new();
}

/// <summary>Phase 6.4 — operational queue depths snapshot.</summary>
public class OperationalQueuesSection
{
    public int PlannerPendingDefects     { get; set; }
    public int PlannerPendingSubmissions { get; set; }
    public int ControlRoomQueue          { get; set; }
    public int ControlRoomAcknowledgeInbox { get; set; }   // Phase 7.6
    public int WorkshopOpenDefects       { get; set; }
    public int AwaitingAdminClearance    { get; set; }
    public int SupervisorPendingSignOff  { get; set; }
    public int OldestPlannerDefectHours  { get; set; }
    public int OldestDispatchHours       { get; set; }
}

/// <summary>Phase 6.10 — one row per fleet (Mota-Engil, Moolmans, Belfast
/// Direct, …). Scoped to the dashboard date window. Lets a fleet manager
/// compare contractor performance side-by-side.</summary>
public class FleetPerformanceRow
{
    public int?    FleetId           { get; set; }
    public string  FleetName         { get; set; } = "";
    public string  FleetColor        { get; set; } = "#475569";
    public int     MachineCount      { get; set; }
    public int     Submissions       { get; set; }
    public int     GoCount           { get; set; }
    public int     GoButCount        { get; set; }
    public int     NoGoCount         { get; set; }
    public double  GoRatePct         { get; set; }
    public int     OpenDefects       { get; set; }
    public double  AvgResolutionHrs  { get; set; }
}

/// <summary>Phase 6.5 — one row per active Artisan, capturing current load
/// and recent throughput so the dispatcher can spot over-loaded or
/// under-utilised technicians.</summary>
public class ArtisanLoadRow
{
    public string ArtisanId        { get; set; } = "";
    public string ArtisanName      { get; set; } = "";
    public string EmployeeNumber   { get; set; } = "";
    public string? FleetName       { get; set; }
    public string? FleetColor      { get; set; }
    public int    OpenLoad         { get; set; }
    public int    AwaitingParts    { get; set; }
    public int    ClosedLast7d     { get; set; }
    public double AvgClosureHours  { get; set; }
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

// ── Competency section ─────────────────────────────────────────────────────
/// <summary>
/// Operator-competency snapshot rendered on the Reports page. The buckets
/// match Power BI Page 5 verbatim so screenshots of the two surfaces are
/// directly comparable — same numbers, same colour coding.
/// </summary>
public class CompetencySection
{
    /// <summary>How many distinct operators have at least one competency row on file.
    /// Operators with zero rows are excluded — they're a separate onboarding gap.</summary>
    public int OperatorsWithAny    { get; set; }
    public int OperatorsFullyValid { get; set; }
    public int OperatorsExpiring30 { get; set; }
    public int OperatorsExpiring7  { get; set; }
    public int OperatorsExpired    { get; set; }
    public int OperatorsRevoked    { get; set; }

    /// <summary>Compliance % = (fully valid + expiring 30d) / total with any.
    /// Operators within the 30-day grace band still count as compliant for the
    /// headline because they're still allowed to operate today.</summary>
    public double CompliancePct    { get; set; }

    /// <summary>Distinct certificates (not operators) coming due within 30 days.
    /// One operator with three certificates can appear three times.</summary>
    public int RenewalsDue30d      { get; set; }

    /// <summary>Active certificates whose ExpiresAt is already in the past.</summary>
    public int LapsedCertificates  { get; set; }

    /// <summary>Top 25 certificates in the renewal queue, oldest-due first.</summary>
    public List<RenewalQueueRow>   Queue         { get; set; } = new();

    /// <summary>One row per machine type — the bench-depth donut + table.</summary>
    public List<CompetencyTypeRow> ByMachineType { get; set; } = new();
}

public class RenewalQueueRow
{
    public string       OperatorName      { get; set; } = "";
    public string       EmployeeNumber    { get; set; } = "";
    public MachineType  MachineType       { get; set; }
    public string?      CertificateNumber { get; set; }
    public string?      IssuedBy          { get; set; }
    public DateTime     ExpiresAt         { get; set; }
    public int          DaysToExpiry      { get; set; }
    /// <summary>"Valid", "Expiring 30d", "Expiring 7d", "Expired", "Revoked".</summary>
    public string       Status            { get; set; } = "";
}

public class CompetencyTypeRow
{
    public MachineType MachineType { get; set; }
    public int Active     { get; set; }
    public int Valid      { get; set; }
    public int Expiring30 { get; set; }
    public int Expiring7  { get; set; }
    public int Expired    { get; set; }
}

// ── Configuration / policy audit section ───────────────────────────────────
/// <summary>
/// Settings + admin-policy audit snapshot for the Reports page. Same source
/// rows the Power BI page 6 visual reads: filtered AuditEvents over a fixed
/// set of action codes, categorised into Configuration / Admin policy /
/// User access / Competency.
/// </summary>
public class ConfigAuditSection
{
    public int Changes30d          { get; set; }
    public int Changes90d          { get; set; }
    public int AdminPolicy30d      { get; set; }
    public int UserAccess30d       { get; set; }
    public int CompetencyEvents30d { get; set; }
    public int DistinctActors90d  { get; set; }
    public DateTime? LastChange    { get; set; }
    public List<CategoryCount>   ByCategory { get; set; } = new();
    public List<ConfigAuditRow>  Recent     { get; set; } = new();
}

public class CategoryCount
{
    public string Category { get; set; } = "";
    public int    Count    { get; set; }
}

public class ConfigAuditRow
{
    public DateTime OccurredAt { get; set; }
    public string   ActorName  { get; set; } = "";
    public string   ActorRole  { get; set; } = "";
    public string   Action     { get; set; } = "";
    public string   Category   { get; set; } = "";
    public string   TargetType { get; set; } = "";
    public string   TargetId   { get; set; } = "";
    public string   DeviceKind { get; set; } = "";
    public string   IpAddress  { get; set; } = "";
}
