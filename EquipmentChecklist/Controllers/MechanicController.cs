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

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> CompleteRepair(int defectOrderId, string? notes)
    {
        var userId = _users.GetUserId(User)!;
        try { await _svc.ResolveDefectAsync(defectOrderId, userId, notes ?? "Repair completed."); TempData["Success"] = "Repair marked as complete."; }
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
