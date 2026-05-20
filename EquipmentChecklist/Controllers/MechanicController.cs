using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EquipmentChecklist.Controllers;

[Authorize(Roles = "Admin,Mechanic")]
public class MechanicController : Controller
{
    private readonly ApplicationDbContext         _db;
    private readonly ChecklistService             _svc;
    private readonly UserManager<ApplicationUser> _users;
    private readonly EmailService                 _email;
    private readonly PdfService                   _pdf;

    public MechanicController(ApplicationDbContext db, ChecklistService svc,
                               UserManager<ApplicationUser> users, EmailService email, PdfService pdf)
    { _db = db; _svc = svc; _users = users; _email = email; _pdf = pdf; }

    public async Task<IActionResult> Index()
    {
        var userId = _users.GetUserId(User)!;

        var myOrders = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Where(d => d.AssignedMechanicId == userId && d.RepairStatus != RepairStatus.Completed)
            .OrderByDescending(d => d.Submission.Machine.IsImmobilised).ThenBy(d => d.CreatedAt)
            .ToListAsync();

        var unassigned = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Where(d => d.AssignedMechanicId == null && d.RepairStatus == RepairStatus.Pending)
            .OrderByDescending(d => d.Submission.Machine.IsImmobilised).ThenBy(d => d.CreatedAt)
            .ToListAsync();

        var noGoMachines = await _db.Machines
            .Include(m => m.Submissions.OrderByDescending(s => s.SubmittedAt).Take(1))
                .ThenInclude(s => s.Items).ThenInclude(i => i.TemplateItem)
            .Include(m => m.Submissions).ThenInclude(s => s.DefectOrders)
            .Where(m => m.IsImmobilised)
            .ToListAsync();

        ViewBag.Unassigned   = unassigned;
        ViewBag.NoGoMachines = noGoMachines;
        ViewBag.CartCount    = GetCart().Count;
        return View(myOrders);
    }

    [HttpGet("/Mechanic/NoGoDetail/{machineId:int}")]
    public async Task<IActionResult> NoGoDetail(int machineId)
    {
        var machine = await _db.Machines
            .Include(m => m.Submissions.OrderByDescending(s => s.SubmittedAt).Take(1))
                .ThenInclude(s => s.Items).ThenInclude(i => i.TemplateItem)
            .Include(m => m.Submissions).ThenInclude(s => s.DefectOrders)
            .Include(m => m.Submissions).ThenInclude(s => s.Operator)
            .FirstOrDefaultAsync(m => m.Id == machineId);
        if (machine == null) return RedirectToAction("Index");

        // Is the signed-in user the responsible mechanic for this machine?
        var userId            = _users.GetUserId(User)!;
        var isAdmin           = User.IsInRole("Admin");
        var isAssignedMech    = await _db.MachineAssignments
            .AnyAsync(a => a.MachineId == machineId && a.IsActive && a.MechanicId == userId);
        ViewBag.CanOrderParts = machine.IsImmobilised && (isAdmin || isAssignedMech);

        return View(machine);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult AddToCart(int defectOrderId, string itemName)
    {
        var cart = GetCart();
        if (!cart.Any(c => c.DefectOrderId == defectOrderId))
            cart.Add(new CartItem { DefectOrderId = defectOrderId, ItemName = itemName });
        SaveCart(cart);
        TempData["Success"] = $"Added to parts cart.";
        return RedirectToAction("Index");
    }

    /// <summary>
    /// Used from the NoGoDetail screen: takes a SubmissionItem, finds-or-creates
    /// its DefectOrder, assigns it to the current mechanic, and adds it to the
    /// parts cart. Only succeeds when the machine is immobilised AND the current
    /// user is its currently-assigned mechanic (or an Admin).
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> EnsureOrderAndAddToCart(int submissionItemId, int machineId)
    {
        var userId  = _users.GetUserId(User)!;
        var isAdmin = User.IsInRole("Admin");

        var machine = await _db.Machines.FindAsync(machineId);
        if (machine == null)
        {
            TempData["Error"] = "Machine not found.";
            return RedirectToAction("Index");
        }
        if (!machine.IsImmobilised)
        {
            TempData["Error"] = $"{machine.MachineNumber} is not immobilised — parts can only be ordered for immobilised machines from this screen.";
            return RedirectToAction("NoGoDetail", new { machineId });
        }

        // Rule: only the responsible mechanic on this machine (or an admin) may order parts here.
        if (!isAdmin)
        {
            var isResponsible = await _db.MachineAssignments
                .AnyAsync(a => a.MachineId == machineId && a.IsActive && a.MechanicId == userId);
            if (!isResponsible)
            {
                TempData["Error"] = $"You are not the assigned mechanic for {machine.MachineNumber}.";
                return RedirectToAction("NoGoDetail", new { machineId });
            }
        }

        var item = await _db.SubmissionItems
            .Include(i => i.TemplateItem)
            .Include(i => i.Submission)
            .FirstOrDefaultAsync(i => i.Id == submissionItemId);
        if (item == null || item.Status != ItemStatus.Defect)
        {
            TempData["Error"] = "Selected item is not a defect.";
            return RedirectToAction("NoGoDetail", new { machineId });
        }

        // Find or create the open DefectOrder for this SubmissionItem
        var order = await _db.DefectOrders
            .FirstOrDefaultAsync(d => d.SubmissionItemId == submissionItemId
                                   && d.RepairStatus != RepairStatus.Completed);
        if (order == null)
        {
            var desc = item.Notes ?? item.TemplateItem.ItemName;
            if (desc.Length > 200) desc = desc.Substring(0, 200);

            order = new DefectOrder
            {
                SubmissionId       = item.SubmissionId,
                SubmissionItemId   = item.Id,
                DefectDescription  = desc,
                AssignedMechanicId = userId,
                RepairStatus       = RepairStatus.InProgress,
                CreatedAt          = DateTime.UtcNow
            };
            _db.DefectOrders.Add(order);
            await _db.SaveChangesAsync();
        }
        else if (order.AssignedMechanicId == null)
        {
            order.AssignedMechanicId = userId;
            order.RepairStatus       = RepairStatus.InProgress;
            await _db.SaveChangesAsync();
        }

        // Add to mechanic's cart
        var cart = GetCart();
        if (!cart.Any(c => c.DefectOrderId == order.Id))
            cart.Add(new CartItem { DefectOrderId = order.Id, ItemName = item.TemplateItem.ItemName });
        SaveCart(cart);

        TempData["Success"] = $"'{item.TemplateItem.ItemName}' added to your parts cart.";
        return RedirectToAction("NoGoDetail", new { machineId });
    }

    /// <summary>Assigns an unassigned defect to the current mechanic AND adds it to the cart in one step.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignAndAddToCart(int defectOrderId, string itemName)
    {
        var userId = _users.GetUserId(User)!;
        var order  = await _db.DefectOrders.FindAsync(defectOrderId);
        if (order != null && order.AssignedMechanicId == null)
        {
            order.AssignedMechanicId = userId;
            order.RepairStatus       = RepairStatus.InProgress;
            await _db.SaveChangesAsync();
        }

        var cart = GetCart();
        if (!cart.Any(c => c.DefectOrderId == defectOrderId))
            cart.Add(new CartItem { DefectOrderId = defectOrderId, ItemName = itemName });
        SaveCart(cart);

        TempData["Success"] = "Job assigned to you and added to the parts cart.";
        return RedirectToAction("Cart");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult RemoveFromCart(int defectOrderId)
    {
        var cart = GetCart();
        cart.RemoveAll(c => c.DefectOrderId == defectOrderId);
        SaveCart(cart);
        return RedirectToAction("Cart");
    }

    [HttpGet]
    public async Task<IActionResult> Cart()
    {
        var cart = GetCart();
        var ids  = cart.Select(c => c.DefectOrderId).ToList();
        var orders = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Where(d => ids.Contains(d.Id))
            .ToListAsync();
        ViewBag.Cart = cart;
        return View(orders);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitCartOrders(List<CartOrderDto> orders)
    {
        var userId = _users.GetUserId(User)!;
        var mechanic = await _users.FindByIdAsync(userId);

        var updatedOrders = new List<DefectOrder>();

        foreach (var o in orders)
        {
            var order = await _db.DefectOrders
                .Include(d => d.Submission).ThenInclude(s => s.Machine)
                .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
                .FirstOrDefaultAsync(d => d.Id == o.DefectOrderId);
            if (order == null) continue;
            order.PartRequired       = o.PartRequired;
            order.PartNumber         = o.PartNumber;
            order.RepairStatus       = RepairStatus.AwaitingParts;
            order.AssignedMechanicId = userId;
            updatedOrders.Add(order);
        }

        await _db.SaveChangesAsync();
        SaveCart(new List<CartItem>());

        // Send confirmation email to mechanic + parts order PDF to manager
        if (mechanic != null && !string.IsNullOrEmpty(mechanic.Email) && updatedOrders.Any())
        {
            var orderRef = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            var lineItems = updatedOrders.Select(d => new OrderLineItem
            {
                MachineNumber = d.Submission.Machine.MachineNumber,
                MachineName   = d.Submission.Machine.MachineName,
                DefectItem    = d.SubmissionItem.TemplateItem.ItemName,
                PartRequired  = d.PartRequired ?? "",
                PartNumber    = d.PartNumber
            }).ToList();

            var partsOrderPdf = _pdf.GeneratePartsOrderPdf(mechanic.FullName, lineItems, orderRef);

            var sent = await _email.SendOrderConfirmationAsync(
                mechanic.Email, mechanic.FullName, lineItems, orderRef, partsOrderPdf);
            TempData["Success"] = sent
                ? $"{orders.Count} part order(s) submitted. Confirmation sent to you and manager notified."
                : $"{orders.Count} part order(s) saved. (Email not configured — manager was not notified via email.)";
        }
        else
        {
            TempData["Success"] = $"{orders.Count} part order(s) saved.";
        }
        return RedirectToAction("Index");
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignToSelf(int defectOrderId)
    {
        var userId = _users.GetUserId(User)!;
        var order  = await _db.DefectOrders.FindAsync(defectOrderId);
        if (order != null) { order.AssignedMechanicId = userId; order.RepairStatus = RepairStatus.InProgress; await _db.SaveChangesAsync(); TempData["Success"] = "Assigned to you."; }
        return RedirectToAction("Index");
    }

    [HttpGet("/Mechanic/OrderPart/{id:int}")]
    public async Task<IActionResult> OrderPart(int id)
    {
        var order = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (order == null) return RedirectToAction("Index");
        return View(order);
    }

    [HttpPost("/Mechanic/OrderPart/{id:int}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> OrderPart(int id, string partRequired, string? partNumber)
    {
        var userId = _users.GetUserId(User)!;
        var mechanic = await _users.FindByIdAsync(userId);
        var order = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (order != null)
        {
            order.PartRequired       = partRequired;
            order.PartNumber         = partNumber;
            order.RepairStatus       = RepairStatus.AwaitingParts;
            order.AssignedMechanicId = userId;
            await _db.SaveChangesAsync();

            // Send confirmation email to mechanic + parts order PDF to manager
            if (mechanic != null && !string.IsNullOrEmpty(mechanic.Email))
            {
                var orderRef  = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var lineItems = new List<OrderLineItem>
                {
                    new OrderLineItem
                    {
                        MachineNumber = order.Submission.Machine.MachineNumber,
                        MachineName   = order.Submission.Machine.MachineName,
                        DefectItem    = order.SubmissionItem.TemplateItem.ItemName,
                        PartRequired  = partRequired,
                        PartNumber    = partNumber
                    }
                };

                var partsOrderPdf = _pdf.GeneratePartsOrderPdf(mechanic.FullName, lineItems, orderRef);

                var sent = await _email.SendOrderConfirmationAsync(
                    mechanic.Email, mechanic.FullName, lineItems, orderRef, partsOrderPdf);
                TempData["Success"] = sent
                    ? "Part order submitted. Confirmation sent to you and manager notified."
                    : "Part order saved. (Email not configured — manager was not notified via email.)";
            }
            else
            {
                TempData["Success"] = "Part order saved.";
            }
        }
        return RedirectToAction("Index");
    }

    // ── My Operators ──────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> MyOperators()
    {
        var userId  = _users.GetUserId(User)!;
        var isAdmin = User.IsInRole("Admin");

        // Machines this mechanic is responsible for → operators who drive them
        IQueryable<MachineAssignment> q = _db.MachineAssignments
            .Include(a => a.Machine)
            .Include(a => a.Operator)
            .Where(a => a.IsActive);
        if (!isAdmin)
            q = q.Where(a => a.MechanicId == userId);

        var assignments = await q.ToListAsync();

        // Group by operator so the page lists each one once with all machines underneath
        var byOperator = assignments
            .GroupBy(a => a.Operator)
            .OrderBy(g => g.Key.FullName)
            .ToList();

        var operatorIds = byOperator.Select(g => g.Key.Id).ToList();
        var since       = DateTime.UtcNow.Date.AddDays(-30);

        // Defects on my machines, grouped by operator
        var defectsByOperator = await _db.DefectOrders
            .Include(d => d.Submission)
            .Where(d => operatorIds.Contains(d.Submission.OperatorId)
                     && d.AssignedMechanicId == (isAdmin ? d.AssignedMechanicId : userId)
                     && d.RepairStatus != RepairStatus.Completed)
            .GroupBy(d => d.Submission.OperatorId)
            .Select(g => new { OperatorId = g.Key, Open = g.Count() })
            .ToListAsync();

        // 30-day NO-GO counts per operator (on my machines)
        var myMachineIds = assignments.Select(a => a.MachineId).Distinct().ToList();
        var noGoByOperator = await _db.ChecklistSubmissions
            .Where(s => operatorIds.Contains(s.OperatorId)
                     && myMachineIds.Contains(s.MachineId)
                     && s.SubmittedAt >= since
                     && s.Status == ChecklistStatus.NoGo)
            .GroupBy(s => s.OperatorId)
            .Select(g => new { OperatorId = g.Key, Count = g.Count() })
            .ToListAsync();

        ViewBag.OpenDefectsByOperator = defectsByOperator.ToDictionary(x => x.OperatorId, x => x.Open);
        ViewBag.NoGoByOperator        = noGoByOperator.ToDictionary(x => x.OperatorId, x => x.Count);

        return View(byOperator);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteRepair(int defectOrderId, string? notes, string? mechanicSignature)
    {
        var userId = _users.GetUserId(User)!;
        if (string.IsNullOrWhiteSpace(mechanicSignature))
        {
            TempData["Error"] = "A digital signature is required to close this repair.";
            return RedirectToAction("Index");
        }
        try
        {
            await _svc.ResolveDefectAsync(defectOrderId, userId, notes ?? "Repair completed.", mechanicSignature);
            TempData["Success"] = "Repair marked as complete.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction("Index");
    }

    private List<CartItem> GetCart()
    {
        var json = HttpContext.Session.GetString("MechanicCart") ?? "[]";
        return JsonSerializer.Deserialize<List<CartItem>>(json) ?? new();
    }
    private void SaveCart(List<CartItem> cart) =>
        HttpContext.Session.SetString("MechanicCart", JsonSerializer.Serialize(cart));
}

public class CartItem { public int DefectOrderId { get; set; } public string ItemName { get; set; } = ""; }
public class CartOrderDto { public int DefectOrderId { get; set; } public string PartRequired { get; set; } = ""; public string? PartNumber { get; set; } }
