using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EquipmentChecklist.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly ChecklistService     _svc;
    private readonly ConfigurationService _config;

    public HomeController(ChecklistService svc, ConfigurationService config)
    {
        _svc    = svc;
        _config = config;
    }

    public async Task<IActionResult> Index()
    {
        var stats = await _svc.GetDashboardStatsAsync();
        // Mine identity surfaces in the page subtitle — DB-backed so an
        // admin can change "Belfast Coal Mine" to anything from
        // Admin → Settings without redeploying.
        ViewBag.MineName    = await _config.GetAsync("Mine.Name")    ?? "Equipment Checklist System";
        ViewBag.MineTagline = await _config.GetAsync("Mine.Tagline") ?? "";
        return View(stats);
    }
}
