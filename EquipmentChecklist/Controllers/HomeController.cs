using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EquipmentChecklist.Controllers;

[Authorize]
public class HomeController : Controller
{
    private readonly ChecklistService _svc;
    public HomeController(ChecklistService svc) => _svc = svc;

    public async Task<IActionResult> Index()
    {
        var stats = await _svc.GetDashboardStatsAsync();
        return View(stats);
    }
}
