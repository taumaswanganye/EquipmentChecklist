using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EquipmentChecklist.Controllers;

/// <summary>
/// Allows Admin users to browse all open defects, add to a cart,
/// and submit a parts order (with email confirmation).
/// Route prefix: /Admin/Mechanic  (keeps it under Admin nav)
/// </summary>
[Authorize(Roles = "Admin")]
[Route("Admin/Mechanic")]
public class AdminMechanicController : Controller
{
    private readonly ApplicationDbContext         _db;
    private readonly UserManager<ApplicationUser> _users;
    private readonly EmailService                 _email;

    public AdminMechanicController(
        ApplicationDbContext db,
        UserManager<ApplicationUser> users,
        EmailService email)
    {
        _db    = db;
        _users = users;
        _email = email;
    }

    // ── All open defect orders ────────────────────────────────────────────────
    [HttpGet("Cart")]
    public async Task<IActionResult> Cart()
    {
        var allOpen = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Include(d => d.Submission).ThenInclude(s => s.Operator)
            .Where(d => d.RepairStatus != RepairStatus.Completed)
            .OrderByDescending(d => d.Submission.Machine.IsImmobilised)
            .ThenBy(d => d.CreatedAt)
            .ToListAsync();

        var cart      = GetCart();
        var cartIds   = cart.Select(c => c.DefectOrderId).ToHashSet();
        ViewBag.Cart  = cart;
        ViewBag.CartIds = cartIds;
        return View(allOpen);
    }

    // ── Add / Remove from cart ────────────────────────────────────────────────
    [HttpPost("AddToCart"), ValidateAntiForgeryToken]
    public IActionResult AddToCart(int defectOrderId, string itemName)
    {
        var cart = GetCart();
        if (!cart.Any(c => c.DefectOrderId == defectOrderId))
            cart.Add(new CartItem { DefectOrderId = defectOrderId, ItemName = itemName });
        SaveCart(cart);
        return RedirectToAction("Cart");
    }

    [HttpPost("RemoveFromCart"), ValidateAntiForgeryToken]
    public IActionResult RemoveFromCart(int defectOrderId)
    {
        var cart = GetCart();
        cart.RemoveAll(c => c.DefectOrderId == defectOrderId);
        SaveCart(cart);
        return RedirectToAction("Checkout");
    }

    // ── Checkout page ─────────────────────────────────────────────────────────
    [HttpGet("Checkout")]
    public async Task<IActionResult> Checkout()
    {
        var cart = GetCart();
        if (!cart.Any()) return RedirectToAction("Cart");

        var ids    = cart.Select(c => c.DefectOrderId).ToList();
        var orders = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Where(d => ids.Contains(d.Id))
            .ToListAsync();

        var user = await _users.GetUserAsync(User);
        ViewBag.AdminEmail = user?.Email ?? "";
        ViewBag.AdminName  = user?.FullName ?? "";

        return View(orders);
    }

    // ── Submit order + send email ─────────────────────────────────────────────
    [HttpPost("SubmitOrder"), ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitOrder(
        List<AdminOrderLineDto> lines,
        string toEmail,
        string? notes)
    {
        var userId = _users.GetUserId(User)!;
        var user   = await _users.GetUserAsync(User);
        var ref_   = $"BO-{DateTime.UtcNow:yyyyMMdd-HHmm}-{new Random().Next(100,999)}";

        var emailLines = new List<OrderLineItem>();

        foreach (var line in lines)
        {
            var order = await _db.DefectOrders
                .Include(d => d.Submission).ThenInclude(s => s.Machine)
                .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
                .FirstOrDefaultAsync(d => d.Id == line.DefectOrderId);

            if (order == null) continue;

            order.PartRequired       = line.PartRequired;
            order.PartNumber         = line.PartNumber;
            order.RepairStatus       = RepairStatus.AwaitingParts;
            order.AssignedMechanicId = userId;

            emailLines.Add(new OrderLineItem
            {
                MachineNumber = order.Submission.Machine.MachineNumber,
                MachineName   = order.Submission.Machine.MachineName,
                DefectItem    = order.SubmissionItem.TemplateItem.ItemName,
                PartRequired  = line.PartRequired,
                PartNumber    = line.PartNumber
            });
        }

        await _db.SaveChangesAsync();
        SaveCart(new List<CartItem>());

        // Send confirmation email
        if (!string.IsNullOrEmpty(toEmail))
        {
            await _email.SendOrderConfirmationAsync(
                toEmail,
                user?.FullName ?? "Admin",
                emailLines,
                ref_);
        }

        TempData["OrderRef"]     = ref_;
        TempData["OrderCount"]   = lines.Count;
        TempData["OrderEmail"]   = toEmail;
        return RedirectToAction("OrderSuccess");
    }

    // ── Success page ──────────────────────────────────────────────────────────
    [HttpGet("OrderSuccess")]
    public IActionResult OrderSuccess() => View();

    // ── Session cart helpers ──────────────────────────────────────────────────
    private List<CartItem> GetCart()
    {
        var json = HttpContext.Session.GetString("AdminCart") ?? "[]";
        return JsonSerializer.Deserialize<List<CartItem>>(json) ?? new();
    }
    private void SaveCart(List<CartItem> cart) =>
        HttpContext.Session.SetString("AdminCart", JsonSerializer.Serialize(cart));
}

public class AdminOrderLineDto
{
    public int     DefectOrderId { get; set; }
    public string  PartRequired  { get; set; } = "";
    public string? PartNumber    { get; set; }
}
