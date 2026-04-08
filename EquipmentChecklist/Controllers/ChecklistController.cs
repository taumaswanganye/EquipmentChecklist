using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Controllers;

[Authorize]
public class ChecklistController : Controller
{
    private readonly ApplicationDbContext         _db;
    private readonly ChecklistService             _svc;
    private readonly UserManager<ApplicationUser> _users;
    private readonly PdfService                   _pdf;

    public ChecklistController(ApplicationDbContext db,
                               ChecklistService svc,
                               UserManager<ApplicationUser> users,
                               PdfService pdf)
    {
        _db    = db;
        _svc   = svc;
        _users = users;
        _pdf   = pdf;
    }

    // Operator Dashboard - only operators see their assigned machines
    [Authorize(Roles = "Operator,Supervisor,Mechanic")]
    public async Task<IActionResult> Index()
    {
        var userId = _users.GetUserId(User)!;
        var stats  = await _svc.GetDashboardStatsAsync();

        var assignments = await _db.MachineAssignments
            .Include(a => a.Machine).ThenInclude(m => m.Template)
            .Where(a => a.OperatorId == userId && a.IsActive)
            .ToListAsync();

        ViewBag.Stats = stats;
        return View(assignments);
    }

    // Start Checklist - Operators ONLY
    [HttpGet("/Checklist/Start/{machineId:int}")]
    [Authorize(Roles = "Operator")]
    public async Task<IActionResult> Start(int machineId)
    {
        var userId = _users.GetUserId(User)!;

        var assignment = await _db.MachineAssignments
            .FirstOrDefaultAsync(a => a.MachineId == machineId
                                   && a.OperatorId == userId
                                   && a.IsActive);
        if (assignment == null)
        {
            TempData["Error"] = "You are not assigned to this machine.";
            return RedirectToAction("Index");
        }

        var machine = await _db.Machines
            .Include(m => m.Template)
            .ThenInclude(t => t!.Items)
            .FirstOrDefaultAsync(m => m.Id == machineId);

        if (machine == null) { TempData["Error"] = "Machine not found."; return RedirectToAction("Index"); }
        if (machine.IsImmobilised) { TempData["Error"] = $"Machine {machine.MachineNumber} is immobilised."; return RedirectToAction("Index"); }

        if (machine.Template != null)
            machine.Template.Items = machine.Template.Items.OrderBy(i => i.SortOrder).ToList();

        return View(machine);
    }

    // Submit - Operators ONLY, Shift + KM required
    [HttpPost("/Checklist/Submit")]
    [Authorize(Roles = "Operator")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(SubmitChecklistDto dto)
    {
        if (dto.Shift == 0)
        {
            TempData["Error"] = "Shift is required.";
            return RedirectToAction("Start", new { machineId = dto.MachineId });
        }
        if (!dto.KmOrHourMeter.HasValue)
        {
            TempData["Error"] = "KM / Hour Meter reading is required.";
            return RedirectToAction("Start", new { machineId = dto.MachineId });
        }

        var userId = _users.GetUserId(User)!;
        try
        {
            var submission = await _svc.ProcessSubmissionAsync(dto, userId);
            return RedirectToAction("Result", new { id = submission.Id });
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction("Start", new { machineId = dto.MachineId });
        }
    }

    // Result page
    [HttpGet("/Checklist/Result/{id:int}")]
    public async Task<IActionResult> Result(int id)
    {
        var submission = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .Include(s => s.DefectOrders)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (submission == null) { TempData["Error"] = "Submission not found."; return RedirectToAction("Index"); }
        return View(submission);
    }

    // View PDF inline in browser
    [HttpGet("/Checklist/ViewPdf/{id:int}")]
    public async Task<IActionResult> ViewPdf(int id)
    {
        var submission = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (submission == null) return NotFound();
        var bytes = _pdf.GenerateChecklistPdf(submission);
        return File(bytes, "application/pdf");
    }

    // Download PDF
    [HttpGet("/Checklist/Pdf/{id:int}")]
    public async Task<IActionResult> DownloadPdf(int id)
    {
        var submission = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (submission == null) return NotFound();
        var bytes    = _pdf.GenerateChecklistPdf(submission);
        var fileName = $"Checklist_{submission.Machine.MachineNumber}_{submission.SubmittedAt:yyyyMMdd_HHmm}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    // History
    [HttpGet]
    public async Task<IActionResult> History()
    {
        var userId = _users.GetUserId(User)!;
        var query  = _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Items)
            .AsQueryable();

        if (!User.IsInRole("Admin") && !User.IsInRole("Supervisor"))
            query = query.Where(s => s.OperatorId == userId);

        var submissions = await query.OrderByDescending(s => s.SubmittedAt).Take(100).ToListAsync();
        return View(submissions);
    }
}
