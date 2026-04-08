using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ClosedXML.Excel;

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
    [HttpGet]
    public async Task<IActionResult> EditMachine(int id)
    {
        var m = await _db.Machines.FindAsync(id);
        if (m == null) return RedirectToAction("Index");
        return View(m);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EditMachine(Machine model)
    {
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

        existing.MachineName   = model.MachineName.Trim();
        existing.MachineNumber = model.MachineNumber.Trim();
        existing.Type          = model.Type;
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
        IFormFile? imageFile)
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

        // Save image now that we have the item ID
        var imagePath = await SaveItemImageAsync(imageFile, newItem.Id);
        if (imagePath != null)
        {
            newItem.IconPath = imagePath;
            await _db.SaveChangesAsync();
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

        if (removeImage)
        {
            // Delete old file if it exists on disk
            if (!string.IsNullOrEmpty(item.IconPath))
            {
                var oldFile = Path.Combine(_env.WebRootPath, item.IconPath.TrimStart('/'));
                if (System.IO.File.Exists(oldFile)) System.IO.File.Delete(oldFile);
            }
            item.IconPath = null;
        }
        else
        {
            var newPath = await SaveItemImageAsync(imageFile, itemId);
            if (newPath != null)
            {
                // Delete old file before replacing
                if (!string.IsNullOrEmpty(item.IconPath))
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
        var machineAssignments = await _db.MachineAssignments
            .Include(a => a.Machine)
            .Include(a => a.Operator)
            .Include(a => a.Mechanic)
            .Where(a => a.IsActive)
            .OrderBy(a => a.Machine.MachineNumber)
            .ToListAsync();

        var supervisorAssignments = await _db.OperatorSupervisorAssignments
            .Include(a => a.Operator)
            .Include(a => a.Supervisor)
            .Where(a => a.IsActive)
            .OrderBy(a => a.Operator.FullName)
            .ToListAsync();

        // Maps for dropdown hints
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

            // Read non-empty cells from column A (skip first 3 header rows)
            for (int row = 4; row <= ws.LastRowUsed()?.RowNumber() + 1; row++)
            {
                var cell = ws.Cell(row, 1).GetString().Trim().ToUpperInvariant();
                if (!string.IsNullOrWhiteSpace(cell))
                    items.Add(cell);
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

            var noGoKeywords = new[]
            {
                "OPERATOR LICENCE", "SEAT BELT", "SEAT BELTS", "BRAKES", "BRAKE TEST",
                "FIRE EXTINGUISHER", "KEY CONTROL", "EMERGENCY STOP"
            };

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
                IsNoGoItem  = noGoKeywords.Any(k =>
                    item.Contains(k, StringComparison.OrdinalIgnoreCase))
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
}
