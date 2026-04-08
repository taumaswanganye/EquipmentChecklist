using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace EquipmentChecklist.Controllers;

public class AccountController : Controller
{
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly UserManager<ApplicationUser>   _users;

    public AccountController(
        SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser>   users)
    {
        _signIn = signIn;
        _users  = users;
    }

    // ── Login ─────────────────────────────────────────────────────────────────
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (_signIn.IsSignedIn(User)) return RedirectToAction("Index", "Home");
        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string email, string password,
                                           bool rememberMe, string? returnUrl = null)
    {
        var result = await _signIn.PasswordSignInAsync(
            email, password, rememberMe, lockoutOnFailure: true);

        if (result.Succeeded)
        {
            if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
                return Redirect(returnUrl);
            return RedirectToAction("Index", "Home");
        }

        if (result.IsLockedOut)
            TempData["Error"] = "Account locked after too many failed attempts. Try again in 5 minutes.";
        else
            TempData["Error"] = "Invalid email or password.";

        ViewBag.ReturnUrl = returnUrl;
        return View();
    }

    // ── Logout ────────────────────────────────────────────────────────────────
    [HttpPost, ValidateAntiForgeryToken, Authorize]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction("Login");
    }

    // ── Change Password ───────────────────────────────────────────────────────
    [HttpGet, Authorize]
    public IActionResult ChangePassword() => View();

    [HttpPost, Authorize, ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(
        string currentPassword, string newPassword, string confirmPassword)
    {
        if (newPassword != confirmPassword)
        {
            TempData["Error"] = "New passwords do not match.";
            return View();
        }

        var user = await _users.GetUserAsync(User);
        if (user == null) return RedirectToAction("Login");

        var result = await _users.ChangePasswordAsync(user, currentPassword, newPassword);
        if (result.Succeeded)
        {
            await _signIn.RefreshSignInAsync(user);
            TempData["Success"] = "Password changed successfully.";
            return RedirectToAction("Index", "Home");
        }

        TempData["Error"] = string.Join(" ", result.Errors.Select(e => e.Description));
        return View();
    }

    // ── Access Denied ─────────────────────────────────────────────────────────
    public IActionResult AccessDenied() => View();
}
