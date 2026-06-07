using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

/// <summary>
/// Single source of truth for "is this operator competent on this machine
/// type?" — every gate (mobile pre-check, web submit, sync submit, daily
/// expiry worker) routes through here so the rules can't drift.
///
/// <para>Competency rule: a row in <c>OperatorCompetencies</c> grants
/// permission iff it has <c>IsActive=true</c> AND <c>ExpiresAt &gt; NOW()</c>.
/// Multiple rows per (operator, machine type) are normal — they're the
/// renewal history. The freshest valid one wins.</para>
/// </summary>
public class CompetencyService
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<CompetencyService> _log;

    public CompetencyService(ApplicationDbContext db, ILogger<CompetencyService> log)
    {
        _db  = db;
        _log = log;
    }

    /// <summary>
    /// Fast yes/no — used by the submission gate. A single indexed lookup
    /// (covering index on OperatorId + MachineType + IsActive) keeps this
    /// at sub-millisecond.
    /// </summary>
    public async Task<bool> IsCompetentAsync(
        string operatorId, MachineType machineType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operatorId)) return false;

        // Admins are allowed everywhere by design — they may need to
        // test-submit a checklist on a machine type they're not formally
        // ticketed for. The mobile gate has the same exception.
        var roles = await _db.UserRoles
            .Where(r => r.UserId == operatorId)
            .Join(_db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name)
            .ToListAsync(ct);
        if (roles.Contains("Admin")) return true;

        return await _db.OperatorCompetencies
            .AnyAsync(c => c.OperatorId   == operatorId
                        && c.MachineType  == machineType
                        && c.IsActive
                        && c.ExpiresAt    >  DateTime.UtcNow, ct);
    }

    /// <summary>
    /// Every competency record (active + history) for one operator. Used
    /// by the per-operator admin detail page so the SHE officer sees the
    /// full lifecycle, not just what's current.
    /// </summary>
    public async Task<List<OperatorCompetency>> GetForOperatorAsync(
        string operatorId, CancellationToken ct = default)
    {
        return await _db.OperatorCompetencies
            .Where(c => c.OperatorId == operatorId)
            .OrderByDescending(c => c.IsActive)
            .ThenByDescending(c => c.ExpiresAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Roster view: every active operator and their current competency
    /// status by machine type. Returns a flat list the view groups.
    /// </summary>
    public async Task<List<RosterRow>> GetRosterAsync(CancellationToken ct = default)
    {
        // Active operators only — deactivated users shouldn't appear on
        // the renewal-tracking page; they're a separate problem.
        var operators = await _db.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName, u.EmployeeNumber, u.Email, u.Role })
            .ToListAsync(ct);

        var allActive = await _db.OperatorCompetencies
            .Where(c => c.IsActive)
            .Select(c => new { c.OperatorId, c.MachineType, c.ExpiresAt })
            .ToListAsync(ct);

        var byOp = allActive.GroupBy(c => c.OperatorId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var now = DateTime.UtcNow;
        return operators.Select(o =>
        {
            byOp.TryGetValue(o.Id, out var certs);
            var current  = certs?.Where(c => c.ExpiresAt > now).Select(c => c.MachineType).Distinct().ToList() ?? new();
            var expiring = certs?.Where(c => c.ExpiresAt > now && c.ExpiresAt <= now.AddDays(30))
                                  .Select(c => c.MachineType).Distinct().ToList() ?? new();
            var expired  = certs?.Where(c => c.ExpiresAt <= now)
                                  .Select(c => c.MachineType).Distinct().ToList() ?? new();
            return new RosterRow(
                OperatorId:     o.Id,
                FullName:       o.FullName,
                EmployeeNumber: o.EmployeeNumber,
                Email:          o.Email ?? "",
                Role:           o.Role.ToString(),
                Current:        current,
                ExpiringIn30:   expiring,
                Expired:        expired);
        }).ToList();
    }

    /// <summary>
    /// All active, non-revoked competencies expiring within the next N
    /// days, including already-expired ones if they haven't had the
    /// expiry notice fired yet. Used by the daily background worker.
    /// </summary>
    public async Task<List<OperatorCompetency>> GetExpiringWithinAsync(
        int daysAhead, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow.AddDays(daysAhead);
        return await _db.OperatorCompetencies
            .Include(c => c.Operator)
            .Where(c => c.IsActive && c.ExpiresAt <= cutoff)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Single row per (operator, machine type) — the FRESHEST currently-
    /// valid certificate. Used by the mobile sync /me endpoint to send
    /// the minimum payload to the device.
    /// </summary>
    public async Task<List<(MachineType MachineType, DateTime ExpiresAt)>>
        GetCurrentSummaryAsync(string operatorId, CancellationToken ct = default)
    {
        var rows = await _db.OperatorCompetencies
            .Where(c => c.OperatorId == operatorId
                     && c.IsActive
                     && c.ExpiresAt > DateTime.UtcNow)
            .Select(c => new { c.MachineType, c.ExpiresAt })
            .ToListAsync(ct);

        return rows
            .GroupBy(c => c.MachineType)
            .Select(g => (g.Key, g.Max(c => c.ExpiresAt)))
            .ToList();
    }

    /// <summary>One row per operator on the admin roster page.</summary>
    public record RosterRow(
        string         OperatorId,
        string         FullName,
        string         EmployeeNumber,
        string         Email,
        string         Role,
        List<MachineType> Current,
        List<MachineType> ExpiringIn30,
        List<MachineType> Expired);
}
