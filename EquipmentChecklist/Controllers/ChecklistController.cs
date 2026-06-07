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
    private readonly CompetencyService            _competency;

    public ChecklistController(ApplicationDbContext db,
                               ChecklistService svc,
                               UserManager<ApplicationUser> users,
                               PdfService pdf,
                               CompetencyService competency)
    {
        _db          = db;
        _svc         = svc;
        _users       = users;
        _pdf         = pdf;
        _competency  = competency;
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

        // ── Competency gate (MHSA Section 22(a)) ──────────────────────
        // Same check as on Submit, but here it fires BEFORE the form is
        // rendered so an unauthorised operator never sees the 30 items.
        // We pass structured TempData keys (CompetencyBlocked + the
        // metadata) so the Index view can render a popup modal with the
        // specifics rather than a generic alert.
        if (!await _competency.IsCompetentAsync(userId, machine.Type))
        {
            // Look up the freshest competency (active or expired) for
            // this machine type so the popup can tell the operator when
            // their last cert expired. Null = they've never had one.
            var lastExpiry = await _db.OperatorCompetencies
                .Where(c => c.OperatorId == userId && c.MachineType == machine.Type)
                .OrderByDescending(c => c.ExpiresAt)
                .Select(c => (DateTime?)c.ExpiresAt)
                .FirstOrDefaultAsync();

            TempData["CompetencyBlocked"]            = "1";
            TempData["CompetencyBlockedMachineType"] = machine.Type.ToString();
            TempData["CompetencyBlockedMachineNum"]  = machine.MachineNumber;
            TempData["CompetencyBlockedMachineName"] = machine.MachineName;
            TempData["CompetencyBlockedLastExpiry"]  = lastExpiry?.ToString("yyyy-MM-dd") ?? "";

            return RedirectToAction("Index");
        }

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
        if (string.IsNullOrWhiteSpace(dto.OperatorSignature))
        {
            TempData["Error"] = "Your digital signature is required.";
            return RedirectToAction("Start", new { machineId = dto.MachineId });
        }

        var userId = _users.GetUserId(User)!;

        // ── Competency gate (MHSA Section 22(a)) ──────────────────────
        // Look up the machine's type and verify the operator holds a
        // current competency. Admins bypass inside IsCompetentAsync.
        var machine = await _db.Machines.AsNoTracking()
            .Where(m => m.Id == dto.MachineId)
            .Select(m => new { m.Type, m.MachineNumber })
            .FirstOrDefaultAsync();
        if (machine == null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToAction("Start", new { machineId = dto.MachineId });
        }
        if (!await _competency.IsCompetentAsync(userId, machine.Type))
        {
            // Surface the same popup modal Index uses when the operator
            // is gated at machine selection. Cheaper than re-implementing
            // the same warning UI in the Start view.
            var lastExpiry = await _db.OperatorCompetencies
                .Where(c => c.OperatorId == userId && c.MachineType == machine.Type)
                .OrderByDescending(c => c.ExpiresAt)
                .Select(c => (DateTime?)c.ExpiresAt)
                .FirstOrDefaultAsync();

            TempData["CompetencyBlocked"]            = "1";
            TempData["CompetencyBlockedMachineType"] = machine.Type.ToString();
            TempData["CompetencyBlockedMachineNum"]  = machine.MachineNumber;
            TempData["CompetencyBlockedMachineName"] = "(submission refused)";
            TempData["CompetencyBlockedLastExpiry"]  = lastExpiry?.ToString("yyyy-MM-dd") ?? "";
            return RedirectToAction("Index");
        }

        try
        {
            var submission = await _svc.ProcessSubmissionAsync(dto, userId);
            return RedirectToAction("Result", new { id = submission.Id });
        }
        catch (Exception ex)
        {
            // EF Core's DbUpdateException always wraps the actual SQL error
            // (column missing, FK violation, NOT NULL, etc.) one or two levels
            // deep. Surface the leaf message so the operator — and the
            // diagnostic banner on the Start page — see what actually failed,
            // not just the framework's generic "An error occurred while
            // saving the entity changes."
            TempData["Error"] = RootCauseOf(ex);
            return RedirectToAction("Start", new { machineId = dto.MachineId });
        }
    }

    /// <summary>Walk the InnerException chain and return the deepest
    /// non-null message. EF wraps Postgres errors twice (DbUpdateException
    /// → PostgresException); the inner one is the useful one.</summary>
    private static string RootCauseOf(Exception ex)
    {
        var cur = ex;
        while (cur.InnerException is not null) cur = cur.InnerException;
        return cur.Message;
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
            .Include(s => s.Supervisor)
            .Include(s => s.Mechanic)
            .Include(s => s.RejectedMechanic)
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
            .Include(s => s.Supervisor)
            .Include(s => s.Mechanic)
            .Include(s => s.RejectedMechanic)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (submission == null) return NotFound();
        var bytes    = _pdf.GenerateChecklistPdf(submission);
        var fileName = $"Checklist_{submission.Machine.MachineNumber}_{submission.SubmittedAt:yyyyMMdd_HHmm}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    // History
    [HttpGet]
    public async Task<IActionResult> History([FromQuery] ListFilter filter)
    {
        // Resolve preset → from/to so a chip click is enough on its own.
        FilterPresets.Apply(filter);

        var userId = _users.GetUserId(User)!;
        var query  = _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items)
            .AsQueryable();

        if (!User.IsInRole("Admin") && !User.IsInRole("Supervisor"))
            query = query.Where(s => s.OperatorId == userId);

        // Date-range filter pushed down to SQL — much cheaper than pulling
        // the full 100-row window and slicing in memory.
        query = query.ApplyDateRange(filter, s => s.SubmittedAt);

        // Cap at 500 so a wide date range doesn't OOM the server. The
        // existing client-side `data-paginate` then pages 5 rows at a time.
        var page = await query
            .OrderByDescending(s => s.SubmittedAt)
            .Take(500)
            .ToListAsync();

        // Search happens in memory so we can hit navigation properties
        // (Machine.MachineNumber, Operator.FullName) without fighting the
        // EF expression translator. With the 500-row ceiling above, this is
        // a no-op cost.
        var submissions = page.ApplySearchInMemory(
            filter,
            s => s.Machine.MachineNumber,
            s => s.Machine.MachineName,
            s => s.Operator.FullName,
            s => s.Operator.EmployeeNumber);

        ViewBag.Filter        = filter;
        ViewBag.TotalUnfiltered = page.Count;
        return View(submissions);
    }
}

