using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Controllers.Api;

// ── Checklist API ─────────────────────────────────────────────────────────────
[ApiController, Route("api/checklist"), Authorize]
public class ChecklistApiController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly ChecklistService _checklistService;
    private readonly UserManager<ApplicationUser> _userManager;

    public ChecklistApiController(ApplicationDbContext db, ChecklistService cs,
        UserManager<ApplicationUser> um)
    {
        _db = db; _checklistService = cs; _userManager = um;
    }

    /// <summary>GET api/checklist/template/{machineId} – returns checklist items for offline use</summary>
    [HttpGet("template/{machineId}")]
    public async Task<IActionResult> GetTemplate(int machineId)
    {
        var template = await _db.ChecklistTemplates
            .Include(t => t.Items.OrderBy(i => i.SortOrder))
            .FirstOrDefaultAsync(t => t.MachineId == machineId);

        if (template == null) return NotFound();
        return Ok(template);
    }

    /// <summary>POST api/checklist/submit – online submission</summary>
    [HttpPost("submit")]
    public async Task<IActionResult> Submit([FromBody] SubmitChecklistDto dto)
    {
        var user = await _userManager.GetUserAsync(User);
        var submission = await _checklistService.ProcessSubmissionAsync(dto, user!.Id);
        return Ok(new { submission.Id, submission.Status });
    }

    /// <summary>GET api/checklist/dashboard – stats for dashboard widgets</summary>
    [HttpGet("dashboard")]
    public async Task<IActionResult> Dashboard()
    {
        var stats = await _checklistService.GetDashboardStatsAsync();
        return Ok(stats);
    }
}

// ── Machine API ───────────────────────────────────────────────────────────────
[ApiController, Route("api/machines"), Authorize]
public class MachineApiController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;

    public MachineApiController(ApplicationDbContext db, UserManager<ApplicationUser> um)
    {
        _db = db; _userManager = um;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var machines = await _db.Machines
            .Where(m => m.IsActive)
            .Select(m => new
            {
                m.Id, m.MachineNumber, m.MachineName,
                m.Type, m.IsImmobilised, m.ImmobilisedReason
            })
            .ToListAsync();
        return Ok(machines);
    }

    [HttpGet("assigned")]
    public async Task<IActionResult> GetAssigned()
    {
        var user = await _userManager.GetUserAsync(User);
        var machines = await _db.MachineAssignments
            .Include(a => a.Machine)
            .Where(a => a.OperatorId == user!.Id && a.IsActive)
            .Select(a => new
            {
                a.Machine.Id,
                a.Machine.MachineNumber,
                a.Machine.MachineName,
                a.Machine.Type,
                a.Machine.IsImmobilised,
                a.Machine.ImmobilisedReason
            })
            .ToListAsync();
        return Ok(machines);
    }
}

// ── Sync API ──────────────────────────────────────────────────────────────────
[ApiController, Route("api/sync"), Authorize]
public class SyncApiController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public SyncApiController(ApplicationDbContext db) => _db = db;

    /// <summary>POST api/sync/push – batch upsert offline submissions</summary>
    [HttpPost("push")]
    public async Task<IActionResult> Push([FromBody] SyncPayloadDto payload)
    {
        var results = new List<object>();
        foreach (var dto in payload.Submissions)
        {
            var exists = await _db.ChecklistSubmissions
                .AnyAsync(s => s.LocalId == dto.LocalId);

            if (!exists)
            {
                var items = dto.Items.Select(i => new SubmissionItem
                {
                    TemplateItemId = i.TemplateItemId,
                    Status = i.Status,
                    Notes = i.Notes
                }).ToList();

                var status = ChecklistService.CalculateStatus(items,
                    await _db.ChecklistTemplateItems.ToListAsync());

                _db.ChecklistSubmissions.Add(new ChecklistSubmission
                {
                    LocalId = dto.LocalId,
                    MachineId = dto.MachineId,
                    OperatorId = dto.OperatorId,
                    Shift = dto.Shift,
                    SubmittedAt = dto.SubmittedAt,
                    KmOrHourMeter = dto.KmOrHourMeter,
                    OperatorRemarks = dto.OperatorRemarks,
                    FitnessDeclarationSigned = dto.FitnessDeclarationSigned,
                    Status = status,
                    IsSyncedToCloud = true,
                    Items = items
                });
            }
            results.Add(new { dto.LocalId, Accepted = !exists });
        }
        await _db.SaveChangesAsync();
        return Ok(results);
    }

    /// <summary>GET api/sync/pull?since={utc} – fetch updates since last sync</summary>
    [HttpGet("pull")]
    public async Task<IActionResult> Pull([FromQuery] DateTime? since)
    {
        since ??= DateTime.UtcNow.AddDays(-1);
        var machines = await _db.Machines
            .Include(m => m.Template)
            .ThenInclude(t => t!.Items)
            .Where(m => m.IsActive)
            .ToListAsync();

        var defectOrders = await _db.DefectOrders
            .Where(d => d.CreatedAt >= since)
            .ToListAsync();

        return Ok(new { machines, defectOrders, pulledAt = DateTime.UtcNow });
    }
}
