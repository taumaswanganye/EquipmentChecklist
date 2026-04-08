using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EquipmentChecklist.Controllers;

// ── ViewModel classes live in the controller file ─────────────────────────────

public class WizardMachineData
{
    public string MachineName   { get; set; } = "";
    public string MachineNumber { get; set; } = "";
    public int    MachineType   { get; set; }
    public string Description   { get; set; } = "";
    public List<WizardItemData> Items { get; set; } = new();
}

public class WizardItemData
{
    public string  ItemName          { get; set; } = "";
    public string  StatusLabel       { get; set; } = "";
    public string  Action            { get; set; } = "";
    public string  InOrderCondition  { get; set; } = "";
    public string  DefectCondition   { get; set; } = "";
    public bool    IsNoGoItem        { get; set; }
    public string? IconPath          { get; set; }   // relative path under wwwroot
    public string? IconFileName      { get; set; }   // original filename for display
}

// ─────────────────────────────────────────────────────────────────────────────

[Authorize(Roles = "Admin")]
public class MachineWizardController : Controller
{
    private const string SessionKey = "machine_wizard_v1";

    private readonly ApplicationDbContext _db;
    private readonly IWebHostEnvironment  _env;

    public MachineWizardController(ApplicationDbContext db, IWebHostEnvironment env)
    { _db = db; _env = env; }

    // ── Session helpers ───────────────────────────────────────────────────────

    private WizardMachineData GetWizard() =>
        JsonSerializer.Deserialize<WizardMachineData>(
            HttpContext.Session.GetString(SessionKey) ?? "{}") ?? new();

    private void SaveWizard(WizardMachineData w) =>
        HttpContext.Session.SetString(SessionKey, JsonSerializer.Serialize(w));

    // ── STEP 1 – Machine Details ──────────────────────────────────────────────

    [HttpGet("/Admin/CreateMachineWizard")]
    public IActionResult Step1()
    {
        HttpContext.Session.Remove(SessionKey);   // fresh start
        return View(new WizardMachineData());
    }

    [HttpPost("/Admin/CreateMachineWizard/Step1")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Step1Post(WizardMachineData vm)
    {
        if (string.IsNullOrWhiteSpace(vm.MachineName) ||
            string.IsNullOrWhiteSpace(vm.MachineNumber) ||
            vm.MachineType == 0)
        {
            ModelState.AddModelError("", "Please fill in all required fields.");
            return View("Step1", vm);
        }

        // Check for duplicate MachineNumber before proceeding
        var machineNumber = vm.MachineNumber.Trim();
        var exists = await _db.Machines
            .AnyAsync(m => m.MachineNumber == machineNumber);

        if (exists)
        {
            ModelState.AddModelError("MachineNumber",
                $"A machine with number '{machineNumber}' already exists. Please use a unique number.");
            return View("Step1", vm);
        }

        var wizard = new WizardMachineData
        {
            MachineName   = vm.MachineName.Trim(),
            MachineNumber = machineNumber,
            MachineType   = vm.MachineType,
            Description   = vm.Description?.Trim() ?? "",
            Items         = new()
        };
        SaveWizard(wizard);
        return Redirect("/Admin/CreateMachineWizard/Step2");
    }

    // ── STEP 2 – Create Checklist Items ───────────────────────────────────────

    [HttpGet("/Admin/CreateMachineWizard/Step2")]
    public IActionResult Step2()
    {
        var w = GetWizard();
        if (string.IsNullOrEmpty(w.MachineName))
            return Redirect("/Admin/CreateMachineWizard");
        return View(w);
    }

    [HttpPost("/Admin/CreateMachineWizard/AddItem")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(
        string itemName,
        string statusLabel,
        string action,
        string inOrderCondition,
        string defectCondition,
        bool   isNoGoItem,
        IFormFile? iconFile)
    {
        var w = GetWizard();
        if (string.IsNullOrEmpty(w.MachineName))
            return Redirect("/Admin/CreateMachineWizard");

        string? iconPath     = null;
        string? iconFileName = null;

        if (iconFile != null && iconFile.Length > 0)
        {
            var uploadDir = Path.Combine(_env.WebRootPath, "uploads", "icons");
            Directory.CreateDirectory(uploadDir);

            var ext  = Path.GetExtension(iconFile.FileName).ToLower();
            var safe = new[] { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp" };
            if (!safe.Contains(ext)) ext = ".png";

            var filename = $"{Guid.NewGuid()}{ext}";
            var fullPath = Path.Combine(uploadDir, filename);

            using var stream = System.IO.File.Create(fullPath);
            await iconFile.CopyToAsync(stream);

            iconPath     = $"uploads/icons/{filename}";
            iconFileName = iconFile.FileName;
        }

        w.Items.Add(new WizardItemData
        {
            ItemName         = itemName?.Trim() ?? "",
            StatusLabel      = statusLabel?.Trim() ?? "",
            Action           = action?.Trim() ?? "",
            InOrderCondition = inOrderCondition?.Trim() ?? "",
            DefectCondition  = defectCondition?.Trim() ?? "",
            IsNoGoItem       = isNoGoItem,
            IconPath         = iconPath,
            IconFileName     = iconFileName
        });

        SaveWizard(w);
        return Redirect("/Admin/CreateMachineWizard/Step2");
    }

    [HttpPost("/Admin/CreateMachineWizard/RemoveItem")]
    [ValidateAntiForgeryToken]
    public IActionResult RemoveItem(int index)
    {
        var w = GetWizard();
        if (index >= 0 && index < w.Items.Count)
        {
            // Delete icon file if it exists
            var item = w.Items[index];
            if (!string.IsNullOrEmpty(item.IconPath))
            {
                var full = Path.Combine(_env.WebRootPath, item.IconPath.Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(full))
                    System.IO.File.Delete(full);
            }
            w.Items.RemoveAt(index);
            SaveWizard(w);
        }
        return Redirect("/Admin/CreateMachineWizard/Step2");
    }

    // ── STEP 3 – Review & Save ────────────────────────────────────────────────

    [HttpGet("/Admin/CreateMachineWizard/Step3")]
    public IActionResult Step3()
    {
        var w = GetWizard();
        if (string.IsNullOrEmpty(w.MachineName))
            return Redirect("/Admin/CreateMachineWizard");
        return View(w);
    }

    [HttpPost("/Admin/CreateMachineWizard/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save()
    {
        var w = GetWizard();
        if (string.IsNullOrEmpty(w.MachineName))
            return Redirect("/Admin/CreateMachineWizard");

        // 1. Create the Machine — guard against race-condition duplicates
        var alreadyExists = await _db.Machines
            .AnyAsync(m => m.MachineNumber == w.MachineNumber);

        if (alreadyExists)
        {
            TempData["Error"] = $"A machine with number '{w.MachineNumber}' was created by someone else while you were in the wizard. Please go back and choose a different number.";
            return Redirect("/Admin/CreateMachineWizard");
        }

        var machine = new Machine
        {
            MachineName   = w.MachineName,
            MachineNumber = w.MachineNumber,
            Type          = (MachineType)w.MachineType,
            Description   = w.Description,
            IsActive      = true
        };
        _db.Machines.Add(machine);
        await _db.SaveChangesAsync();

        // 2. Create the ChecklistTemplate
        var template = new ChecklistTemplate
        {
            MachineType = (MachineType)w.MachineType,
            Name        = $"{w.MachineName} Checklist",
            MachineId   = machine.Id
        };
        _db.ChecklistTemplates.Add(template);
        await _db.SaveChangesAsync();

        // 3. Create the ChecklistTemplateItems
        for (int i = 0; i < w.Items.Count; i++)
        {
            var item = w.Items[i];
            _db.ChecklistTemplateItems.Add(new ChecklistTemplateItem
            {
                TemplateId       = template.Id,
                ItemName         = item.ItemName,
                Section          = "General",
                SortOrder        = i + 1,
                IsNoGoItem       = item.IsNoGoItem,
                StatusLabel      = item.StatusLabel,
                Action           = item.Action,
                InOrderCondition = item.InOrderCondition,
                DefectCondition  = item.DefectCondition,
                IconPath         = item.IconPath
            });
        }
        await _db.SaveChangesAsync();

        HttpContext.Session.Remove(SessionKey);
        TempData["Success"] = $"Machine '{w.MachineName}' created with {w.Items.Count} checklist item(s).";
        return Redirect("/Admin");
    }
}
