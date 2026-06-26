using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Controllers;

[Authorize(Roles = "Admin,Supervisor")]
public class SupervisorController : Controller
{
    private readonly ApplicationDbContext         _db;
    private readonly ChecklistService             _svc;
    private readonly UserManager<ApplicationUser> _users;
    private readonly EmailService                 _email;
    private readonly NotificationService           _notifications;
    private readonly AuditService                  _audit;
    private readonly ILogger<SupervisorController> _log;

    public SupervisorController(ApplicationDbContext db,
                                ChecklistService svc,
                                UserManager<ApplicationUser> users,
                                EmailService email,
                                NotificationService notifications,
                                AuditService audit,
                                ILogger<SupervisorController> log)
    {
        _db    = db;
        _svc   = svc;
        _users = users;
        _email = email;
        _notifications = notifications;
        _audit = audit;
        _log   = log;
    }

    // ── Sign-Off Queue ────────────────────────────────────────────────────────
    public async Task<IActionResult> Index([FromQuery] ListFilter filter,
                                           [FromQuery] int page = 1)
    {
        FilterPresets.Apply(filter);

        var supervisorId = _users.GetUserId(User)!;
        var isAdmin = User.IsInRole("Admin");

        // Get operator IDs assigned to this supervisor (all if admin)
        List<string> assignedOperatorIds;
        if (isAdmin)
        {
            assignedOperatorIds = await _db.Users.Select(u => u.Id).ToListAsync();
        }
        else
        {
            assignedOperatorIds = await _db.OperatorSupervisorAssignments
                .Where(a => a.SupervisorId == supervisorId && a.IsActive)
                .Select(a => a.OperatorId)
                .ToListAsync();
        }

        var baseQuery = _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .Where(s => s.Status == ChecklistStatus.GoButRepair24H
                     && s.SupervisorId == null
                     && assignedOperatorIds.Contains(s.OperatorId))
            .ApplyDateRange(filter, s => s.SubmittedAt)
            .OrderBy(s => s.SubmittedAt);

        var loaded = await baseQuery.ToListAsync();

        // Search hits navigation properties — apply in memory.
        var pending = loaded.ApplySearchInMemory(
            filter,
            s => s.Machine.MachineNumber,
            s => s.Machine.MachineName,
            s => s.Operator.FullName,
            s => s.Operator.EmployeeNumber);

        // ── Paging ────────────────────────────────────────────────────────
        // Sign-off cards are visually dense (defects + reject panel), so a
        // small page size of 4 keeps the page scannable while letting busy
        // supervisors burn through a deep queue.
        const int PAGE_SIZE = 4;
        if (page < 1) page = 1;
        var totalPages = Math.Max(1, (int)Math.Ceiling(pending.Count / (double)PAGE_SIZE));
        if (page > totalPages) page = totalPages;
        var pageSlice = pending
            .Skip((page - 1) * PAGE_SIZE)
            .Take(PAGE_SIZE)
            .ToList();

        // ── Phase 7.5 — Clean GO quick-approve lane ───────────────────────
        // Per the user's spec, clean GO submissions also require explicit
        // Supervisor review + approval before they route to Planner. We
        // query a SECOND lane (separate from the GO-BUT sign-off lane
        // above) so the supervisor can scan + click without confusing it
        // with the defect-acceptance flow.
        //
        // Window: 48h. The supervisor catches up after every shift; rows
        // older than 48h are stale enough that the right action is "investigate
        // why they were ignored" rather than auto-approving them late.
        var goCutoff = DateTime.UtcNow.AddHours(-48);
        var pendingGoApprovals = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Where(s => s.Status == ChecklistStatus.Go
                     && s.SupervisorSignedAt == null
                     && s.SubmittedAt >= goCutoff
                     && assignedOperatorIds.Contains(s.OperatorId))
            .OrderBy(s => s.SubmittedAt)
            .Take(100)
            .ToListAsync();

        // Pass available mechanics so supervisor can pick who to assign on reject
        ViewBag.Mechanics            = await _users.GetUsersInRoleAsync("Mechanic");
        ViewBag.Filter               = filter;
        ViewBag.TotalUnfiltered      = loaded.Count;
        ViewBag.TotalFiltered        = pending.Count;
        ViewBag.Page                 = page;
        ViewBag.TotalPages           = totalPages;
        ViewBag.PageSize             = PAGE_SIZE;
        ViewBag.PendingGoApprovals   = pendingGoApprovals;
        ViewBag.ApprovedGoToday      = await _db.ChecklistSubmissions
            .CountAsync(s => s.Status == ChecklistStatus.Go
                          && s.SupervisorSignedAt != null
                          && s.SupervisorSignedAt >= DateTime.UtcNow.Date
                          && assignedOperatorIds.Contains(s.OperatorId));

        return View(pageSlice);
    }

    // ─── Phase 7.5 — Clean GO Quick Approve ──────────────────────────────────
    /// <summary>
    /// Approve a clean GO submission. Click-only — no signature pad — because
    /// there's no risk transfer (machine is fine, no defect). Stamps
    /// <c>SupervisorSignedAt + SupervisorId</c> which is what the Planner
    /// query checks for before showing the submission on the capture list.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveGo(int id)
    {
        var supervisorId = _users.GetUserId(User)!;
        var sub = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null)
        {
            TempData["Error"] = "Submission not found.";
            return RedirectToAction("Index");
        }
        if (sub.Status != ChecklistStatus.Go)
        {
            TempData["Error"] = $"Submission #{id} isn't a clean GO — use the standard sign-off flow.";
            return RedirectToAction("Index");
        }
        if (sub.SupervisorSignedAt != null)
        {
            TempData["Error"] = $"Submission #{id} was already approved at {sub.SupervisorSignedAt:yyyy-MM-dd HH:mm}.";
            return RedirectToAction("Index");
        }

        sub.SupervisorSignedAt = DateTime.UtcNow;
        sub.SupervisorId       = supervisorId;
        await _db.SaveChangesAsync();

        try
        {
            await _audit.LogAsync(
                "supervisor.go_approved",
                targetType: "ChecklistSubmission",
                targetId:   sub.Id,
                payload:    new {
                    machineNumber = sub.Machine.MachineNumber,
                    supervisor    = User.Identity?.Name
                });
        }
        catch { /* audit failure must not block approval */ }

        TempData["Success"] = $"✅ Approved clean GO for {sub.Machine.MachineNumber}.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Bulk-approve every clean GO submission currently visible to this
    /// supervisor. End-of-shift one-click pattern — same shape as the
    /// Planner's "Capture all" button (Phase 6.2). Writes one audit row
    /// per submission with <c>bulk: true</c> so investigators can later
    /// distinguish bulk-approvals from individual reviews.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ApproveAllGo()
    {
        var supervisorId = _users.GetUserId(User)!;
        var isAdmin = User.IsInRole("Admin");

        List<string> assignedOperatorIds = isAdmin
            ? await _db.Users.Select(u => u.Id).ToListAsync()
            : await _db.OperatorSupervisorAssignments
                .Where(a => a.SupervisorId == supervisorId && a.IsActive)
                .Select(a => a.OperatorId)
                .ToListAsync();

        var goCutoff = DateTime.UtcNow.AddHours(-48);
        var batch = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Where(s => s.Status == ChecklistStatus.Go
                     && s.SupervisorSignedAt == null
                     && s.SubmittedAt >= goCutoff
                     && assignedOperatorIds.Contains(s.OperatorId))
            .ToListAsync();

        if (batch.Count == 0)
        {
            TempData["Error"] = "Nothing to approve — your clean GO queue is already clear.";
            return RedirectToAction("Index");
        }

        var now = DateTime.UtcNow;
        foreach (var s in batch)
        {
            s.SupervisorSignedAt = now;
            s.SupervisorId       = supervisorId;
        }
        await _db.SaveChangesAsync();

        // One audit row per submission so the trail still shows exactly
        // which submissions were approved and when. Hash chain is serialised
        // inside AuditService so this loop is safe + the chain stays intact.
        foreach (var s in batch)
        {
            try
            {
                await _audit.LogAsync(
                    "supervisor.go_approved",
                    targetType: "ChecklistSubmission",
                    targetId:   s.Id,
                    payload:    new {
                        machineNumber = s.Machine.MachineNumber,
                        supervisor    = User.Identity?.Name,
                        bulk          = true
                    });
            }
            catch { /* never break the bulk on a single audit failure */ }
        }

        TempData["Success"] = $"✅ Bulk-approved {batch.Count} clean GO submission{(batch.Count == 1 ? "" : "s")}.";
        return RedirectToAction("Index");
    }

    // ── Review Specific Submission ────────────────────────────────────────────
    [HttpGet("/Supervisor/Review/{id:int}")]
    public async Task<IActionResult> Review(int id)
    {
        var submission = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (submission == null)
        {
            TempData["Error"] = "Submission not found.";
            return RedirectToAction("Index");
        }

        return View(submission);
    }

    // ── Approve Sign Off ──────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOff(int id, int resolution, string? supervisorSignature)
    {
        var supervisorId = _users.GetUserId(User)!;
        var status = resolution == 3
            ? ChecklistStatus.GoTillNextService
            : ChecklistStatus.GoButRepair24H;

        if (string.IsNullOrWhiteSpace(supervisorSignature))
        {
            TempData["Error"] = "A digital signature is required to approve.";
            return RedirectToAction("Review", new { id });
        }

        try
        {
            await _svc.SupervisorSignOffAsync(id, supervisorId, status, supervisorSignature);
            TempData["Success"] = "Sign-off recorded.";
        }
        catch (ChecklistService.ConflictException cx)
        {
            // Two supervisors raced on the same submission, we lost. Write
            // a notification so the bell badge surfaces it next page load,
            // and show TempData so the redirect destination renders an
            // immediate explanation.
            await _notifications.PushAsync(
                userId:              supervisorId,
                kind:                NotificationKinds.ConflictRejected,
                title:               $"✕ Sign-off rejected — {cx.MachineNumber ?? "submission"}",
                body:                cx.Message,
                relatedSubmissionId: id);
            TempData["Error"] = cx.Message;
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction("Index");
    }

    // ── Reject Submission ─────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string rejectionReason, string mechanicId)
    {
        var supervisorId = _users.GetUserId(User)!;

        var submission = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (submission == null)
        {
            TempData["Error"] = "Submission not found.";
            return RedirectToAction("Index");
        }

        // Mark as rejected and record supervisor + mechanic
        submission.Status           = ChecklistStatus.Rejected;
        submission.SupervisorId     = supervisorId;
        submission.SupervisorSignedAt = DateTime.UtcNow;
        submission.RejectionReason  = rejectionReason;
        submission.RejectedMechanicId = mechanicId;

        // Immobilise the machine – supervisor rejected it as unfit to operate
        submission.Machine.IsImmobilised     = true;
        submission.Machine.ImmobilisedReason = $"Supervisor rejected checklist on {DateTime.UtcNow:yyyy-MM-dd HH:mm}. Reason: {rejectionReason}";

        // Create a DefectOrder for every defective item and assign to the chosen mechanic
        var defects = submission.Items.Where(i => i.Status == ItemStatus.Defect).ToList();
        int created = 0;
        foreach (var item in defects)
        {
            // Avoid duplicates – skip if a pending order already exists for this item
            bool alreadyExists = await _db.DefectOrders
                .AnyAsync(d => d.SubmissionItemId == item.Id
                            && d.RepairStatus != RepairStatus.Completed);
            if (alreadyExists) continue;

            _db.DefectOrders.Add(new DefectOrder
            {
                SubmissionId       = submission.Id,
                SubmissionItemId   = item.Id,
                DefectDescription  = item.Notes ?? item.TemplateItem.ItemName,
                AssignedMechanicId = mechanicId,
                RepairStatus       = RepairStatus.InProgress,
                CreatedAt          = DateTime.UtcNow
            });
            created++;
        }

        await _db.SaveChangesAsync();

        // ── Notify the assigned mechanic by email ──
        // Mirrors the mobile API. Failures are logged and swallowed — the
        // rejection itself has already been persisted.
        try
        {
            var mech       = await _users.FindByIdAsync(mechanicId);
            var supervisor = await _users.FindByIdAsync(supervisorId);
            if (mech != null && supervisor != null)
            {
                await _email.SendRejectionNotificationAsync(
                    mechanicEmail:  mech.Email ?? "",
                    mechanicName:   mech.FullName,
                    operatorName:   submission.Operator.FullName,
                    machineNumber:  submission.Machine.MachineNumber,
                    machineName:    submission.Machine.MachineName,
                    reason:         rejectionReason,
                    defectCount:    created,
                    supervisorName: supervisor.FullName);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rejection-notification email failed for submission {SubmissionId} (web). " +
                                "Reject persisted; email pipeline was skipped.", id);
        }

        // In-app notification to the operator (parallels the mobile API path).
        try
        {
            await _notifications.PushAsync(
                userId:              submission.OperatorId,
                kind:                NotificationKinds.SubmissionRejected,
                title:               $"🛑 Rejected on {submission.Machine.MachineNumber}",
                body:                $"Reason: {rejectionReason}",
                relatedSubmissionId: submission.Id,
                relatedMachineId:    submission.MachineId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rejection in-app notification failed for submission {SubmissionId} (web).", id);
        }

        TempData["Success"] = "Submission rejected. Machine immobilised and defects sent to mechanic.";
        return RedirectToAction("Index");
    }

    // ── My Operators ──────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> MyOperators()
    {
        var supervisorId = _users.GetUserId(User)!;
        var isAdmin      = User.IsInRole("Admin");

        // Admin sees everyone; supervisors only see their assigned operators
        IQueryable<OperatorSupervisorAssignment> q = _db.OperatorSupervisorAssignments
            .Include(a => a.Operator)
            .Where(a => a.IsActive);
        if (!isAdmin)
            q = q.Where(a => a.SupervisorId == supervisorId);

        var assignments = await q
            .OrderBy(a => a.Operator.FullName)
            .ToListAsync();

        var operatorIds = assignments.Select(a => a.OperatorId).ToList();
        var since       = DateTime.UtcNow.Date.AddDays(-30);

        // Per-operator: last submission + counts in the trailing 30 days
        var recentByOperator = await _db.ChecklistSubmissions
            .Where(s => operatorIds.Contains(s.OperatorId) && s.SubmittedAt >= since)
            .GroupBy(s => s.OperatorId)
            .Select(g => new
            {
                OperatorId = g.Key,
                Total      = g.Count(),
                Pending    = g.Count(s => s.Status == ChecklistStatus.GoButRepair24H && s.SupervisorId == null),
                NoGo       = g.Count(s => s.Status == ChecklistStatus.NoGo),
                LastAt     = g.Max(s => s.SubmittedAt)
            })
            .ToListAsync();

        ViewBag.Stats = recentByOperator.ToDictionary(x => x.OperatorId,
            x => (Total: x.Total, Pending: x.Pending, NoGo: x.NoGo, LastAt: (DateTime?)x.LastAt));

        // Machines each operator is currently driving
        var machinesByOperator = await _db.MachineAssignments
            .Include(a => a.Machine)
            .Where(a => operatorIds.Contains(a.OperatorId) && a.IsActive)
            .ToListAsync();
        ViewBag.MachinesByOperator = machinesByOperator
            .GroupBy(a => a.OperatorId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.Machine).ToList());

        return View(assignments);
    }

    // ── NO-GO Machines ────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> NoGoMachines(int page = 1)
    {
        // Pull the full assignment graph so the view can render mechanic
        // names + acknowledgement state per defect without N+1 queries.
        // Ordering happens in memory below — nested Min() over a filtered
        // subset is the kind of EF expression that the Npgsql translator
        // dislikes, and the row count here (immobilised machines, usually
        // <50 at a busy mine) is small enough that in-memory ordering is
        // free.
        var loaded = await _db.Machines
            .Include(m => m.Submissions)
                .ThenInclude(s => s.Operator)
            .Include(m => m.Submissions)
                .ThenInclude(s => s.DefectOrders)
                    .ThenInclude(d => d.AssignedMechanic)
            .Include(m => m.Submissions)
                .ThenInclude(s => s.DefectOrders)
                    .ThenInclude(d => d.SubmissionItem)
                        .ThenInclude(i => i.TemplateItem)
            .Where(m => m.IsImmobilised)
            .ToListAsync();

        // Oldest open defect first — the supervisor cares most about
        // machines that have been down longest. Machines with no open
        // defects (rare — usually means data drift) sort to the end.
        var all = loaded
            .OrderBy(m =>
            {
                var openCreated = m.Submissions
                    .SelectMany(s => s.DefectOrders)
                    .Where(d => d.RepairStatus != RepairStatus.Completed)
                    .Select(d => (DateTime?)d.CreatedAt)
                    .DefaultIfEmpty(null)
                    .Min();
                return openCreated ?? DateTime.MaxValue;
            })
            .ToList();

        const int PAGE_SIZE = 6;
        if (page < 1) page = 1;
        var totalPages = Math.Max(1, (int)Math.Ceiling(all.Count / (double)PAGE_SIZE));
        if (page > totalPages) page = totalPages;

        var pageRows = all
            .Skip((page - 1) * PAGE_SIZE)
            .Take(PAGE_SIZE)
            .ToList();

        ViewBag.Page       = page;
        ViewBag.TotalPages = totalPages;
        ViewBag.TotalCount = all.Count;
        ViewBag.PageSize   = PAGE_SIZE;
        return View(pageRows);
    }
}
