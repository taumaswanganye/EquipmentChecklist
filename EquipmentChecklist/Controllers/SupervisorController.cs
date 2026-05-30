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
    private readonly ILogger<SupervisorController> _log;

    public SupervisorController(ApplicationDbContext db,
                                ChecklistService svc,
                                UserManager<ApplicationUser> users,
                                EmailService email,
                                ILogger<SupervisorController> log)
    {
        _db    = db;
        _svc   = svc;
        _users = users;
        _email = email;
        _log   = log;
    }

    // ── Sign-Off Queue ────────────────────────────────────────────────────────
    public async Task<IActionResult> Index()
    {
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

        var pending = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .Where(s => s.Status == ChecklistStatus.GoButRepair24H
                     && s.SupervisorId == null
                     && assignedOperatorIds.Contains(s.OperatorId))
            .OrderBy(s => s.SubmittedAt)
            .ToListAsync();

        // Pass available mechanics so supervisor can pick who to assign on reject
        ViewBag.Mechanics = await _users.GetUsersInRoleAsync("Mechanic");

        return View(pending);
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
    public async Task<IActionResult> NoGoMachines()
    {
        var machines = await _db.Machines
            .Include(m => m.Submissions)
                .ThenInclude(s => s.DefectOrders)
            .Where(m => m.IsImmobilised)
            .ToListAsync();

        return View(machines);
    }
}
