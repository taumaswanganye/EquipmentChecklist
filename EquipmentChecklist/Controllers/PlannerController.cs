using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Controllers;

/// <summary>
/// Plant Maintenance Planner workflow. Sits between Supervisor approval
/// and Control Room dispatch in the Phase 3 routing chain:
///
/// <para>Operator → Supervisor → <b>Planner</b> → Control Room → Artisan</para>
///
/// <para>The Planner reviews each supervisor-approved DefectOrder, assigns
/// it a SAP jobcard number (manually or from an SAP response when the
/// integration is live), and clicks Capture. That stamps
/// <c>PlannerCapturedAt</c> + <c>PlannerCapturedById</c>, fires an audit
/// event, and moves the row into the Control Room queue.</para>
/// </summary>
[Authorize(Policy = "Planner")]
public class PlannerController : Controller
{
    private readonly ApplicationDbContext         _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly AuditService                 _audit;

    public PlannerController(ApplicationDbContext db,
                             UserManager<ApplicationUser> users,
                             AuditService audit)
    {
        _db    = db;
        _users = users;
        _audit = audit;
    }

    /// <summary>
    /// The Planner queue. Lists every DefectOrder that's been raised
    /// (supervisor approval is implicit because the DefectOrder only
    /// exists after a NO-GO submission, which the supervisor has signed
    /// or rejected) but hasn't been captured into the jobcard system yet.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var pending = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.Submission).ThenInclude(s => s.Operator)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Where(d => d.PlannerCapturedAt == null
                     && d.RepairStatus     != RepairStatus.Completed)
            .OrderBy(d => d.CreatedAt)
            .Take(100)
            .ToListAsync();

        // Phase 4.B + 7.5 — supervisor-approved submissions awaiting
        // Planner capture (the no-defect-route closure). Per the spec
        // tightening in Phase 7.5, ALL three approved statuses now
        // require explicit Supervisor approval before flowing to Planner:
        //   - clean GO with SupervisorSignedAt != null (Phase 7.5)
        //   - GO-BUT 24H with SupervisorSignedAt != null
        //   - GO-BUT 30D with SupervisorSignedAt != null
        // Excludes NO-GO (DefectOrder route above), unapproved GO-BUT
        // (still in Supervisor queue), and unapproved clean GO (now also
        // in Supervisor's Quick Approve lane).
        // Window is 48h — operational shift logs span up to two shifts back.
        var captureCutoff = DateTime.UtcNow.AddHours(-48);
        var approvedAwaitingCapture = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Where(s => s.CapturedAt == null
                     && s.SubmittedAt >= captureCutoff
                     && s.SupervisorSignedAt != null
                     && (s.Status == ChecklistStatus.Go
                         || s.Status == ChecklistStatus.GoButRepair24H
                         || s.Status == ChecklistStatus.GoTillNextService))
            .OrderBy(s => s.SubmittedAt)
            .Take(50)
            .ToListAsync();

        // Stats for the page header — total pending + how many are sitting
        // longer than 24h (which usually means the Planner is behind).
        var now      = DateTime.UtcNow;
        ViewBag.Stats = new {
            Pending             = pending.Count,
            Stale24h            = pending.Count(d => (now - d.CreatedAt).TotalHours > 24),
            CapturedToday       = await _db.DefectOrders
                                      .CountAsync(d => d.PlannerCapturedAt != null
                                                    && d.PlannerCapturedAt >= DateTime.UtcNow.Date),
            // Phase 4.B
            ApprovedToCapture   = approvedAwaitingCapture.Count,
            ApprovedCapturedToday = await _db.ChecklistSubmissions
                                      .CountAsync(s => s.CapturedAt != null
                                                    && s.CapturedAt >= DateTime.UtcNow.Date)
        };
        ViewBag.ApprovedAwaitingCapture = approvedAwaitingCapture;
        return View(pending);
    }

    /// <summary>
    /// Phase 6.6 — lightweight JSON snapshot of the Planner's queue depth.
    /// Polled by the Planner/Index page every 30s; when any of the
    /// numbers change, the page surfaces a "new items — refresh" banner.
    /// Returns the same numbers the Index page renders in its stat-cards
    /// so the comparison is apples-to-apples.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> QueueDepth()
    {
        var now           = DateTime.UtcNow;
        var captureCutoff = now.AddHours(-48);

        var pending = await _db.DefectOrders
            .CountAsync(d => d.PlannerCapturedAt == null
                          && d.RepairStatus     != RepairStatus.Completed);

        // Pre-compute the "24h ago" cutoff and compare against it directly
        // — Npgsql doesn't translate EF.Functions.DateDiffHour.
        var staleCutoff = now.AddHours(-24);
        var stale24h = await _db.DefectOrders
            .CountAsync(d => d.PlannerCapturedAt == null
                          && d.RepairStatus     != RepairStatus.Completed
                          && d.CreatedAt        < staleCutoff);

        var approvedAwaitingCapture = await _db.ChecklistSubmissions
            .CountAsync(s => s.CapturedAt == null
                          && s.SubmittedAt >= captureCutoff
                          && (s.Status == ChecklistStatus.Go
                              || (s.Status == ChecklistStatus.GoButRepair24H
                                  && s.SupervisorSignedAt != null)
                              || (s.Status == ChecklistStatus.GoTillNextService
                                  && s.SupervisorSignedAt != null)));

        return Json(new {
            pending,
            stale24h,
            approvedAwaitingCapture,
            checkedAt = now
        });
    }

    /// <summary>
    /// Phase 4.B — log a supervisor-approved submission into the Planner's
    /// shift system. Simple "I've recorded this" marker; doesn't create a
    /// jobcard (that's only for defects). Stamps CapturedAt +
    /// CapturedByPlannerId so reporting can show "of all approved
    /// submissions, how many did the Planner capture within X hours".
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CaptureSubmission(int id)
    {
        var submission = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (submission == null)
        {
            TempData["Error"] = "Submission not found.";
            return RedirectToAction("Index");
        }
        if (submission.CapturedAt != null)
        {
            TempData["Error"] = $"Submission #{id} was already captured at {submission.CapturedAt:yyyy-MM-dd HH:mm}.";
            return RedirectToAction("Index");
        }

        submission.CapturedAt          = DateTime.UtcNow;
        submission.CapturedByPlannerId = _users.GetUserId(User);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            "planner.captured_submission",
            targetType: "ChecklistSubmission",
            targetId:   submission.Id,
            payload:    new {
                machineNumber = submission.Machine.MachineNumber,
                status        = submission.Status.ToString(),
                planner       = User.Identity?.Name
            });

        TempData["Success"] = $"✅ Captured submission #{id} ({submission.Machine.MachineNumber}) into the shift log.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Phase 6.2 — bulk-capture every approved submission currently shown
    /// in the "awaiting capture" queue. Walter (Planner) runs this at the
    /// end of his shift to clear the backlog of clean GO + signed-off
    /// GO-BUT submissions in one click instead of 20 individual clicks.
    /// Uses the same 48h window as the Index query so the "what gets
    /// captured" matches what the user sees on screen at submit time.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CaptureAllSubmissions()
    {
        var captureCutoff = DateTime.UtcNow.AddHours(-48);
        var batch = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Where(s => s.CapturedAt == null
                     && s.SubmittedAt >= captureCutoff
                     && (s.Status == ChecklistStatus.Go
                         || (s.Status == ChecklistStatus.GoButRepair24H
                             && s.SupervisorSignedAt != null)
                         || (s.Status == ChecklistStatus.GoTillNextService
                             && s.SupervisorSignedAt != null)))
            .ToListAsync();

        if (batch.Count == 0)
        {
            TempData["Error"] = "Nothing to capture — queue is already clear.";
            return RedirectToAction("Index");
        }

        var now       = DateTime.UtcNow;
        var plannerId = _users.GetUserId(User);
        foreach (var s in batch)
        {
            s.CapturedAt          = now;
            s.CapturedByPlannerId = plannerId;
        }
        await _db.SaveChangesAsync();

        // One audit row per submission so the trail still shows exactly
        // which submissions were captured by whom + when. Hash chain is
        // serialised inside AuditService so this is safe to loop.
        foreach (var s in batch)
        {
            await _audit.LogAsync(
                "planner.captured_submission",
                targetType: "ChecklistSubmission",
                targetId:   s.Id,
                payload:    new {
                    machineNumber = s.Machine.MachineNumber,
                    status        = s.Status.ToString(),
                    planner       = User.Identity?.Name,
                    bulk          = true
                });
        }

        TempData["Success"] = $"✅ Bulk-captured {batch.Count} approved submission{(batch.Count == 1 ? "" : "s")} into the shift log.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Capture a defect into the jobcard system. In SAP-integrated mode
    /// the jobcard number comes back from SAP via the outbox; here the
    /// Planner can also type one in manually (useful while the SAP
    /// integration is being commissioned, or when SAP is down). Either
    /// way, this is the action that "promotes" the defect from Planner
    /// queue to Control Room queue.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Capture(int id, string? jobCardNumber)
    {
        var defect = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (defect == null)
        {
            TempData["Error"] = "Defect not found.";
            return RedirectToAction("Index");
        }
        if (defect.PlannerCapturedAt != null)
        {
            TempData["Error"] = $"Defect #{id} was already captured at {defect.PlannerCapturedAt:yyyy-MM-dd HH:mm} — refusing to re-capture.";
            return RedirectToAction("Index");
        }

        defect.PlannerCapturedAt   = DateTime.UtcNow;
        defect.PlannerCapturedById = _users.GetUserId(User);
        defect.JobCardNumber       = string.IsNullOrWhiteSpace(jobCardNumber) ? null : jobCardNumber.Trim();
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            "planner.captured",
            targetType: "DefectOrder",
            targetId:   defect.Id,
            payload:    new {
                machineNumber = defect.Submission.Machine.MachineNumber,
                jobCardNumber = defect.JobCardNumber,
                planner       = User.Identity?.Name
            });

        var label = defect.JobCardNumber ?? $"defect #{defect.Id}";
        TempData["Success"] = $"✅ Captured {label} for machine {defect.Submission.Machine.MachineNumber}. Now in Control Room dispatch queue.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Planner-side rejection. Used when the defect is a duplicate of
    /// an existing jobcard, or when the Planner believes the operator
    /// flagged something that isn't actually a defect, or when the
    /// submission needs supervisor clarification before it can be
    /// captured. The defect is marked Completed with the rejection
    /// reason stored as the resolution notes, an audit row is written,
    /// and the original supervisor gets a notification.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "Rejection reason is required.";
            return RedirectToAction("Index");
        }

        var defect = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.Submission).ThenInclude(s => s.Operator)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (defect == null)
        {
            TempData["Error"] = "Defect not found.";
            return RedirectToAction("Index");
        }
        if (defect.PlannerCapturedAt != null)
        {
            TempData["Error"] = $"Defect #{id} has already been captured — cannot reject after capture.";
            return RedirectToAction("Index");
        }
        if (defect.RepairStatus == RepairStatus.Completed)
        {
            TempData["Error"] = $"Defect #{id} is already closed.";
            return RedirectToAction("Index");
        }

        var trimmedReason = reason.Trim();
        defect.RepairStatus    = RepairStatus.Completed;
        defect.ResolvedAt      = DateTime.UtcNow;
        defect.ResolutionNotes = $"REJECTED BY PLANNER: {trimmedReason}";
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            "planner.rejected",
            targetType: "DefectOrder",
            targetId:   defect.Id,
            payload:    new {
                machineNumber = defect.Submission.Machine.MachineNumber,
                reason        = trimmedReason,
                planner       = User.Identity?.Name
            });

        TempData["Success"] = $"Defect #{id} on {defect.Submission.Machine.MachineNumber} rejected with reason: \"{trimmedReason}\".";
        return RedirectToAction("Index");
    }
}
