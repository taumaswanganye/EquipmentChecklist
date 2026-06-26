using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Controllers;

/// <summary>
/// Mine Control Room dispatch workflow. The fourth seat in the Phase 3
/// routing chain:
///
/// <para>Operator → Supervisor → Planner → <b>Control Room</b> → Artisan</para>
///
/// <para>Control Room sees every defect that the Planner has captured
/// into a jobcard but hasn't been dispatched to an Artisan yet. The
/// dispatcher picks an Artisan from the pool (filtered by fleet in a
/// future Path B round; today it's any Mechanic-role user) and clicks
/// Dispatch. That stamps DispatchedAt + DispatchedById +
/// AssignedMechanicId, notifies the Artisan, writes audit, and moves
/// the defect into the Artisan's queue.</para>
/// </summary>
[Authorize(Policy = "ControlRoom")]
public class ControlRoomController : Controller
{
    private readonly ApplicationDbContext         _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly NotificationService          _notifications;
    private readonly AuditService                 _audit;

    public ControlRoomController(ApplicationDbContext db,
                                 UserManager<ApplicationUser> users,
                                 NotificationService notifications,
                                 AuditService audit)
    {
        _db            = db;
        _users         = users;
        _notifications = notifications;
        _audit         = audit;
    }

    /// <summary>
    /// Dispatch queue. Every DefectOrder that's been captured by the
    /// Planner but not yet assigned to an Artisan. Plus the list of
    /// available Artisans for the dispatch dropdown.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var pending = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine).ThenInclude(m => m.Fleet)
            .Include(d => d.Submission).ThenInclude(s => s.Operator)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Where(d => d.PlannerCapturedAt != null
                     && d.DispatchedAt      == null
                     && d.RepairStatus      != RepairStatus.Completed)
            .OrderBy(d => d.PlannerCapturedAt)
            .Take(100)
            .ToListAsync();

        // Phase 4 — fleet-aware Artisan picker. Build a lookup of
        // artisan-id → fleet-id (null = any-fleet artisan), plus the
        // display list per fleet. The view uses ViewBag.ArtisansByFleet
        // to filter the dropdown client-side when the user picks a
        // defect: only Artisans whose FleetId matches the defect's
        // Machine.FleetId appear (or all of them if the machine has no
        // fleet, preserving pre-Phase-4 behaviour).
        //
        // Phase 6.3 — also compute each Artisan's current OpenLoad
        // (count of dispatched-but-unresolved defects on their plate).
        // The view sorts the dropdown ascending by load within each
        // fleet so the lightest-loaded Artisan is the default — the
        // dispatcher can override with one click but doesn't have to
        // think about who's free.
        var artisanRole = await _users.GetUsersInRoleAsync("Mechanic");
        var openLoadByArtisan = await _db.DefectOrders
            .Where(d => d.AssignedMechanicId != null
                     && d.RepairStatus != RepairStatus.Completed)
            .GroupBy(d => d.AssignedMechanicId!)
            .Select(g => new { ArtisanId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ArtisanId, x => x.Count);
        var artisansAll = artisanRole
            .Where(u => u.IsActive)
            .OrderBy(u => u.FullName)
            .Select(u => new ArtisanOption(
                Id:          u.Id,
                Display:     $"{u.FullName} ({u.EmployeeNumber})",
                FleetId:     u.FleetId,
                OpenLoad:    openLoadByArtisan.TryGetValue(u.Id, out var n) ? n : 0))
            .ToList();

        // Recently dispatched but not yet resolved — surfaces the
        // Reassign affordance for when the originally-dispatched Artisan
        // has gone off-shift, on leave, or is otherwise unavailable.
        // 24h window keeps the list short; older in-flight defects are
        // available on /Mechanic but rarely need reassignment after a day.
        var since24h = DateTime.UtcNow.AddHours(-24);
        var recentlyDispatched = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine).ThenInclude(m => m.Fleet)
            .Include(d => d.AssignedMechanic)
            .Where(d => d.DispatchedAt != null
                     && d.DispatchedAt >= since24h
                     && d.RepairStatus != RepairStatus.Completed)
            .OrderByDescending(d => d.DispatchedAt)
            .Take(20)
            .ToListAsync();

        // Phase 4.B — Shift Status Board. For every active machine, the
        // most recent submission in the last 24h. Read-only situational
        // awareness for the Control Room dispatcher — "what's happening
        // on the fleet right now?" Colour-coded in the view: green =
        // operational, amber = GO-BUT, red = NO-GO (awaiting dispatch or
        // in repair), grey = no recent check.
        //
        // Implementation pulls every machine + its most recent submission
        // in one round-trip. For a 50-machine pilot this is trivial; if
        // it grows to thousands of machines the query becomes a "latest
        // per group" SQL pattern.
        // (Reuses `since24h` declared above for recentlyDispatched.)
        var activeMachines = await _db.Machines
            .Include(m => m.Fleet)
            .Where(m => m.IsActive)
            .OrderBy(m => m.MachineNumber)
            .ToListAsync();
        // Most recent submission per active machine, last 24h only.
        var recentByMachine = await _db.ChecklistSubmissions
            .Include(s => s.Operator)
            .Where(s => s.SubmittedAt >= since24h)
            .GroupBy(s => s.MachineId)
            .Select(g => g.OrderByDescending(s => s.SubmittedAt).First())
            .ToListAsync();
        var recentLookup = recentByMachine.ToDictionary(s => s.MachineId, s => s);
        // Build a status-board row per active machine (machine + optional latest submission).
        var statusBoard = activeMachines
            .Select(m => new StatusBoardRow(
                Machine:           m,
                LatestSubmission:  recentLookup.TryGetValue(m.Id, out var s) ? s : null))
            .ToList();

        // ── Phase 7.6 — Approved-submissions acknowledge inbox ────────────
        // Per the user's strict-spec reading of the no-defect route, every
        // supervisor-approved checklist routes to BOTH Planner (capture)
        // AND Control Room (acknowledge). The two roles act in parallel —
        // neither blocks the other. This is the Control Room's inbox for
        // single-click acknowledgment, mirroring the Planner's Phase 4.B
        // capture lane.
        //
        // Window: 48h. Older submissions are stale enough that the right
        // action is "investigate why they were missed" not auto-ack.
        var ackCutoff = DateTime.UtcNow.AddHours(-48);
        var pendingAcknowledge = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Where(s => s.AcknowledgedAt == null
                     && s.SubmittedAt >= ackCutoff
                     && s.SupervisorSignedAt != null
                     && (s.Status == ChecklistStatus.Go
                         || s.Status == ChecklistStatus.GoButRepair24H
                         || s.Status == ChecklistStatus.GoTillNextService))
            .OrderBy(s => s.SubmittedAt)
            .Take(50)
            .ToListAsync();
        var acknowledgedToday = await _db.ChecklistSubmissions
            .CountAsync(s => s.AcknowledgedAt != null
                          && s.AcknowledgedAt >= DateTime.UtcNow.Date);

        var now = DateTime.UtcNow;
        ViewBag.Artisans = artisansAll;
        ViewBag.RecentlyDispatched = recentlyDispatched;
        ViewBag.StatusBoard = statusBoard;
        ViewBag.PendingAcknowledge   = pendingAcknowledge;
        ViewBag.AcknowledgedToday    = acknowledgedToday;
        ViewBag.Stats = new {
            Pending          = pending.Count,
            Stale4h          = pending.Count(d => d.PlannerCapturedAt.HasValue &&
                                                  (now - d.PlannerCapturedAt.Value).TotalHours > 4),
            DispatchedToday  = await _db.DefectOrders
                                    .CountAsync(d => d.DispatchedAt != null
                                                  && d.DispatchedAt >= DateTime.UtcNow.Date),
            // Phase 4.B — situational-awareness counters
            FleetOperational = statusBoard.Count(r => r.LatestSubmission?.Status == ChecklistStatus.Go),
            FleetGoBut       = statusBoard.Count(r => r.LatestSubmission?.Status == ChecklistStatus.GoButRepair24H
                                                   || r.LatestSubmission?.Status == ChecklistStatus.GoTillNextService),
            FleetNoGo        = statusBoard.Count(r => r.LatestSubmission?.Status == ChecklistStatus.NoGo),
            FleetStale       = statusBoard.Count(r => r.LatestSubmission == null)
        };
        return View(pending);
    }

    /// <summary>One row in the Shift Status Board — a machine + its
    /// most recent submission in the last 24h (or null if none).</summary>
    public record StatusBoardRow(Machine Machine, ChecklistSubmission? LatestSubmission);

    /// <summary>
    /// Phase 6.6 — JSON snapshot of the dispatch queue depth. Polled
    /// every 30s by the Control Room view; when any number changes
    /// the page surfaces a "new items — refresh" banner. Mirrors the
    /// counters rendered in the page header so the comparison is
    /// apples-to-apples.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> QueueDepth()
    {
        var now         = DateTime.UtcNow;
        var staleCutoff = now.AddHours(-4);   // Control Room SLA is tighter than Planner's

        var pending = await _db.DefectOrders
            .CountAsync(d => d.PlannerCapturedAt != null
                          && d.DispatchedAt      == null
                          && d.RepairStatus      != RepairStatus.Completed);

        // "Stale" for the dispatcher = Planner-captured > 4h ago and still
        // not dispatched. That's our internal SLA before we expect an
        // Artisan to be picked.
        var stale4h = await _db.DefectOrders
            .CountAsync(d => d.PlannerCapturedAt != null
                          && d.DispatchedAt      == null
                          && d.RepairStatus      != RepairStatus.Completed
                          && d.PlannerCapturedAt < staleCutoff);

        // Phase 7.6 — acknowledge inbox depth. Same 48h window the Index
        // page uses so the live-refresh banner triggers when a supervisor
        // approval lands or when the inbox grows past where the dispatcher
        // last cleared it.
        var ackCutoff = now.AddHours(-48);
        var pendingAck = await _db.ChecklistSubmissions
            .CountAsync(s => s.AcknowledgedAt == null
                          && s.SubmittedAt >= ackCutoff
                          && s.SupervisorSignedAt != null
                          && (s.Status == ChecklistStatus.Go
                              || s.Status == ChecklistStatus.GoButRepair24H
                              || s.Status == ChecklistStatus.GoTillNextService));

        return Json(new {
            pending,
            stale4h,
            pendingAck,
            checkedAt = now
        });
    }

    /// <summary>Lightweight option type for the dispatch dropdown.
    /// Carries the artisan's FleetId so the view (or future JS) can
    /// filter by the defect's machine fleet without another round-trip.
    /// Phase 6.3 — <c>OpenLoad</c> is the count of dispatched-but-unresolved
    /// defects on this Artisan's plate; the view uses it to sort the
    /// dropdown so the lightest-loaded Artisan in the eligible fleet is
    /// the default selection.</summary>
    public record ArtisanOption(string Id, string Display, int? FleetId, int OpenLoad);

    /// <summary>
    /// Assign an Artisan to a defect. Refuses if already dispatched
    /// (double-dispatch would notify two artisans for the same job).
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Dispatch(int id, string artisanId)
    {
        if (string.IsNullOrWhiteSpace(artisanId))
        {
            TempData["Error"] = "Pick an Artisan before dispatching.";
            return RedirectToAction("Index");
        }

        var defect = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (defect == null)
        {
            TempData["Error"] = "Defect not found.";
            return RedirectToAction("Index");
        }
        if (defect.PlannerCapturedAt == null)
        {
            TempData["Error"] = $"Defect #{id} hasn't been captured by the Planner yet — refusing to dispatch.";
            return RedirectToAction("Index");
        }
        if (defect.DispatchedAt != null)
        {
            TempData["Error"] = $"Defect #{id} was already dispatched at {defect.DispatchedAt:yyyy-MM-dd HH:mm} — refusing to re-dispatch.";
            return RedirectToAction("Index");
        }

        var artisan = await _users.FindByIdAsync(artisanId);
        if (artisan == null || !artisan.IsActive)
        {
            TempData["Error"] = "Selected Artisan not found or is inactive.";
            return RedirectToAction("Index");
        }

        defect.DispatchedAt       = DateTime.UtcNow;
        defect.DispatchedById     = _users.GetUserId(User);
        defect.AssignedMechanicId = artisanId;
        // Move RepairStatus from Pending to InProgress so the artisan
        // sees it in their active queue (not in any "unassigned" lane
        // that we add later).
        if (defect.RepairStatus == RepairStatus.Pending)
            defect.RepairStatus = RepairStatus.InProgress;
        await _db.SaveChangesAsync();

        // Notify the artisan so it pops up in their bell drop-down + on
        // the mobile app if they're signed in. Fire-and-forget — a
        // notification failure must not roll back the dispatch. The
        // PushAsync signature uses `userId` (not recipientUserId) and
        // doesn't take an actionUrl — the bell view derives the deep
        // link from kind + relatedSubmissionId / relatedMachineId, so
        // we pass those instead and let the inbox renderer route to
        // /Mechanic/DefectDetail when it sees DefectAssigned.
        try
        {
            await _notifications.PushAsync(
                userId:              artisanId,
                kind:                NotificationKinds.DefectAssigned,
                title:               $"Dispatched: {defect.Submission.Machine.MachineNumber}",
                body:                $"{defect.DefectDescription} (jobcard {defect.JobCardNumber ?? "—"})",
                payload:             new {
                    defectOrderId = defect.Id,
                    jobCardNumber = defect.JobCardNumber,
                    machineNumber = defect.Submission.Machine.MachineNumber
                },
                relatedSubmissionId: defect.SubmissionId,
                relatedMachineId:    defect.Submission.MachineId);
        }
        catch (Exception ex)
        {
            // Swallow — the dispatch is recorded, the Artisan can still
            // find it by checking their queue manually.
            System.Diagnostics.Debug.WriteLine($"Dispatch notify failed: {ex.Message}");
        }

        await _audit.LogAsync(
            "controlroom.dispatched",
            targetType: "DefectOrder",
            targetId:   defect.Id,
            payload:    new {
                machineNumber = defect.Submission.Machine.MachineNumber,
                jobCardNumber = defect.JobCardNumber,
                artisanId     = artisanId,
                artisanName   = artisan.FullName
            });

        TempData["Success"] = $"✅ Dispatched {defect.Submission.Machine.MachineNumber} to {artisan.FullName}.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Re-assign an already-dispatched defect to a different Artisan.
    /// Used when the originally-dispatched Artisan went off-shift, is
    /// on leave, or the workshop supervisor needs to rebalance load.
    /// The original Artisan gets a "reassigned away" courtesy
    /// notification; the new one gets the standard "dispatched"
    /// notification.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reassign(int id, string newArtisanId, string? reason)
    {
        if (string.IsNullOrWhiteSpace(newArtisanId))
        {
            TempData["Error"] = "Pick a new Artisan before reassigning.";
            return RedirectToAction("Index");
        }

        var defect = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.AssignedMechanic)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (defect == null)
        {
            TempData["Error"] = "Defect not found.";
            return RedirectToAction("Index");
        }
        if (defect.AssignedMechanicId == null)
        {
            TempData["Error"] = $"Defect #{id} hasn't been dispatched yet — use Dispatch, not Reassign.";
            return RedirectToAction("Index");
        }
        if (defect.RepairStatus == RepairStatus.Completed)
        {
            TempData["Error"] = $"Defect #{id} is already closed — cannot reassign.";
            return RedirectToAction("Index");
        }
        if (string.Equals(defect.AssignedMechanicId, newArtisanId, StringComparison.Ordinal))
        {
            TempData["Error"] = "Defect is already assigned to that Artisan.";
            return RedirectToAction("Index");
        }

        var newArtisan = await _users.FindByIdAsync(newArtisanId);
        if (newArtisan == null || !newArtisan.IsActive)
        {
            TempData["Error"] = "Selected Artisan not found or is inactive.";
            return RedirectToAction("Index");
        }

        var oldArtisanId   = defect.AssignedMechanicId;
        var oldArtisanName = defect.AssignedMechanic?.FullName ?? "previous Artisan";

        defect.AssignedMechanicId = newArtisanId;
        defect.DispatchedAt       = DateTime.UtcNow;   // refresh stamp
        defect.DispatchedById     = _users.GetUserId(User);
        await _db.SaveChangesAsync();

        // Notify the new Artisan (same as a fresh dispatch).
        try
        {
            await _notifications.PushAsync(
                userId:              newArtisanId,
                kind:                NotificationKinds.DefectAssigned,
                title:               $"Reassigned: {defect.Submission.Machine.MachineNumber}",
                body:                $"{defect.DefectDescription} (jobcard {defect.JobCardNumber ?? "—"})",
                payload:             new {
                    defectOrderId   = defect.Id,
                    jobCardNumber   = defect.JobCardNumber,
                    machineNumber   = defect.Submission.Machine.MachineNumber,
                    reason          = reason ?? ""
                },
                relatedSubmissionId: defect.SubmissionId,
                relatedMachineId:    defect.Submission.MachineId);
        }
        catch { /* never fail the reassign on notify error */ }

        await _audit.LogAsync(
            "controlroom.reassigned",
            targetType: "DefectOrder",
            targetId:   defect.Id,
            payload:    new {
                machineNumber  = defect.Submission.Machine.MachineNumber,
                jobCardNumber  = defect.JobCardNumber,
                oldArtisanId   = oldArtisanId,
                oldArtisanName = oldArtisanName,
                newArtisanId   = newArtisanId,
                newArtisanName = newArtisan.FullName,
                reason         = reason ?? "(not specified)"
            });

        TempData["Success"] = $"🔄 Reassigned {defect.Submission.Machine.MachineNumber} from {oldArtisanName} to {newArtisan.FullName}.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Escalation flag — used when no Artisan is available to dispatch
    /// and the operations team needs to know about a stuck jobcard.
    /// The defect stays in the dispatch queue (so it's still visible) but
    /// is tagged in the resolution notes; admins get a notification.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Escalate(int id, string? reason)
    {
        var defect = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (defect == null)
        {
            TempData["Error"] = "Defect not found.";
            return RedirectToAction("Index");
        }
        if (defect.RepairStatus == RepairStatus.Completed)
        {
            TempData["Error"] = $"Defect #{id} is already closed — nothing to escalate.";
            return RedirectToAction("Index");
        }

        var trimmedReason = string.IsNullOrWhiteSpace(reason)
            ? "No Artisan available"
            : reason.Trim();

        // Stamp the escalation into the resolution notes so it's visible
        // on every screen that shows this defect. Append rather than
        // replace in case the defect has been escalated before.
        var existing = defect.ResolutionNotes ?? "";
        var stamp    = $"[ESCALATED {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC by Control Room: {trimmedReason}]";
        defect.ResolutionNotes = string.IsNullOrEmpty(existing) ? stamp : $"{existing}\n{stamp}";
        await _db.SaveChangesAsync();

        // Notify every admin so somebody can intervene (e.g. cancel
        // someone's leave, raise to maintenance manager, etc.).
        try
        {
            var admins = await _users.GetUsersInRoleAsync("Admin");
            foreach (var admin in admins.Where(a => a.IsActive))
            {
                await _notifications.PushAsync(
                    userId:              admin.Id,
                    kind:                NotificationKinds.DefectAssigned,   // reuse for now
                    title:               $"⚠ Escalated: {defect.Submission.Machine.MachineNumber}",
                    body:                $"Control Room couldn't dispatch: {trimmedReason}",
                    payload:             new {
                        defectOrderId = defect.Id,
                        jobCardNumber = defect.JobCardNumber,
                        machineNumber = defect.Submission.Machine.MachineNumber,
                        reason        = trimmedReason
                    },
                    relatedSubmissionId: defect.SubmissionId,
                    relatedMachineId:    defect.Submission.MachineId);
            }
        }
        catch { /* notify failure must not break escalation */ }

        await _audit.LogAsync(
            "controlroom.escalated",
            targetType: "DefectOrder",
            targetId:   defect.Id,
            payload:    new {
                machineNumber = defect.Submission.Machine.MachineNumber,
                jobCardNumber = defect.JobCardNumber,
                reason        = trimmedReason
            });

        TempData["Success"] = $"⚠ Escalated defect #{id} ({defect.Submission.Machine.MachineNumber}). Admins notified.";
        return RedirectToAction("Index");
    }

    // ═════ Phase 7.6 — Approved-submissions acknowledge inbox ═════════════
    /// <summary>
    /// Acknowledge a single supervisor-approved submission. Click-only
    /// (no signature) because acknowledgment is "I've seen this and
    /// incorporated it into my shift situational awareness" — not a
    /// risk-transfer decision. Stamps <c>AcknowledgedAt + AcknowledgedByControlRoomId</c>
    /// + writes <c>controlroom.acknowledged</c> audit.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Acknowledge(int id)
    {
        var controlRoomId = _users.GetUserId(User)!;
        var sub = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null)
        {
            TempData["Error"] = "Submission not found.";
            return RedirectToAction("Index");
        }
        if (sub.SupervisorSignedAt == null)
        {
            TempData["Error"] = $"Submission #{id} hasn't been supervisor-approved yet — nothing to acknowledge.";
            return RedirectToAction("Index");
        }
        if (sub.AcknowledgedAt != null)
        {
            TempData["Error"] = $"Submission #{id} was already acknowledged at {sub.AcknowledgedAt:yyyy-MM-dd HH:mm}.";
            return RedirectToAction("Index");
        }

        sub.AcknowledgedAt             = DateTime.UtcNow;
        sub.AcknowledgedByControlRoomId = controlRoomId;
        await _db.SaveChangesAsync();

        try
        {
            await _audit.LogAsync(
                "controlroom.acknowledged",
                targetType: "ChecklistSubmission",
                targetId:   sub.Id,
                payload:    new {
                    machineNumber = sub.Machine.MachineNumber,
                    status        = sub.Status.ToString(),
                    controlRoom   = User.Identity?.Name
                });
        }
        catch { /* audit failure must not block acknowledgment */ }

        TempData["Success"] = $"✅ Acknowledged #{id} ({sub.Machine.MachineNumber}).";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Bulk-acknowledge every supervisor-approved submission in the 48h
    /// window. End-of-shift "clear the inbox" pattern matching the
    /// Planner's Phase 6.2 bulk-capture and Supervisor's Phase 7.5
    /// bulk-approve. One audit row per submission with <c>bulk: true</c>
    /// so investigators can distinguish bulk from individual acks.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AcknowledgeAll()
    {
        var controlRoomId = _users.GetUserId(User)!;
        var ackCutoff = DateTime.UtcNow.AddHours(-48);
        var batch = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Where(s => s.AcknowledgedAt == null
                     && s.SubmittedAt >= ackCutoff
                     && s.SupervisorSignedAt != null
                     && (s.Status == ChecklistStatus.Go
                         || s.Status == ChecklistStatus.GoButRepair24H
                         || s.Status == ChecklistStatus.GoTillNextService))
            .ToListAsync();

        if (batch.Count == 0)
        {
            TempData["Error"] = "Inbox is already clear — nothing to acknowledge.";
            return RedirectToAction("Index");
        }

        var now = DateTime.UtcNow;
        foreach (var s in batch)
        {
            s.AcknowledgedAt             = now;
            s.AcknowledgedByControlRoomId = controlRoomId;
        }
        await _db.SaveChangesAsync();

        foreach (var s in batch)
        {
            try
            {
                await _audit.LogAsync(
                    "controlroom.acknowledged",
                    targetType: "ChecklistSubmission",
                    targetId:   s.Id,
                    payload:    new {
                        machineNumber = s.Machine.MachineNumber,
                        status        = s.Status.ToString(),
                        controlRoom   = User.Identity?.Name,
                        bulk          = true
                    });
            }
            catch { /* never break the bulk on a single audit failure */ }
        }

        TempData["Success"] = $"✅ Bulk-acknowledged {batch.Count} approved submission{(batch.Count == 1 ? "" : "s")}.";
        return RedirectToAction("Index");
    }
}
