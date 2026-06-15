using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
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
using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Controllers;

[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly ApplicationDbContext         _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly IWebHostEnvironment          _env;
    private readonly ReportsService               _reports;
    private readonly NotificationService          _notifications;
    private readonly AuditService                 _audit;
    private readonly EmailService                 _email;
    private readonly CompetencyService            _competency;
    private readonly ConfigurationService         _settings;

    public AdminController(ApplicationDbContext db,
                           UserManager<ApplicationUser> users,
                           IWebHostEnvironment env,
                           ReportsService reports,
                           NotificationService notifications,
                           AuditService audit,
                           EmailService email,
                           CompetencyService competency,
                           ConfigurationService settings)
    {
        _db            = db;
        _users         = users;
        _env           = env;
        _reports       = reports;
        _notifications = notifications;
        _audit         = audit;
        _email         = email;
        _competency    = competency;
        _settings      = settings;
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

    // ══════════════════════════════════════════════════════════════════════════
    //  PASSWORD RESET (admin-initiated)
    //
    //  Operator forgot their password → admin clicks "Reset password" on
    //  the Employees row → server generates a strong temp password, sets
    //  it via Identity's standard token flow, and emails the user a clean
    //  template message with the new credentials. The action is fully
    //  auditable: AuditActions.UserPasswordReset is logged with the actor
    //  (current admin) and target (user) so a security review can trace
    //  every credential change.
    //
    //  Why temp-password-in-email (vs reset-link-token):
    //    * Most operators are field staff with sketchy browser sessions —
    //      a clickable link is friction; a typed password is what they
    //      already know how to use.
    //    * Audit trail captures the admin's intent regardless of which
    //      method is used.
    //    * SSL/TLS protects the email in transit; mailbox compromise
    //      remains the same attack surface either way.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetEmployeePassword(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            TempData["Error"] = "No user selected.";
            return RedirectToAction("Employees");
        }

        var user = await _users.FindByIdAsync(userId);
        if (user == null)
        {
            TempData["Error"] = "That user no longer exists.";
            return RedirectToAction("Employees");
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            // We could still reset and hand the password over verbally —
            // but the whole point of this action is "email it to them",
            // so refusing here flags the data-cleanliness problem rather
            // than papering over it.
            TempData["Error"] =
                $"{user.FullName} has no email address on file. Edit the employee first to add one.";
            return RedirectToAction("Employees");
        }

        // Generate a strong-but-typeable temp password. Format:
        //   Reset-{6 alphanumeric}-{4 digits}
        // 18 characters, satisfies Identity's defaults (digit + 8+),
        // memorable enough to be read out over a phone if email fails.
        var tempPassword = GenerateTempPassword();

        // Identity's "admin reset" pattern: get a token, then apply it.
        // RemovePasswordAsync + AddPasswordAsync would also work and is
        // less ceremonial, but the token flow exercises the same code path
        // a real user-facing reset would, which catches Identity-config
        // drift earlier.
        var token  = await _users.GeneratePasswordResetTokenAsync(user);
        var result = await _users.ResetPasswordAsync(user, token, tempPassword);
        if (!result.Succeeded)
        {
            TempData["Error"] = "Could not reset password: "
                + string.Join(" ", result.Errors.Select(e => e.Description));
            return RedirectToAction("Employees");
        }

        // ── Email the new password ─────────────────────────────────────
        // EmailService returns false when SMTP isn't configured or the
        // send actually fails. We still consider the reset itself
        // successful (the password IS changed); the admin just has to
        // hand the temp password over in person instead.
        var adminName = User?.Identity?.Name ?? "an administrator";
        var emailSent = await _email.SendPasswordResetAsync(
            toEmail:        user.Email,
            toName:         user.FullName,
            newPassword:    tempPassword,
            resetByAdmin:   adminName);

        // ── Audit trail ────────────────────────────────────────────────
        // PayloadJson holds the target identity AND whether the email
        // actually went out, so a later investigation knows whether to
        // ask the user "did you receive an email" or "did the admin tell
        // you the password verbally".
        try
        {
            await _audit.LogAsync(
                action:     AuditActions.UserPasswordReset,
                targetType: "User",
                targetId:   null,
                payload:    new
                {
                    targetUserId = user.Id,
                    targetEmail  = user.Email,
                    targetName   = user.FullName,
                    targetRole   = user.Role.ToString(),
                    emailSent
                });
        }
        catch { /* audit failure shouldn't block the operator flow */ }

        TempData["Success"] = emailSent
            ? $"Password reset for {user.FullName}. A new temporary password was emailed to {user.Email}."
            : $"Password reset for {user.FullName}, but the email couldn't be sent. " +
              $"Temporary password: {tempPassword} — share it with them in person.";
        return RedirectToAction("Employees");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ACTIVATE / DEACTIVATE USER
    //
    //  Toggle a user's IsActive flag. The flag is the single source of
    //  truth checked in three places:
    //    1. SyncController.Login — rejects online sign-in for inactive
    //       users, returning a 403 with a distinct error code so the
    //       mobile client can show a friendly message and clear its
    //       cached credentials.
    //    2. JwtBearer OnTokenValidated — revalidates IsActive against
    //       the DB on EVERY authenticated API request, so a deactivation
    //       takes effect within seconds without waiting for the JWT to
    //       expire on its own.
    //    3. Mobile AuthService.OfflineSignInAsync — refuses offline
    //       sign-in when the cached user has IsBlocked=true, which the
    //       mobile picks up from the server's 403 response.
    //
    //  The admin can't deactivate themselves — locking the only admin
    //  out of the system is a worse outcome than any individual
    //  password-rotation incident.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetEmployeeActive(string userId, bool active)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            TempData["Error"] = "No user selected.";
            return RedirectToAction("Employees");
        }

        var user = await _users.FindByIdAsync(userId);
        if (user == null)
        {
            TempData["Error"] = "That user no longer exists.";
            return RedirectToAction("Employees");
        }

        // ── Guardrail: an admin can't deactivate themselves. ──
        // Losing access to your own account mid-session is one of the
        // few mistakes that's nearly impossible to recover from without
        // direct database access.
        var currentAdminId = _users.GetUserId(User);
        if (string.Equals(user.Id, currentAdminId, StringComparison.Ordinal) && !active)
        {
            TempData["Error"] = "You can't deactivate your own admin account.";
            return RedirectToAction("Employees");
        }

        // Skip the round-trip if there's nothing to change. The DB write
        // would be a no-op but we'd still write an audit row, which would
        // pollute the trail.
        if (user.IsActive == active)
        {
            TempData["Success"] = $"{user.FullName} is already {(active ? "active" : "deactivated")}.";
            return RedirectToAction("Employees");
        }

        user.IsActive = active;

        // ── Bump the security stamp ──
        // Identity uses SecurityStamp for cookie session invalidation —
        // changing it logs out any active web session for this user
        // immediately. JWT sessions are revoked via the OnTokenValidated
        // re-check that hits the database on every API call.
        await _users.UpdateSecurityStampAsync(user);

        var saveResult = await _users.UpdateAsync(user);
        if (!saveResult.Succeeded)
        {
            TempData["Error"] = "Could not update the user: "
                + string.Join(" ", saveResult.Errors.Select(e => e.Description));
            return RedirectToAction("Employees");
        }

        // ── Audit trail ──
        try
        {
            await _audit.LogAsync(
                action:     active ? AuditActions.UserReactivated : AuditActions.UserDeactivated,
                targetType: "User",
                targetId:   null,
                payload:    new
                {
                    targetUserId = user.Id,
                    targetEmail  = user.Email,
                    targetName   = user.FullName,
                    targetRole   = user.Role.ToString()
                });
        }
        catch { /* audit failure shouldn't block the operator flow */ }

        TempData["Success"] = active
            ? $"{user.FullName} reactivated. They can sign in again immediately."
            : $"{user.FullName} deactivated. They'll be signed out next time their phone reaches the API, " +
              $"and offline sign-in will refuse them on next attempt.";
        return RedirectToAction("Employees");
    }

    /// <summary>
    /// Build an admin-reset temporary password that satisfies the project's
    /// Identity policy (≥8 chars, ≥1 digit) AND stays typeable enough that
    /// an operator can re-enter it on a phone screen without misreading
    /// look-alike glyphs. We deliberately skip the alphabet's
    /// confusable characters (O/0, 1/l/I) so a fat-fingered "lower-case L"
    /// can't be mistaken for a one or a capital i.
    /// </summary>
    private static string GenerateTempPassword()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz";
        const string digits  = "23456789";

        // Crypto-secure random source — System.Random isn't strong enough
        // for credentials. RandomNumberGenerator is the .NET standard.
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();

        char Pick(string pool)
        {
            // GetInt32 is rejection-sampled internally so the modulo
            // bias problem doesn't apply.
            var idx = System.Security.Cryptography
                .RandomNumberGenerator.GetInt32(pool.Length);
            return pool[idx];
        }

        var middle  = string.Concat(Enumerable.Range(0, 6).Select(_ => Pick(letters)));
        var tail    = string.Concat(Enumerable.Range(0, 4).Select(_ => Pick(digits)));
        return $"Reset-{middle}-{tail}";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  DEVICE ALLOWLIST (MDM-lite)
    //
    //  Admin maintains the list of phones / tablets allowed to run the
    //  mobile app. Enforcement is in two places on the server:
    //    1. SyncController.Login refuses unknown or revoked devices.
    //    2. JwtBearer.OnTokenValidated re-checks on every API call so a
    //       revocation takes effect within seconds.
    //
    //  Mobile-side, the user sees the device fingerprint on a "device not
    //  authorised" screen and reads it out to the admin to register here.
    // ══════════════════════════════════════════════════════════════════════════

    [HttpGet]
    public async Task<IActionResult> Devices()
    {
        var devices = await _db.AllowedDevices
            .Include(d => d.AssignedUser)
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync();
        return View(devices);
    }

    [HttpGet]
    public async Task<IActionResult> AddDevice()
    {
        // Operators dropdown for optional assignment. Only active users.
        ViewBag.Users = await _db.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName, u.Email, u.EmployeeNumber })
            .ToListAsync();
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDevice(
        string deviceFingerprint,
        string? label,
        string? manufacturer,
        string? model,
        string? platform,
        string? osVersion,
        string? assignedUserId,
        string? notes)
    {
        if (string.IsNullOrWhiteSpace(deviceFingerprint))
        {
            TempData["Error"] = "Device fingerprint is required.";
            return RedirectToAction("AddDevice");
        }

        var fp = deviceFingerprint.Trim().ToLowerInvariant();
        var existing = await _db.AllowedDevices.FirstOrDefaultAsync(d => d.DeviceFingerprint == fp);
        if (existing != null)
        {
            TempData["Error"] =
                "A device with that fingerprint is already registered" +
                (existing.IsActive ? "" : " (revoked — reactivate it on the Devices page)") + ".";
            return RedirectToAction("Devices");
        }

        var adminId = _users.GetUserId(User);
        var device = new AllowedDevice
        {
            DeviceFingerprint = fp,
            Label             = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            Manufacturer      = string.IsNullOrWhiteSpace(manufacturer) ? null : manufacturer.Trim(),
            Model             = string.IsNullOrWhiteSpace(model)        ? null : model.Trim(),
            Platform          = string.IsNullOrWhiteSpace(platform)     ? null : platform.Trim(),
            OsVersion         = string.IsNullOrWhiteSpace(osVersion)    ? null : osVersion.Trim(),
            AssignedUserId    = string.IsNullOrWhiteSpace(assignedUserId) ? null : assignedUserId.Trim(),
            ApprovedByAdminId = adminId,
            CreatedAt         = DateTime.UtcNow,
            ApprovedAt        = DateTime.UtcNow,
            IsActive          = true,
            Notes             = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        };
        _db.AllowedDevices.Add(device);
        await _db.SaveChangesAsync();

        TempData["Success"] = $"Device {device.Label ?? fp.Substring(0, 8) + "…"} authorised.";
        return RedirectToAction("Devices");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetDeviceActive(int id, bool active)
    {
        var device = await _db.AllowedDevices.FindAsync(id);
        if (device == null)
        {
            TempData["Error"] = "Device not found.";
            return RedirectToAction("Devices");
        }

        if (device.IsActive == active)
        {
            TempData["Success"] = $"Device is already {(active ? "active" : "revoked")}.";
            return RedirectToAction("Devices");
        }

        device.IsActive       = active;
        device.DeactivatedAt  = active ? null : DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = active
            ? $"Device reactivated — user can sign in again."
            : $"Device revoked — user will be signed out the next time their device reaches the server.";
        return RedirectToAction("Devices");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  OPERATOR COMPETENCY (MHSA Section 22(a))
    //
    //  Per-operator licence register. Append-only — revoked / expired /
    //  replaced rows are kept so an inspector can answer historical
    //  questions ("was John competent on this haul truck on 14 March?").
    //  Single source of truth for the rules lives in CompetencyService;
    //  these actions just do CRUD + redirect.
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Roster view — one row per active operator with their
    /// current / expiring / expired chips per machine type.</summary>
    [HttpGet]
    public async Task<IActionResult> Competencies()
    {
        var rows = await _competency.GetRosterAsync();
        return View(rows);
    }

    /// <summary>Per-operator detail — full history of their certificates
    /// + form to add a new one.</summary>
    [HttpGet]
    public async Task<IActionResult> OperatorCompetency(string id)
    {
        var op = await _users.FindByIdAsync(id);
        if (op == null) return NotFound();

        var history = await _competency.GetForOperatorAsync(id);
        ViewBag.Operator = op;
        return View(history);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AddCompetency(
        string operatorId,
        int    machineType,
        string? certificateNumber,
        string? issuedBy,
        DateTime issuedAt,
        DateTime expiresAt,
        string? notes,
        IFormFile? scan)
    {
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            TempData["Error"] = "Operator is required.";
            return RedirectToAction("Competencies");
        }

        var op = await _users.FindByIdAsync(operatorId);
        if (op == null)
        {
            TempData["Error"] = "Operator not found.";
            return RedirectToAction("Competencies");
        }

        // Sanity guard: machine type must be a defined enum value.
        if (!Enum.IsDefined(typeof(MachineType), machineType))
        {
            TempData["Error"] = "Invalid machine type.";
            return RedirectToAction("OperatorCompetency", new { id = operatorId });
        }

        if (expiresAt <= issuedAt)
        {
            TempData["Error"] = "Expiry must be after issue date.";
            return RedirectToAction("OperatorCompetency", new { id = operatorId });
        }

        // Read scan bytes if provided. We store inline (bytea) to match
        // the existing defect-photo / audio storage pattern — one query
        // pulls the whole record + scan together.
        byte[]? scanBytes = null;
        string? scanContentType = null;
        string? scanFileName    = null;
        if (scan != null && scan.Length > 0)
        {
            const long maxBytes = 8 * 1024 * 1024; // 8 MB cap on cert scans
            if (scan.Length > maxBytes)
            {
                TempData["Error"] = "Certificate scan must be under 8 MB.";
                return RedirectToAction("OperatorCompetency", new { id = operatorId });
            }
            using var ms = new MemoryStream();
            await scan.CopyToAsync(ms);
            scanBytes       = ms.ToArray();
            scanContentType = string.IsNullOrEmpty(scan.ContentType)
                                ? "application/octet-stream"
                                : scan.ContentType;
            scanFileName    = scan.FileName;
        }

        // Detect a renewal: if there's an existing active competency for
        // the same operator + machine type, this new one renews it. The
        // OLD row is revoked (its IsActive flips to false) so the freshest
        // valid row is the source of truth. Audit notes the renewal
        // distinctly from a fresh add.
        var existingActive = await _db.OperatorCompetencies
            .Where(c => c.OperatorId == operatorId
                     && c.MachineType == (MachineType)machineType
                     && c.IsActive)
            .ToListAsync();

        var adminId = _users.GetUserId(User);
        foreach (var prev in existingActive)
        {
            prev.IsActive         = false;
            prev.RevokedByAdminId = adminId;
            prev.RevokedAt        = DateTime.UtcNow;
            prev.RevocationReason = "Superseded by renewal";
        }

        var row = new OperatorCompetency
        {
            OperatorId        = operatorId,
            MachineType       = (MachineType)machineType,
            CertificateNumber = string.IsNullOrWhiteSpace(certificateNumber) ? null : certificateNumber.Trim(),
            IssuedBy          = string.IsNullOrWhiteSpace(issuedBy) ? null : issuedBy.Trim(),
            IssuedAt          = DateTime.SpecifyKind(issuedAt,  DateTimeKind.Utc),
            ExpiresAt         = DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc),
            ScanData          = scanBytes,
            ScanContentType   = scanContentType,
            ScanFileName      = scanFileName,
            Notes             = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            CreatedAt         = DateTime.UtcNow,
            AddedByAdminId    = adminId,
            IsActive          = true
        };
        _db.OperatorCompetencies.Add(row);
        await _db.SaveChangesAsync();

        // Audit — distinguish first-time-add from renewal so the trail
        // tells the story over time.
        try
        {
            await _audit.LogAsync(
                action:     existingActive.Count > 0 ? AuditActions.CompetencyRenewed : AuditActions.CompetencyAdded,
                targetType: "OperatorCompetency",
                targetId:   row.Id,
                payload:    new
                {
                    operatorId,
                    operatorName  = op.FullName,
                    machineType   = ((MachineType)machineType).ToString(),
                    certificate   = certificateNumber,
                    issuedBy,
                    issuedAt      = row.IssuedAt,
                    expiresAt     = row.ExpiresAt,
                    supersededIds = existingActive.Select(p => p.Id).ToArray()
                });
        }
        catch { /* audit failure must not block the operator flow */ }

        TempData["Success"] = existingActive.Count > 0
            ? $"Renewed {op.FullName}'s competency on {(MachineType)machineType}. Expires {row.ExpiresAt:yyyy-MM-dd}."
            : $"Added competency for {op.FullName} on {(MachineType)machineType}. Expires {row.ExpiresAt:yyyy-MM-dd}.";
        return RedirectToAction("OperatorCompetency", new { id = operatorId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeCompetency(int id, string? reason)
    {
        var row = await _db.OperatorCompetencies
            .Include(c => c.Operator)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (row == null)
        {
            TempData["Error"] = "Competency not found.";
            return RedirectToAction("Competencies");
        }

        if (!row.IsActive)
        {
            TempData["Success"] = "Competency was already revoked.";
            return RedirectToAction("OperatorCompetency", new { id = row.OperatorId });
        }

        var adminId = _users.GetUserId(User);
        row.IsActive         = false;
        row.RevokedByAdminId = adminId;
        row.RevokedAt        = DateTime.UtcNow;
        row.RevocationReason = string.IsNullOrWhiteSpace(reason) ? "Revoked by admin" : reason.Trim();
        await _db.SaveChangesAsync();

        try
        {
            await _audit.LogAsync(
                action:     AuditActions.CompetencyRevoked,
                targetType: "OperatorCompetency",
                targetId:   row.Id,
                payload:    new
                {
                    operatorId   = row.OperatorId,
                    operatorName = row.Operator?.FullName,
                    machineType  = row.MachineType.ToString(),
                    certificate  = row.CertificateNumber,
                    reason       = row.RevocationReason
                });
        }
        catch { }

        TempData["Success"] = $"Competency revoked. The operator can no longer submit for {row.MachineType}.";
        return RedirectToAction("OperatorCompetency", new { id = row.OperatorId });
    }

    /// <summary>Serve the scanned certificate PDF / image back to the
    /// admin so they can re-verify what's on file.</summary>
    [HttpGet]
    public async Task<IActionResult> CompetencyScan(int id)
    {
        var row = await _db.OperatorCompetencies
            .Where(c => c.Id == id)
            .Select(c => new { c.ScanData, c.ScanContentType, c.ScanFileName })
            .FirstOrDefaultAsync();
        if (row?.ScanData == null || row.ScanData.Length == 0)
            return NotFound();

        // Inline so the browser previews the PDF in a tab rather than
        // forcing a download — admins usually just want to skim and close.
        Response.Headers["Content-Disposition"] =
            $"inline; filename=\"{row.ScanFileName ?? "competency.pdf"}\"";
        return File(row.ScanData, row.ScanContentType ?? "application/pdf");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  ADMIN MANAGEMENT
    //
    //  Adding a new admin is deliberately separated from the regular
    //  CreateEmployee flow because:
    //    1. Granting Admin = full system access. A confused tap in a
    //       dropdown shouldn't ever promote someone by accident — the form
    //       requires the operator to TYPE "GRANT ADMIN" verbatim before
    //       the POST is accepted.
    //    2. SHE / DMR audit trail needs to identify which admin promoted
    //       which other admin, when, and from where. AuditService.LogAsync
    //       captures actor + IP + UA automatically.
    //    3. The list view of current admins makes it obvious how many
    //       backup accounts exist — which is the whole point of this
    //       feature ("the other admin isn't in the office").
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Lists every active Admin user so an admin can see who else
    /// can log in as Admin, with their last-sign-in time as a freshness
    /// signal.</summary>
    [HttpGet]
    public async Task<IActionResult> Admins()
    {
        var adminRoleIds = await _db.Roles
            .Where(r => r.Name == "Admin")
            .Select(r => r.Id)
            .ToListAsync();

        // AspNetUserRoles is shadow-mapped in Identity — query through
        // UserManager rather than the EF DbSet for portability across
        // Identity versions.
        var adminUsers = await _users.GetUsersInRoleAsync("Admin");

        // Order admins newest first so a fresh "I just created this backup
        // admin" entry surfaces at the top of the table.
        var ordered = adminUsers
            .OrderByDescending(u => u.CreatedAt)
            .ToList();

        return View(ordered);
    }

    /// <summary>Renders the Create Admin form. GET only — no state changes
    /// here, so no anti-forgery dance required.</summary>
    [HttpGet]
    public IActionResult CreateAdmin() => View();

    /// <summary>Creates a new user with the Admin role and writes an audit
    /// row. Requires the operator to type a confirmation phrase so a
    /// mis-click can't accidentally grant admin.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateAdmin(
        string fullName,
        string employeeNumber,
        string email,
        string password,
        string confirmation)
    {
        // ── Guardrail #1 — confirmation phrase ─────────────────────────
        // The form requires the operator to type GRANT ADMIN verbatim.
        // A misclick or stray Enter on the regular Employees form can't
        // hit this endpoint because the route is distinct AND the input
        // is required.
        const string RequiredPhrase = "GRANT ADMIN";
        if (!string.Equals(confirmation?.Trim(), RequiredPhrase, StringComparison.Ordinal))
        {
            ModelState.AddModelError("",
                $"Type exactly {RequiredPhrase} (case-sensitive) to confirm you want to grant Admin access.");
            return View();
        }

        // ── Guardrail #2 — input validation ────────────────────────────
        if (string.IsNullOrWhiteSpace(fullName))
        {
            ModelState.AddModelError("", "Full name is required.");
            return View();
        }
        if (string.IsNullOrWhiteSpace(employeeNumber))
        {
            ModelState.AddModelError("", "Employee number is required.");
            return View();
        }
        if (string.IsNullOrWhiteSpace(email) ||
            !email.Contains('@'))
        {
            ModelState.AddModelError("", "A valid email address is required.");
            return View();
        }
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            ModelState.AddModelError("",
                "Password must be at least 8 characters. Use a passphrase the operator can remember offline.");
            return View();
        }

        // ── Guardrail #3 — no duplicate email ──────────────────────────
        var existing = await _users.FindByEmailAsync(email);
        if (existing != null)
        {
            ModelState.AddModelError("",
                "An account with that email already exists. Use Employees → Edit to change their role instead.");
            return View();
        }

        var user = new ApplicationUser
        {
            UserName       = email,
            Email          = email,
            FullName       = fullName.Trim(),
            EmployeeNumber = employeeNumber.Trim(),
            Role           = UserRole.Admin,
            IsActive       = true,
            EmailConfirmed = true
        };

        var result = await _users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            ModelState.AddModelError("",
                string.Join(" ", result.Errors.Select(e => e.Description)));
            return View();
        }

        var roleResult = await _users.AddToRoleAsync(user, "Admin");
        if (!roleResult.Succeeded)
        {
            // Rollback the user so we don't leave an orphan with no role
            // (which is worse than a clean failure — they'd be able to
            // sign in but see nothing).
            await _users.DeleteAsync(user);
            ModelState.AddModelError("",
                "Account was created but Admin role couldn't be assigned. Try again.");
            return View();
        }

        // ── Audit trail ─────────────────────────────────────────────────
        // The actor (current admin) is taken from the HttpContext inside
        // AuditService; we only need to supply the target + payload.
        // PayloadJson is queryable via PG's jsonb so a later investigation
        // can answer "show me every admin promotion in the last 90 days".
        try
        {
            await _audit.LogAsync(
                action:     AuditActions.AdminCreated,
                targetType: "User",
                targetId:   null,
                payload:    new
                {
                    newAdminId    = user.Id,
                    newAdminEmail = user.Email,
                    newAdminName  = user.FullName,
                    employeeNo    = user.EmployeeNumber
                });
        }
        catch
        {
            // Audit write failure must not break the user creation. The
            // user is already created and the role is assigned; the audit
            // gap will surface in a separate alert if any.
        }

        TempData["Success"] =
            $"Admin {fullName} created. They can sign in with {email} and reset their password from the login page.";
        return RedirectToAction("Admins");
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

    // ── Pending admin clearances ──────────────────────────────────────────────
    //
    // A machine lands here when a mechanic completes the LAST open defect on it.
    // Until an admin clicks Clear, IsImmobilised stays true and operators can't
    // start a checklist on it. Tiny working set (rarely more than 5-10 at once)
    // so we don't paginate — render every card on one page.
    [HttpGet]
    public async Task<IActionResult> PendingClearances()
    {
        var machines = await _db.Machines
            .Include(m => m.Submissions)
                .ThenInclude(s => s.Operator)
            .Include(m => m.Submissions)
                .ThenInclude(s => s.DefectOrders)
                    .ThenInclude(d => d.AssignedMechanic)
            .Include(m => m.Submissions)
                .ThenInclude(s => s.DefectOrders)
                    .ThenInclude(d => d.SubmissionItem)
                        .ThenInclude(i => i.TemplateItem)
            .Where(m => m.AwaitingAdminClearance)
            // Oldest first — the machine that's been waiting longest gets cleared first.
            .OrderBy(m => m.Submissions
                .SelectMany(s => s.DefectOrders)
                .Max(d => (DateTime?)d.ResolvedAt))
            .ToListAsync();

        return View(machines);
    }

    /// <summary>
    /// Admin clears a machine back into service. Flips IsImmobilised off,
    /// records who cleared + when + optional notes, audits the action, and
    /// notifies the mechanic(s) whose work was just signed off.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearMachine(int machineId, string? clearanceNotes)
    {
        var machine = await _db.Machines
            .Include(m => m.Submissions)
                .ThenInclude(s => s.DefectOrders)
                    .ThenInclude(d => d.AssignedMechanic)
            .FirstOrDefaultAsync(m => m.Id == machineId);
        if (machine == null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToAction("PendingClearances");
        }

        if (!machine.AwaitingAdminClearance)
        {
            TempData["Error"] = $"{machine.MachineNumber} isn't awaiting clearance.";
            return RedirectToAction("PendingClearances");
        }

        var adminId = _users.GetUserId(User);

        // Capture the mechanics who did the work — every distinct
        // AssignedMechanicId across this machine's completed defects.
        // We notify each one so they know their repair has been signed off.
        var mechanicIds = machine.Submissions
            .SelectMany(s => s.DefectOrders)
            .Where(d => d.RepairStatus == RepairStatus.Completed &&
                        !string.IsNullOrEmpty(d.AssignedMechanicId))
            .Select(d => d.AssignedMechanicId!)
            .Distinct()
            .ToList();

        // ── Commit the clearance ────────────────────────────────────────
        machine.IsImmobilised           = false;
        machine.ImmobilisedReason       = null;
        machine.AwaitingAdminClearance  = false;
        machine.ClearedByAdminId        = adminId;
        machine.ClearedAt               = DateTime.UtcNow;
        machine.AdminClearanceNotes     = string.IsNullOrWhiteSpace(clearanceNotes)
                                            ? null : clearanceNotes.Trim();
        await _db.SaveChangesAsync();

        // ── Audit ───────────────────────────────────────────────────────
        await _audit.LogAsync(
            action:     AuditActions.MachineReleased,
            targetType: "Machine",
            targetId:   machine.Id,
            payload:    new
            {
                reason  = "admin-cleared",
                adminId = adminId,
                notes   = machine.AdminClearanceNotes
            });

        // ── Notify mechanic(s) ──────────────────────────────────────────
        foreach (var mid in mechanicIds)
        {
            await _notifications.PushAsync(
                userId:           mid,
                kind:             NotificationKinds.MachineCleared,
                title:            $"✓ {machine.MachineNumber} cleared by admin",
                body:             string.IsNullOrEmpty(machine.AdminClearanceNotes)
                                    ? "Your repair has been signed off — machine returned to service."
                                    : $"Signed off. Note: {machine.AdminClearanceNotes}",
                relatedMachineId: machine.Id);
        }

        TempData["Success"] = $"{machine.MachineNumber} cleared and returned to service.";
        return RedirectToAction("PendingClearances");
    }

    // ── Audit trail ───────────────────────────────────────────────────────────
    // Browse the append-only AuditEvents table. Supports the standard
    // ListFilter (search across actor name/email + action constant +
    // date range on OccurredAtServer) PLUS a dedicated action dropdown,
    // and an actor email pre-filter so the per-user "what did Sipho do
    // this morning?" question is one click from the user list.
    [HttpGet]
    public async Task<IActionResult> Audit([FromQuery] ListFilter filter,
                                           [FromQuery] string? action = null,
                                           [FromQuery] string? actor  = null,
                                           [FromQuery] int     page   = 1)
    {
        FilterPresets.Apply(filter);
        // Default window — last 7 days. Audit volumes grow quickly so a wider
        // default would chew RAM on busy mines.
        filter.From ??= DateTime.UtcNow.Date.AddDays(-7);
        filter.To   ??= DateTime.UtcNow.Date;

        const int PAGE_SIZE = 50;
        if (page < 1) page = 1;

        IQueryable<AuditEvent> q = _db.AuditEvents
            .ApplyDateRange(filter, e => e.OccurredAtServer);

        if (!string.IsNullOrWhiteSpace(action))
            q = q.Where(e => e.Action == action);
        if (!string.IsNullOrWhiteSpace(actor))
            q = q.Where(e => e.ActorEmail == actor);

        // Materialise enough rows to comfortably search through in memory
        // (the search hits ActorName/Email which are denormalised onto the
        // row, so SQL Where would also work — but keeping the same pattern
        // as the other list pages reads cleaner).
        var loaded = await q
            .OrderByDescending(e => e.OccurredAtServer)
            .Take(5000)
            .ToListAsync();

        var filtered = loaded.ApplySearchInMemory(filter,
            e => e.ActorName,
            e => e.ActorEmail,
            e => e.Action,
            e => e.TargetType,
            e => e.PayloadJson);

        var total      = filtered.Count;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PAGE_SIZE));
        if (page > totalPages) page = totalPages;

        var rows = filtered.Skip((page - 1) * PAGE_SIZE).Take(PAGE_SIZE).ToList();

        // Distinct action names from the loaded window populate the dropdown
        // — way better than hard-coding them, because admins can add new ones
        // without touching the view.
        var actions = loaded.Select(e => e.Action).Distinct().OrderBy(a => a).ToList();

        ViewBag.Filter      = filter;
        // Named `ActionFilter` (not `Action`) because the _ListFilterBar
        // partial is invoked with a ViewDataDictionary that sets ViewData["Action"]
        // to the form's POST URL — having both keys collide blows up the
        // initializer with "An item with the same key has already been added".
        ViewBag.ActionFilter = action;
        ViewBag.Actor       = actor;
        ViewBag.Actions     = actions;
        ViewBag.Page        = page;
        ViewBag.TotalPages  = totalPages;
        ViewBag.TotalRows   = total;
        ViewBag.WindowSize  = loaded.Count;
        return View(rows);
    }

    // ── Reports ───────────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Reports([FromQuery] ListFilter filter)
    {
        // Resolve preset chips into concrete dates, then default empty
        // bounds to the "last 30 days" window the page has used forever.
        FilterPresets.Apply(filter);
        filter.From ??= DateTime.UtcNow.Date.AddDays(-30);
        filter.To   ??= DateTime.UtcNow.Date;

        // KPIs, trends, top-N — single round-trip into the dashboard service.
        var dashboard = await _reports.BuildDashboardAsync(filter.From, filter.To);

        // Pulled separately because the table still wants the full join with
        // search applied; the dashboard payload deals only in aggregates.
        var loaded = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items)
            .ApplyDateRange(filter, s => s.SubmittedAt)
            .OrderByDescending(s => s.SubmittedAt)
            .Take(1000)
            .ToListAsync();

        var submissions = loaded.ApplySearchInMemory(
            filter,
            s => s.Machine.MachineNumber,
            s => s.Machine.MachineName,
            s => s.Operator.FullName,
            s => s.Operator.EmployeeNumber);

        ViewBag.Filter          = filter;
        ViewBag.Dashboard       = dashboard;
        ViewBag.From            = filter.From;   // legacy compat for any other binders
        ViewBag.To              = filter.To;
        ViewBag.TotalUnfiltered = loaded.Count;
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

    // ══════════════════════════════════════════════════════════════════════════
    //  APPLICATION SETTINGS (DB-backed config)
    //
    //  Lists every editable setting grouped by Category. Edits write back
    //  through ConfigurationService (which invalidates its cache) and
    //  audit-log the change. Secret values render password-masked.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet]
    public async Task<IActionResult> Settings()
    {
        // GetAllAsync masks secrets before returning.
        var rows = await _settings.GetAllAsync();
        return View(rows);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSetting(int id, string? value)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == id);
        if (row == null)
        {
            TempData["Error"] = "Setting not found.";
            return RedirectToAction("Settings");
        }

        // Secret heuristic: if the form value is empty AND the row is a
        // secret, leave the existing value alone — empty submit on a
        // masked field shouldn't wipe the credential.
        if (row.IsSecret && string.IsNullOrEmpty(value))
        {
            TempData["Success"] = $"{row.Key} unchanged.";
            return RedirectToAction("Settings");
        }

        var actorId = _users.GetUserId(User);
        await _settings.SetAsync(row.Key, value, actorId);

        try
        {
            // Don't write the secret value into the audit payload — just
            // record that it changed. Non-secret values are captured so a
            // future investigation can see the before/after.
            await _audit.LogAsync(
                action:     AuditActions.SettingsChanged,
                targetType: "AppSetting",
                targetId:   row.Id,
                payload:    new
                {
                    key       = row.Key,
                    category  = row.Category,
                    oldValue  = row.IsSecret ? "***" : row.Value,
                    newValue  = row.IsSecret ? "***" : value,
                    isSecret  = row.IsSecret
                });
        }
        catch { /* audit failure must not block the operator flow */ }

        TempData["Success"] = $"{row.Key} updated.";
        return RedirectToAction("Settings");
    }

    /// <summary>
    /// Reset a setting back to its seeded DefaultValue. Useful when an
    /// admin fat-fingers a value and wants to undo without remembering
    /// what the original was. Audited under settings.reset.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetSetting(int id)
    {
        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == id);
        if (row == null)
        {
            TempData["Error"] = "Setting not found.";
            return RedirectToAction("Settings");
        }

        if (row.DefaultValue == null && row.Value == null)
        {
            TempData["Success"] = $"{row.Key} is already at its default (unset).";
            return RedirectToAction("Settings");
        }

        var prior = row.Value;
        var actorId = _users.GetUserId(User);
        await _settings.SetAsync(row.Key, row.DefaultValue, actorId);

        try
        {
            await _audit.LogAsync(
                action:     AuditActions.SettingsReset,
                targetType: "AppSetting",
                targetId:   row.Id,
                payload:    new
                {
                    key       = row.Key,
                    category  = row.Category,
                    oldValue  = row.IsSecret ? "***" : prior,
                    newValue  = row.IsSecret ? "***" : row.DefaultValue,
                    isSecret  = row.IsSecret
                });
        }
        catch { }

        TempData["Success"] = $"{row.Key} reset to default.";
        return RedirectToAction("Settings");
    }

    /// <summary>
    /// Fire an SMTP self-test to the signed-in admin's address using the
    /// current Email.* settings. Surfaces the actual SMTP error if the
    /// send fails so the admin can diagnose without leaving the page.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestEmail()
    {
        var actor = await _users.GetUserAsync(User);
        if (actor == null || string.IsNullOrWhiteSpace(actor.Email))
        {
            TempData["Error"] = "Sign-in account has no email address on file. Add one before testing.";
            return RedirectToAction("Settings");
        }

        var error = await _email.SendTestAsync(actor.Email, actor.FullName ?? actor.Email);
        if (error == null)
        {
            TempData["Success"] =
                $"Test email sent to {actor.Email}. Check your inbox — it'll arrive within a few seconds.";
        }
        else
        {
            // Show the actual SMTP failure so the admin doesn't have to
            // tail logs to know why it didn't work.
            TempData["Error"] = $"SMTP test failed: {error}";
        }
        return RedirectToAction("Settings");
    }
}
