using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ClosedXML.Excel;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace EquipmentChecklist.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly ApplicationDbContext         _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IWebHostEnvironment          _env;

    public AdminController(ApplicationDbContext db,
                           UserManager<ApplicationUser> users,
                           IWebHostEnvironment env)
    {
        _db    = db;
        _users = users;
        _env   = env;
    }

    // ── Image upload helper ───────────────────────────────────────────────────
    private async Task<string?> SaveItemImageAsync(IFormFile? file, int itemId)
    {
        if (file == null || file.Length == 0) return null;

        var ext     = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        if (!allowed.Contains(ext)) return null;

        var dir = Path.Combine(_env.WebRootPath, "item-images");
        Directory.CreateDirectory(dir);

        var fileName = $"{itemId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}{ext}";
        var fullPath = Path.Combine(dir, fileName);

        await using var stream = new FileStream(fullPath, FileMode.Create);
        await file.CopyToAsync(stream);

        return $"/item-images/{fileName}";
    }

    // ── Fleet Machines ────────────────────────────────────────────────────────
    public async Task<IActionResult> Index()
    {
        var machines = await _db.Machines
            .Include(m => m.Template)
            .OrderBy(m => m.MachineNumber)
            .ToListAsync();

        ViewBag.MachineCount   = machines.Count;
        ViewBag.EmployeeCount  = await _db.Users.CountAsync();
        ViewBag.PendingDefects = await _db.DefectOrders
            .CountAsync(d => d.RepairStatus != RepairStatus.Completed);

        return View(machines);
    }

    // ── Create Machine (simple form) ─────────────────────────────────────────
    [HttpGet]
    public IActionResult CreateMachine() => View(new Machine());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateMachine(Machine model)
    {
        if (!ModelState.IsValid) return View(model);

        _db.Machines.Add(model);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Machine {model.MachineNumber} added.";
        return RedirectToAction("Index");
    }

    // ── Edit Machine ──────────────────────────────────────────────────────────
    private async Task<List<string>> GetMachineTypeSuggestionsAsync()
    {
        var builtIn = MachineDisplayExtensions.MachineTypeLabels.Values;
        var custom = await _db.Machines
            .Where(m => m.TypeName != null && m.TypeName != "")
            .Select(m => m.TypeName!)
            .Distinct()
            .ToListAsync();

        return builtIn
            .Concat(custom)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    [HttpGet]
    public async Task<IActionResult> EditMachine(int id)
    {
        var m = await _db.Machines.FindAsync(id);
        if (m == null) return RedirectToAction("Index");
        ViewBag.TypeSuggestions = await GetMachineTypeSuggestionsAsync();
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditMachine(Machine model)
    {
        ViewBag.TypeSuggestions = await GetMachineTypeSuggestionsAsync();

        // The form posts TypeName (free-text). The enum-bound Type field is no
        // longer in the form, so we resolve it from the typed string here.
        if (string.IsNullOrWhiteSpace(model.TypeName))
        {
            ModelState.AddModelError("TypeName", "Type is required.");
            return View(model);
        }
        if (!ModelState.IsValid) return View(model);

        var existing = await _db.Machines.FindAsync(model.Id);
        if (existing == null) return RedirectToAction("Index");

        // Duplicate MachineNumber check (exclude self)
        var dup = await _db.Machines
            .AnyAsync(m => m.MachineNumber == model.MachineNumber && m.Id != model.Id);
        if (dup)
        {
            ModelState.AddModelError("MachineNumber",
                $"Machine number '{model.MachineNumber}' is already used by another machine.");
            return View(model);
        }

        var typeName = model.TypeName.Trim();
        var resolved = MachineDisplayExtensions.TryResolveMachineType(typeName);

        existing.MachineName   = model.MachineName.Trim();
        existing.MachineNumber = model.MachineNumber.Trim();
        existing.Type          = resolved ?? existing.Type;     // keep prior enum if no match
        existing.TypeName      = typeName;
        existing.Description   = model.Description?.Trim();
        existing.IsActive      = model.IsActive;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Machine {existing.MachineNumber} updated.";
        return RedirectToAction("Index");
    }

    // ── Manage Checklist Items for a machine ──────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> ManageItems(int id)
    {
        var machine = await _db.Machines
            .Include(m => m.Template)
                .ThenInclude(t => t!.Items.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(m => m.Id == id);
        if (machine == null) return RedirectToAction("Index");
        return View(machine);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddTemplateItem(
        int machineId,
        string itemName,
        bool isNoGoItem,
        string? statusLabel,
        string? action,
        string? inOrderCondition,
        string? defectCondition,
        IFormFile? imageFile,
        string? iconLibraryPath)
    {
        if (string.IsNullOrWhiteSpace(itemName))
        {
            TempData["Error"] = "Item name is required.";
            return RedirectToAction("ManageItems", new { id = machineId });
        }

        // Create template if the machine doesn't have one yet
        var machine  = await _db.Machines.Include(m => m.Template).FirstOrDefaultAsync(m => m.Id == machineId);
        if (machine == null) return RedirectToAction("Index");

        ChecklistTemplate template;
        if (machine.Template == null)
        {
            template = new ChecklistTemplate
            {
                MachineId   = machineId,
                MachineType = machine.Type,
                Name        = $"{machine.MachineName} Checklist"
            };
            _db.ChecklistTemplates.Add(template);
            await _db.SaveChangesAsync();
        }
        else
        {
            template = machine.Template;
        }

        var maxOrder = await _db.ChecklistTemplateItems
            .Where(i => i.TemplateId == template.Id)
            .MaxAsync(i => (int?)i.SortOrder) ?? 0;

        var newItem = new ChecklistTemplateItem
        {
            TemplateId       = template.Id,
            ItemName         = itemName.Trim(),
            Section          = "General",
            SortOrder        = maxOrder + 1,
            IsNoGoItem       = isNoGoItem,
            StatusLabel      = statusLabel?.Trim(),
            Action           = action?.Trim(),
            InOrderCondition = inOrderCondition?.Trim(),
            DefectCondition  = defectCondition?.Trim()
        };
        _db.ChecklistTemplateItems.Add(newItem);
        await _db.SaveChangesAsync();

        // Icon: prefer a library pick; fall back to an inline file upload.
        if (!string.IsNullOrWhiteSpace(iconLibraryPath))
        {
            newItem.IconPath = iconLibraryPath.Trim();
            await _db.SaveChangesAsync();
        }
        else
        {
            var imagePath = await SaveItemImageAsync(imageFile, newItem.Id);
            if (imagePath != null)
            {
                newItem.IconPath = imagePath;
                await _db.SaveChangesAsync();
            }
        }

        TempData["Success"] = $"Item '{itemName.Trim()}' added.";
        return RedirectToAction("ManageItems", new { id = machineId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTemplateItem(
        int itemId,
        int machineId,
        string itemName,
        bool isNoGoItem,
        string? statusLabel,
        string? action,
        string? inOrderCondition,
        string? defectCondition,
        IFormFile? imageFile,
        string? iconLibraryPath,
        bool removeImage = false)
    {
        var item = await _db.ChecklistTemplateItems.FindAsync(itemId);
        if (item == null) return RedirectToAction("ManageItems", new { id = machineId });

        if (string.IsNullOrWhiteSpace(itemName))
        {
            TempData["Error"] = "Item name is required.";
            return RedirectToAction("ManageItems", new { id = machineId });
        }

        item.ItemName         = itemName.Trim();
        item.IsNoGoItem       = isNoGoItem;
        item.StatusLabel      = statusLabel?.Trim();
        item.Action           = action?.Trim();
        item.InOrderCondition = inOrderCondition?.Trim();
        item.DefectCondition  = defectCondition?.Trim();

        // Icon resolution priority:
        //   1. removeImage flag wins  →  null
        //   2. iconLibraryPath set    →  use library reference (don't touch files)
        //   3. imageFile uploaded     →  save inline, replace previous inline file
        //   4. else                   →  leave whatever was there
        if (removeImage)
        {
            if (!string.IsNullOrEmpty(item.IconPath) && item.IconPath.StartsWith("/item-images/"))
            {
                var oldFile = Path.Combine(_env.WebRootPath, item.IconPath.TrimStart('/'));
                if (System.IO.File.Exists(oldFile)) System.IO.File.Delete(oldFile);
            }
            item.IconPath = null;
        }
        else if (!string.IsNullOrWhiteSpace(iconLibraryPath))
        {
            // If the previous icon was an inline upload, clean it off disk.
            if (!string.IsNullOrEmpty(item.IconPath)
                && item.IconPath.StartsWith("/item-images/")
                && item.IconPath != iconLibraryPath)
            {
                var oldFile = Path.Combine(_env.WebRootPath, item.IconPath.TrimStart('/'));
                if (System.IO.File.Exists(oldFile)) System.IO.File.Delete(oldFile);
            }
            item.IconPath = iconLibraryPath.Trim();
        }
        else
        {
            var newPath = await SaveItemImageAsync(imageFile, itemId);
            if (newPath != null)
            {
                if (!string.IsNullOrEmpty(item.IconPath) && item.IconPath.StartsWith("/item-images/"))
                {
                    var oldFile = Path.Combine(_env.WebRootPath, item.IconPath.TrimStart('/'));
                    if (System.IO.File.Exists(oldFile)) System.IO.File.Delete(oldFile);
                }
                item.IconPath = newPath;
            }
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = $"Item '{item.ItemName}' updated.";
        return RedirectToAction("ManageItems", new { id = machineId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTemplateItem(int itemId, int machineId)
    {
        var item = await _db.ChecklistTemplateItems.FindAsync(itemId);
        if (item != null)
        {
            _db.ChecklistTemplateItems.Remove(item);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Checklist item removed.";
        }
        return RedirectToAction("ManageItems", new { id = machineId });
    }

    // ── Immobilise / Clear ────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ImmobiliseMachine(int id, string reason)
    {
        var m = await _db.Machines.FindAsync(id);
        if (m != null)
        {
            m.IsImmobilised     = true;
            m.ImmobilisedReason = reason;
            await _db.SaveChangesAsync();
            TempData["Success"] = $"{m.MachineNumber} immobilised.";
        }
        return RedirectToAction("Index");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearImmobilisation(int id)
    {
        var m = await _db.Machines.FindAsync(id);
        if (m != null)
        {
            m.IsImmobilised     = false;
            m.ImmobilisedReason = null;
            await _db.SaveChangesAsync();
            TempData["Success"] = $"{m.MachineNumber} immobilisation cleared.";
        }
        return RedirectToAction("Index");
    }

    // ── Employees ─────────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Employees()
    {
        var users = await _db.Users
            .OrderBy(u => u.FullName)
            .ToListAsync();
        return View(users);
    }

    [HttpGet]
    public IActionResult CreateEmployee() => View();

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateEmployee(
        string fullName, string employeeNumber,
        string email,    string password,
        UserRole role)
    {
        var user = new ApplicationUser
        {
            UserName       = email,
            Email          = email,
            FullName       = fullName,
            EmployeeNumber = employeeNumber,
            Role           = role,
            IsActive       = true,
            EmailConfirmed = true
        };

        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
            return View();
        }

        await _users.AddToRoleAsync(user, role.ToString());
        TempData["Success"] = $"Employee {fullName} created.";
        return RedirectToAction("Employees");
    }

    // ── Assignments ───────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Assignments()
    {
        var machineAssignmentsRaw = await _db.MachineAssignments
            .Include(a => a.Machine)
            .Include(a => a.Operator)
            .Include(a => a.Mechanic)
            .Where(a => a.IsActive)
            .OrderByDescending(a => a.AssignedFrom)
            .ToListAsync();

        // Defensive: keep only the most-recent active assignment per machine.
        // Older "active" rows can exist if AssignMachine ever raced or if data
        // was edited directly. We deactivate the duplicates here so the page
        // doesn't crash on ToDictionary and the state stays clean.
        var machineAssignments = machineAssignmentsRaw
            .GroupBy(a => a.MachineId)
            .Select(g =>
            {
                var keep = g.First(); // already ordered desc by AssignedFrom
                foreach (var dupe in g.Skip(1)) dupe.IsActive = false;
                return keep;
            })
            .OrderBy(a => a.Machine.MachineNumber)
            .ToList();
        if (machineAssignmentsRaw.Count != machineAssignments.Count)
            await _db.SaveChangesAsync();

        var supervisorAssignmentsRaw = await _db.OperatorSupervisorAssignments
            .Include(a => a.Operator)
            .Include(a => a.Supervisor)
            .Where(a => a.IsActive)
            .OrderByDescending(a => a.AssignedAt)
            .ToListAsync();

        // Same defensive de-dupe for operator→supervisor assignments.
        var supervisorAssignments = supervisorAssignmentsRaw
            .GroupBy(a => a.OperatorId)
            .Select(g =>
            {
                var keep = g.First();
                foreach (var dupe in g.Skip(1)) dupe.IsActive = false;
                return keep;
            })
            .OrderBy(a => a.Operator.FullName)
            .ToList();
        if (supervisorAssignmentsRaw.Count != supervisorAssignments.Count)
            await _db.SaveChangesAsync();

        // Maps for dropdown hints — safe now that the source lists are unique.
        var machineAssignmentMap = machineAssignments
            .ToDictionary(a => a.MachineId, a => a.Operator.FullName);
        var operatorSupervisorMap = supervisorAssignments
            .ToDictionary(a => a.OperatorId, a => a.Supervisor.FullName);

        ViewBag.Machines              = await _db.Machines.Where(m => m.IsActive).OrderBy(m => m.MachineNumber).ToListAsync();
        ViewBag.Operators             = await _users.GetUsersInRoleAsync("Operator");
        ViewBag.Mechanics             = await _users.GetUsersInRoleAsync("Mechanic");
        ViewBag.Supervisors           = await _users.GetUsersInRoleAsync("Supervisor");
        ViewBag.SupervisorAssignments = supervisorAssignments;
        ViewBag.MachineAssignmentMap  = machineAssignmentMap;
        ViewBag.OperatorSupervisorMap = operatorSupervisorMap;
        ViewBag.ActiveTab             = TempData["ActiveTab"] as string ?? "machine";

        return View(machineAssignments);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignMachine(int machineId, string operatorId, string? mechanicId)
    {
        TempData["ActiveTab"] = "machine";

        var machine = await _db.Machines.FindAsync(machineId);
        if (machine == null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToAction("Assignments");
        }
        if (machine.IsImmobilised)
        {
            TempData["Error"] = $"Machine {machine.MachineNumber} is immobilised and cannot be assigned to anyone.";
            return RedirectToAction("Assignments");
        }
        if (string.IsNullOrWhiteSpace(mechanicId))
        {
            TempData["Error"] = "Please select a responsible mechanic before assigning the machine.";
            return RedirectToAction("Assignments");
        }

        // Deactivate any existing assignment (handles both new assign and reassign)
        var existing = await _db.MachineAssignments
            .Where(a => a.MachineId == machineId && a.IsActive)
            .ToListAsync();
        bool isReassign = existing.Any();
        existing.ForEach(a => a.IsActive = false);

        _db.MachineAssignments.Add(new MachineAssignment
        {
            MachineId    = machineId,
            OperatorId   = operatorId,
            MechanicId   = mechanicId,
            AssignedFrom = DateTime.UtcNow,
            IsActive     = true
        });

        await _db.SaveChangesAsync();
        TempData["Success"] = isReassign ? "Machine reassigned successfully." : "Machine assigned successfully.";
        return RedirectToAction("Assignments");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UnassignMachine(int assignmentId)
    {
        TempData["ActiveTab"] = "machine";
        var a = await _db.MachineAssignments.FindAsync(assignmentId);
        if (a != null)
        {
            a.IsActive = false;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Assignment removed.";
        }
        return RedirectToAction("Assignments");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignOperatorToSupervisor(string operatorId, string supervisorId)
    {
        TempData["ActiveTab"] = "supervisor";

        // Deactivate existing assignment for this operator (handles reassign too)
        var existing = await _db.OperatorSupervisorAssignments
            .Where(a => a.OperatorId == operatorId && a.IsActive)
            .ToListAsync();
        bool isReassign = existing.Any();
        existing.ForEach(a => a.IsActive = false);

        _db.OperatorSupervisorAssignments.Add(new OperatorSupervisorAssignment
        {
            OperatorId   = operatorId,
            SupervisorId = supervisorId,
            AssignedAt   = DateTime.UtcNow,
            IsActive     = true
        });

        await _db.SaveChangesAsync();
        TempData["Success"] = isReassign ? "Operator reassigned to supervisor successfully." : "Operator assigned to supervisor successfully.";
        return RedirectToAction("Assignments");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UnassignOperatorFromSupervisor(int assignmentId)
    {
        TempData["ActiveTab"] = "supervisor";
        var a = await _db.OperatorSupervisorAssignments.FindAsync(assignmentId);
        if (a != null)
        {
            a.IsActive = false;
            await _db.SaveChangesAsync();
            TempData["Success"] = "Supervisor assignment removed.";
        }
        return RedirectToAction("Assignments");
    }

    // ── Reports ───────────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Reports(DateTime? from, DateTime? to)
    {
        from ??= DateTime.UtcNow.Date.AddDays(-30);
        to   ??= DateTime.UtcNow.Date.AddDays(1);

        var submissions = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items)
            .Where(s => s.SubmittedAt >= from && s.SubmittedAt <= to)
            .OrderByDescending(s => s.SubmittedAt)
            .ToListAsync();

        ViewBag.From = from;
        ViewBag.To   = to;
        return View(submissions);
    }

    // ── Report exports ────────────────────────────────────────────────────────
    private async Task<List<ChecklistSubmission>> GetReportSubmissionsAsync(DateTime? from, DateTime? to)
    {
        from ??= DateTime.UtcNow.Date.AddDays(-30);
        to   ??= DateTime.UtcNow.Date.AddDays(1);

        return await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Supervisor)
            .Include(s => s.Items)
            .Where(s => s.SubmittedAt >= from && s.SubmittedAt <= to)
            .OrderByDescending(s => s.SubmittedAt)
            .ToListAsync();
    }

    private static string FormatStatus(ChecklistStatus s) => s switch
    {
        ChecklistStatus.Go                => "GO",
        ChecklistStatus.GoButRepair24H    => "GO-BUT 24H",
        ChecklistStatus.GoTillNextService => "GO-BUT 30D",
        ChecklistStatus.NoGo              => "NO-GO",
        ChecklistStatus.Rejected          => "REJECTED",
        _                                 => "Pending"
    };

    [HttpGet]
    public async Task<IActionResult> ExportReportsXlsx(DateTime? from, DateTime? to)
    {
        var submissions = await GetReportSubmissionsAsync(from, to);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Submissions");

        // Header
        var headers = new[]
        {
            "Date / Time", "Machine #", "Machine Name", "Type",
            "Operator", "Employee #", "Shift", "KM / Hours",
            "Status", "Defects",
            "Fitness Signed", "Operator Signed", "Supervisor",
            "Supervisor Signed At", "Supervisor Signed"
        };
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Value = headers[i];

        var headerRange = ws.Range(1, 1, 1, headers.Length);
        headerRange.Style.Font.Bold       = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#1e3a5f");
        headerRange.Style.Font.FontColor  = XLColor.White;
        headerRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // Data
        int row = 2;
        foreach (var s in submissions)
        {
            int defects = s.Items.Count(i => i.Status == ItemStatus.Defect);
            ws.Cell(row,  1).Value = s.SubmittedAt;
            ws.Cell(row,  1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            ws.Cell(row,  2).Value = s.Machine.MachineNumber;
            ws.Cell(row,  3).Value = s.Machine.MachineName;
            ws.Cell(row,  4).Value = s.Machine.TypeDisplay();
            ws.Cell(row,  5).Value = s.Operator.FullName;
            ws.Cell(row,  6).Value = s.Operator.EmployeeNumber;
            ws.Cell(row,  7).Value = s.Shift.ToString();
            ws.Cell(row,  8).Value = s.KmOrHourMeter;
            ws.Cell(row,  9).Value = FormatStatus(s.Status);
            ws.Cell(row, 10).Value = defects;
            ws.Cell(row, 11).Value = s.FitnessDeclarationSigned ? "Yes" : "No";
            ws.Cell(row, 12).Value = string.IsNullOrEmpty(s.OperatorSignature) ? "No" : "Yes";
            ws.Cell(row, 13).Value = s.Supervisor?.FullName ?? "";
            ws.Cell(row, 14).Value = s.SupervisorSignedAt;
            if (s.SupervisorSignedAt.HasValue)
                ws.Cell(row, 14).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            ws.Cell(row, 15).Value = string.IsNullOrEmpty(s.SupervisorSignature) ? "No" : "Yes";

            // Colour-code the status cell
            var statusCell = ws.Cell(row, 9);
            statusCell.Style.Fill.BackgroundColor = s.Status switch
            {
                ChecklistStatus.Go                => XLColor.FromHtml("#dcfce7"),
                ChecklistStatus.GoButRepair24H    => XLColor.FromHtml("#fef3c7"),
                ChecklistStatus.GoTillNextService => XLColor.FromHtml("#dbeafe"),
                ChecklistStatus.NoGo              => XLColor.FromHtml("#fee2e2"),
                ChecklistStatus.Rejected          => XLColor.FromHtml("#fee2e2"),
                _                                 => XLColor.FromHtml("#f3f4f6")
            };
            statusCell.Style.Font.Bold = true;

            row++;
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(1);
        ws.RangeUsed().SetAutoFilter();

        // Summary sheet
        var sum = wb.Worksheets.Add("Summary");
        sum.Cell(1, 1).Value = "Metric";
        sum.Cell(1, 2).Value = "Count";
        sum.Range(1, 1, 1, 2).Style.Font.Bold = true;
        sum.Cell(2, 1).Value = "Total Submissions";    sum.Cell(2, 2).Value = submissions.Count;
        sum.Cell(3, 1).Value = "GO";                   sum.Cell(3, 2).Value = submissions.Count(s => s.Status == ChecklistStatus.Go);
        sum.Cell(4, 1).Value = "GO-BUT";               sum.Cell(4, 2).Value = submissions.Count(s => s.Status == ChecklistStatus.GoButRepair24H || s.Status == ChecklistStatus.GoTillNextService);
        sum.Cell(5, 1).Value = "NO-GO";                sum.Cell(5, 2).Value = submissions.Count(s => s.Status == ChecklistStatus.NoGo);
        sum.Cell(6, 1).Value = "Rejected";             sum.Cell(6, 2).Value = submissions.Count(s => s.Status == ChecklistStatus.Rejected);
        sum.Cell(7, 1).Value = "From";                 sum.Cell(7, 2).Value = from;
        sum.Cell(8, 1).Value = "To";                   sum.Cell(8, 2).Value = to;
        sum.Cell(7, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        sum.Cell(8, 2).Style.DateFormat.Format = "yyyy-mm-dd";
        sum.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var fileName = $"Checklist_Report_{DateTime.UtcNow:yyyyMMdd_HHmm}.xlsx";
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    [HttpGet]
    public async Task<IActionResult> ExportReportsPdf(DateTime? from, DateTime? to)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var submissions = await GetReportSubmissionsAsync(from, to);
        var fromDate = from ?? DateTime.UtcNow.Date.AddDays(-30);
        var toDate   = to   ?? DateTime.UtcNow.Date.AddDays(1);

        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4.Landscape());
                page.Margin(28);
                page.DefaultTextStyle(x => x.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Text("Checklist Submissions Report")
                        .FontSize(18).Bold().FontColor("#1e3a5f");
                    col.Item().Text($"Range: {fromDate:yyyy-MM-dd} – {toDate:yyyy-MM-dd}  ·  Generated {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC")
                        .FontSize(9).FontColor("#6b7280");

                    col.Item().PaddingTop(8).Row(r =>
                    {
                        void Stat(string label, int n, string col)
                        {
                            r.RelativeItem().Background(col).Padding(8).Column(c =>
                            {
                                c.Item().Text(label).FontSize(9).FontColor("#374151");
                                c.Item().Text(n.ToString()).FontSize(16).Bold();
                            });
                        }
                        Stat("Total",   submissions.Count,                                                                                   "#f3f4f6");
                        r.ConstantItem(6);
                        Stat("GO",      submissions.Count(s => s.Status == ChecklistStatus.Go),                                              "#dcfce7");
                        r.ConstantItem(6);
                        Stat("GO-BUT", submissions.Count(s => s.Status == ChecklistStatus.GoButRepair24H || s.Status == ChecklistStatus.GoTillNextService), "#fef3c7");
                        r.ConstantItem(6);
                        Stat("NO-GO",   submissions.Count(s => s.Status == ChecklistStatus.NoGo),                                            "#fee2e2");
                        r.ConstantItem(6);
                        Stat("Rejected",submissions.Count(s => s.Status == ChecklistStatus.Rejected),                                        "#fee2e2");
                    });
                });

                page.Content().PaddingVertical(10).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2.2f);   // Date
                        c.RelativeColumn(2);      // Machine
                        c.RelativeColumn(2);      // Operator
                        c.RelativeColumn(1);      // Shift
                        c.RelativeColumn(1);      // KM/Hrs
                        c.RelativeColumn(1.4f);   // Status
                        c.RelativeColumn(.8f);    // Defects
                        c.RelativeColumn(1);      // Sup. Signed
                    });

                    table.Header(h =>
                    {
                        void H(string s) => h.Cell().Background("#1e3a5f")
                            .Padding(5).Text(s).FontColor("#ffffff").Bold().FontSize(9);
                        H("Date / Time"); H("Machine"); H("Operator"); H("Shift");
                        H("KM/Hrs"); H("Status"); H("Defects"); H("Sup. Signed");
                    });

                    int idx = 0;
                    foreach (var s in submissions)
                    {
                        var bg = idx++ % 2 == 0 ? "#ffffff" : "#f8fafc";
                        var statusBg = s.Status switch
                        {
                            ChecklistStatus.Go                => "#dcfce7",
                            ChecklistStatus.GoButRepair24H    => "#fef3c7",
                            ChecklistStatus.GoTillNextService => "#dbeafe",
                            ChecklistStatus.NoGo              => "#fee2e2",
                            ChecklistStatus.Rejected          => "#fee2e2",
                            _                                 => "#f3f4f6"
                        };
                        int defects = s.Items.Count(i => i.Status == ItemStatus.Defect);

                        table.Cell().Background(bg).Padding(4).Text(s.SubmittedAt.ToString("yyyy-MM-dd HH:mm"));
                        table.Cell().Background(bg).Padding(4).Text($"{s.Machine.MachineNumber}\n{s.Machine.MachineName}");
                        table.Cell().Background(bg).Padding(4).Text($"{s.Operator.FullName}\n{s.Operator.EmployeeNumber}");
                        table.Cell().Background(bg).Padding(4).Text(s.Shift.ToString());
                        table.Cell().Background(bg).Padding(4).Text(s.KmOrHourMeter?.ToString() ?? "–");
                        table.Cell().Background(statusBg).Padding(4).Text(FormatStatus(s.Status)).Bold();
                        table.Cell().Background(bg).Padding(4).Text(defects > 0 ? defects.ToString() : "–");
                        table.Cell().Background(bg).Padding(4)
                            .Text(string.IsNullOrEmpty(s.SupervisorSignature) ? "—" : $"✓ {s.Supervisor?.FullName ?? ""}");
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ").FontSize(8).FontColor("#6b7280");
                    x.CurrentPageNumber().FontSize(8);
                    x.Span(" of ").FontSize(8).FontColor("#6b7280");
                    x.TotalPages().FontSize(8);
                });
            });
        }).GeneratePdf();

        var fileName = $"Checklist_Report_{DateTime.UtcNow:yyyyMMdd_HHmm}.pdf";
        return File(bytes, "application/pdf", fileName);
    }

    // ── NO-GO keyword list (shared between Excel + PDF upload paths) ─────────
    private static readonly string[] NoGoKeywords =
    {
        "OPERATOR LICENCE", "SEAT BELT", "SEAT BELTS", "BRAKES", "BRAKE TEST",
        "FIRE EXTINGUISHER", "KEY CONTROL", "EMERGENCY STOP"
    };

    private static bool IsNoGo(string text) =>
        NoGoKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    // ── Upload Template ───────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> UploadTemplate()
    {
        ViewBag.Machines = await _db.Machines.OrderBy(m => m.MachineNumber).ToListAsync();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadTemplate(
        IFormFile file, int machineId, bool replaceExisting = false)
    {
        ViewBag.Machines = await _db.Machines.OrderBy(m => m.MachineNumber).ToListAsync();

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Please select an Excel file to upload.";
            return View();
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xls")
        {
            TempData["Error"] = "Only .xlsx or .xls files are supported.";
            return View();
        }

        var machine = await _db.Machines.FindAsync(machineId);
        if (machine == null)
        {
            TempData["Error"] = "Machine not found.";
            return View();
        }

        try
        {
            using var stream = file.OpenReadStream();
            using var wb     = new XLWorkbook(stream);
            var ws = wb.Worksheets.First();

            var items = new List<string>();

            // Read non-empty cells from column A (skip first 3 header rows).
            // ItemName column is varchar(200), so truncate to fit.
            const int MaxItemNameLen = 200;
            for (int row = 4; row <= ws.LastRowUsed()?.RowNumber() + 1; row++)
            {
                var cell = ws.Cell(row, 1).GetString().Trim().ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(cell))
                {
                    if (cell.Length > MaxItemNameLen) cell = cell.Substring(0, MaxItemNameLen);
                    items.Add(cell);
                }
            }

            if (items.Count == 0)
            {
                TempData["Error"] = "No checklist items found. Expected items in column A starting from row 4.";
                return View();
            }

            // Remove old template if replacing
            if (replaceExisting)
            {
                var old = await _db.ChecklistTemplates
                    .Include(t => t.Items)
                    .FirstOrDefaultAsync(t => t.MachineId == machineId);
                if (old != null)
                {
                    _db.ChecklistTemplateItems.RemoveRange(old.Items);
                    _db.ChecklistTemplates.Remove(old);
                    await _db.SaveChangesAsync();
                }
            }

            var template = new ChecklistTemplate
            {
                MachineId   = machineId,
                MachineType = machine.Type,
                Name        = $"{machine.MachineName} Checklist (Uploaded)"
            };
            _db.ChecklistTemplates.Add(template);
            await _db.SaveChangesAsync();

            var templateItems = items.Select((item, idx) => new ChecklistTemplateItem
            {
                TemplateId  = template.Id,
                ItemName    = item,
                Section     = "General",
                SortOrder   = idx + 1,
                IsNoGoItem  = IsNoGo(item)
            }).ToList();

            _db.ChecklistTemplateItems.AddRange(templateItems);
            await _db.SaveChangesAsync();

            TempData["Success"] =
                $"Template uploaded for {machine.MachineNumber} with {items.Count} checklist items.";
            return RedirectToAction("Index");
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to parse Excel file: {ex.Message}";
            return View();
        }
    }

    // ── Bulk Upload (multi-sheet workbook → many machines + templates) ────────
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> UploadTemplatesBulk(IFormFile file, bool replaceExisting = false)
    {
        ViewBag.Machines  = await _db.Machines.OrderBy(m => m.MachineNumber).ToListAsync();
        ViewBag.ActiveTab = "bulk";

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Please select an Excel workbook to upload.";
            return View("UploadTemplate");
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xls")
        {
            TempData["Error"] = "Bulk upload requires a .xlsx or .xls workbook.";
            return View("UploadTemplate");
        }

        const int MaxItemNameLen = 200;

        int createdMachines = 0, updatedTemplates = 0, skipped = 0, totalItems = 0;
        var perSheet = new List<string>();

        try
        {
            using var stream = file.OpenReadStream();
            using var wb     = new XLWorkbook(stream);

            foreach (var ws in wb.Worksheets)
            {
                var sheetName = (ws.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sheetName))
                {
                    skipped++;
                    continue;
                }

                // Pull items from column A, row 4 onwards
                var items = new List<string>();
                var last  = ws.LastRowUsed()?.RowNumber() ?? 0;
                for (int row = 4; row <= last; row++)
                {
                    var cell = ws.Cell(row, 1).GetString().Trim().ToUpperInvariant();
                    if (string.IsNullOrWhiteSpace(cell)) continue;
                    if (cell.Length > MaxItemNameLen) cell = cell.Substring(0, MaxItemNameLen);
                    items.Add(cell);
                }

                if (items.Count == 0)
                {
                    perSheet.Add($"'{sheetName}': skipped (no items in column A from row 4)");
                    skipped++;
                    continue;
                }

                // Find-or-create the machine; sheet name doubles as MachineNumber & TypeName
                var machineNumber = sheetName;
                var machine = await _db.Machines
                    .Include(m => m.Template).ThenInclude(t => t!.Items)
                    .FirstOrDefaultAsync(m => m.MachineNumber == machineNumber);

                if (machine == null)
                {
                    machine = new Machine
                    {
                        MachineNumber = machineNumber,
                        MachineName   = machineNumber,
                        TypeName      = machineNumber,
                        Type          = 0,
                        IsActive      = true
                    };
                    _db.Machines.Add(machine);
                    await _db.SaveChangesAsync();
                    createdMachines++;
                }
                else if (machine.Template != null && !replaceExisting)
                {
                    perSheet.Add($"'{sheetName}': skipped (template exists — tick 'Replace existing' to overwrite)");
                    skipped++;
                    continue;
                }
                else if (machine.Template != null && replaceExisting)
                {
                    _db.ChecklistTemplateItems.RemoveRange(machine.Template.Items);
                    _db.ChecklistTemplates.Remove(machine.Template);
                    await _db.SaveChangesAsync();
                }

                var template = new ChecklistTemplate
                {
                    MachineId   = machine.Id,
                    MachineType = machine.Type,
                    Name        = $"{machine.MachineName} Checklist (Bulk Upload)"
                };
                _db.ChecklistTemplates.Add(template);
                await _db.SaveChangesAsync();

                var templateItems = items.Select((it, idx) => new ChecklistTemplateItem
                {
                    TemplateId = template.Id,
                    ItemName   = it,
                    Section    = "General",
                    SortOrder  = idx + 1,
                    IsNoGoItem = IsNoGo(it)
                }).ToList();

                _db.ChecklistTemplateItems.AddRange(templateItems);
                await _db.SaveChangesAsync();

                updatedTemplates++;
                totalItems += items.Count;
                perSheet.Add($"'{sheetName}': {items.Count} item(s) → machine {machine.MachineNumber}");
            }
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Bulk upload failed: {ex.Message}";
            return View("UploadTemplate");
        }

        TempData["Success"] =
            $"Bulk upload complete. Created {createdMachines} machine(s), wrote {updatedTemplates} template(s) " +
            $"with {totalItems} item(s). Skipped {skipped} sheet(s).";
        TempData["BulkDetail"] = string.Join("\n", perSheet);
        return RedirectToAction("Index");
    }

    // ── Upload PDF Template (Step 1: extract lines, show review) ─────────────
    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(25_000_000)]
    public async Task<IActionResult> UploadPdfTemplate(
        IFormFile file, int machineId, bool replaceExisting = false)
    {
        ViewBag.Machines  = await _db.Machines.OrderBy(m => m.MachineNumber).ToListAsync();
        ViewBag.ActiveTab = "pdf";

        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Please select a PDF file to upload.";
            return View("UploadTemplate");
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".pdf")
        {
            TempData["Error"] = "Only .pdf files are supported on this tab.";
            return View("UploadTemplate");
        }

        var machine = await _db.Machines.FindAsync(machineId);
        if (machine == null)
        {
            TempData["Error"] = "Machine not found.";
            return View("UploadTemplate");
        }

        List<string> lines;
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            ms.Position = 0;

            lines = new List<string>();
            using var pdf = PdfDocument.Open(ms);
            foreach (Page page in pdf.GetPages())
            {
                // Split page text into lines, trim, and drop blanks
                var pageText = page.Text ?? string.Empty;
                foreach (var raw in pageText.Split('\n', '\r'))
                {
                    var trimmed = raw.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed))
                        lines.Add(trimmed);
                }
            }
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to read PDF file: {ex.Message}";
            return View("UploadTemplate");
        }

        if (lines.Count == 0)
        {
            TempData["Error"] = "No text could be extracted. The PDF may be image-only/scanned.";
            return View("UploadTemplate");
        }

        ViewBag.Machine          = machine;
        ViewBag.MachineId        = machineId;
        ViewBag.ReplaceExisting  = replaceExisting;
        ViewBag.NoGoKeywords     = NoGoKeywords;
        ViewBag.Lines            = lines;

        return View("ReviewPdfTemplate");
    }

    // ── DTO for the PDF review form (indexed binding) ────────────────────────
    public class PdfReviewRow
    {
        public string? Text { get; set; }
        public bool    Keep { get; set; }
        public bool    NoGo { get; set; }
    }

    // ── Save PDF Template (Step 2: persist admin's selected items) ───────────
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SavePdfTemplate(
        int machineId,
        bool replaceExisting,
        List<PdfReviewRow>? items)
    {
        var machine = await _db.Machines.FindAsync(machineId);
        if (machine == null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToAction("UploadTemplate");
        }

        items ??= new List<PdfReviewRow>();

        // ItemName column is varchar(200) — truncate to fit
        const int MaxItemNameLen = 200;
        static string Fit(string s) =>
            s.Length <= MaxItemNameLen ? s : s.Substring(0, MaxItemNameLen);

        var kept = items
            .Where(r => r.Keep && !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => (Text: Fit(r.Text!.Trim().ToUpperInvariant()), NoGo: r.NoGo))
            .ToList();

        if (kept.Count == 0)
        {
            TempData["Error"] = "No checklist items were selected. Please tick at least one line.";
            return RedirectToAction("UploadTemplate");
        }

        if (replaceExisting)
        {
            var old = await _db.ChecklistTemplates
                .Include(t => t.Items)
                .FirstOrDefaultAsync(t => t.MachineId == machineId);
            if (old != null)
            {
                _db.ChecklistTemplateItems.RemoveRange(old.Items);
                _db.ChecklistTemplates.Remove(old);
                await _db.SaveChangesAsync();
            }
        }

        var template = new ChecklistTemplate
        {
            MachineId   = machineId,
            MachineType = machine.Type,
            Name        = $"{machine.MachineName} Checklist (PDF Upload)"
        };
        _db.ChecklistTemplates.Add(template);
        await _db.SaveChangesAsync();

        var templateItems = kept.Select((row, idx) => new ChecklistTemplateItem
        {
            TemplateId = template.Id,
            ItemName   = row.Text,
            Section    = "General",
            SortOrder  = idx + 1,
            IsNoGoItem = row.NoGo
        }).ToList();

        _db.ChecklistTemplateItems.AddRange(templateItems);
        await _db.SaveChangesAsync();

        TempData["Success"] =
            $"Template uploaded for {machine.MachineNumber} with {kept.Count} checklist items.";
        return RedirectToAction("Index");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                              ICON LIBRARY
    // ══════════════════════════════════════════════════════════════════════════
    private static readonly string[] AllowedIconExtensions =
        { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg" };

    [HttpGet]
    public async Task<IActionResult> IconLibrary()
    {
        var icons = await _db.IconLibraryItems
            .OrderByDescending(i => i.UploadedAt)
            .ToListAsync();
        return View(icons);
    }

    /// <summary>JSON endpoint used by the icon-picker modal.</summary>
    [HttpGet]
    public async Task<IActionResult> IconLibraryJson()
    {
        var icons = await _db.IconLibraryItems
            .OrderBy(i => i.Name)
            .Select(i => new { i.Id, i.Name, i.FilePath, i.OriginalFileName, i.FileSize })
            .ToListAsync();
        return Json(icons);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> UploadIcon(IFormFile file, string? displayName)
    {
        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Please choose a file to upload.";
            return RedirectToAction(nameof(IconLibrary));
        }

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedIconExtensions.Contains(ext))
        {
            TempData["Error"] = $"Unsupported file type. Allowed: {string.Join(", ", AllowedIconExtensions)}.";
            return RedirectToAction(nameof(IconLibrary));
        }

        var dir = Path.Combine(_env.WebRootPath, "icon-library");
        Directory.CreateDirectory(dir);

        var storedName = $"{Guid.NewGuid():N}{ext}";
        var fullPath   = Path.Combine(dir, storedName);

        await using (var stream = new FileStream(fullPath, FileMode.Create))
            await file.CopyToAsync(stream);

        var name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(file.FileName)
            : displayName.Trim();
        if (name.Length > 120) name = name.Substring(0, 120);

        _db.IconLibraryItems.Add(new IconLibraryItem
        {
            Name             = name,
            OriginalFileName = file.FileName,
            FilePath         = $"/icon-library/{storedName}",
            ContentType      = file.ContentType,
            FileSize         = file.Length,
            UploadedAt       = DateTime.UtcNow,
            UploadedById     = _users.GetUserId(User)
        });
        await _db.SaveChangesAsync();

        TempData["Success"] = $"'{name}' added to the icon library.";
        return RedirectToAction(nameof(IconLibrary));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteIcon(int id)
    {
        var icon = await _db.IconLibraryItems.FindAsync(id);
        if (icon == null)
        {
            TempData["Error"] = "Icon not found.";
            return RedirectToAction(nameof(IconLibrary));
        }

        // Delete the file off disk (best-effort)
        try
        {
            var full = Path.Combine(_env.WebRootPath, icon.FilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(full)) System.IO.File.Delete(full);
        }
        catch { /* leave dangling file rather than failing the request */ }

        // Detach the icon from any template items that referenced it so the rows don't 404.
        var refs = await _db.ChecklistTemplateItems
            .Where(t => t.IconPath == icon.FilePath)
            .ToListAsync();
        foreach (var t in refs) t.IconPath = null;

        _db.IconLibraryItems.Remove(icon);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"'{icon.Name}' removed from the library.";
        if (refs.Any())
            TempData["Success"] += $" Cleared the icon from {refs.Count} checklist item(s).";

        return RedirectToAction(nameof(IconLibrary));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                  DEBUG-ONLY · DATABASE TRUNCATE
    // ══════════════════════════════════════════════════════════════════════════
    // Wipes every domain table + every non-admin user. PRESERVES the currently
    // signed-in admin so they don't lock themselves out. ONLY available when
    // ASPNETCORE_ENVIRONMENT=Development and the caller is in the Admin role.
    // Returns 404 in any non-Development environment so the URL also disappears.
    // ──────────────────────────────────────────────────────────────────────────
    private const string TruncateConfirmPhrase = "TRUNCATE EVERYTHING";

    [HttpGet]
    public async Task<IActionResult> Debug()
    {
        if (!_env.IsDevelopment()) return NotFound();

        ViewBag.ConfirmPhrase = TruncateConfirmPhrase;
        ViewBag.Counts = new Dictionary<string, int>
        {
            ["Submissions"]              = await _db.ChecklistSubmissions.CountAsync(),
            ["Submission items"]         = await _db.SubmissionItems.CountAsync(),
            ["Defect orders"]            = await _db.DefectOrders.CountAsync(),
            ["Machines"]                 = await _db.Machines.CountAsync(),
            ["Machine assignments"]      = await _db.MachineAssignments.CountAsync(),
            ["Operator→Supervisor"]      = await _db.OperatorSupervisorAssignments.CountAsync(),
            ["Checklist templates"]      = await _db.ChecklistTemplates.CountAsync(),
            ["Checklist template items"] = await _db.ChecklistTemplateItems.CountAsync(),
            ["Users (incl. admin)"]      = await _db.Users.CountAsync(),
        };
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> DebugTruncate(string confirm)
    {
        if (!_env.IsDevelopment()) return NotFound();

        if (!string.Equals(confirm?.Trim(), TruncateConfirmPhrase, StringComparison.Ordinal))
        {
            TempData["Error"] = $"Confirmation text did not match. Type exactly: {TruncateConfirmPhrase}";
            return RedirectToAction(nameof(Debug));
        }

        var currentAdminId = _users.GetUserId(User);

        // TRUNCATE all domain tables in one statement so FK constraints don't
        // matter. RESTART IDENTITY resets the integer PKs to 1.
        const string sql = @"
            TRUNCATE TABLE
                ""DefectOrders"",
                ""SubmissionItems"",
                ""ChecklistSubmissions"",
                ""ChecklistTemplateItems"",
                ""ChecklistTemplates"",
                ""MachineAssignments"",
                ""OperatorSupervisorAssignments"",
                ""Machines""
            RESTART IDENTITY CASCADE;";

        try
        {
            await _db.Database.ExecuteSqlRawAsync(sql);

            // Remove every user except the currently-signed-in admin.
            var others = await _db.Users
                .Where(u => u.Id != currentAdminId)
                .ToListAsync();
            foreach (var u in others) await _users.DeleteAsync(u);

            TempData["Success"] =
                $"Database truncated. {others.Count} non-admin user(s) removed. " +
                "Identity counters reset.";
            return RedirectToAction(nameof(Debug));
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Truncate failed: {ex.Message}";
            return RedirectToAction(nameof(Debug));
        }
    }
}
