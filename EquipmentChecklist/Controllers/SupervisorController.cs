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

    public SupervisorController(ApplicationDbContext db,
                                ChecklistService svc,
                                UserManager<ApplicationUser> users)
    {
        _db    = db;
        _svc   = svc;
        _users = users;
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
    public async Task<IActionResult> SignOff(int id, int resolution)
    {
        var supervisorId = _users.GetUserId(User)!;
        var status = resolution == 3
            ? ChecklistStatus.GoTillNextService
            : ChecklistStatus.GoButRepair24H;

        try
        {
            await _svc.SupervisorSignOffAsync(id, supervisorId, status);
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
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "Submission rejected. Machine immobilised and defects sent to mechanic.";
        return RedirectToAction("Index");
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
