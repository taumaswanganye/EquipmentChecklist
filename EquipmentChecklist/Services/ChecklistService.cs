using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

public class ChecklistService
{
    private readonly ApplicationDbContext _db;

    public ChecklistService(ApplicationDbContext db) => _db = db;

    /// <summary>
    /// Processes a submitted checklist, computes the GO/NO-GO/GO-BUT status,
    /// immobilises the machine if required, and creates defect orders.
    /// </summary>
    public async Task<ChecklistSubmission> ProcessSubmissionAsync(SubmitChecklistDto dto, string operatorId)
    {
        var machine = await _db.Machines
            .Include(m => m.Template)
            .ThenInclude(t => t!.Items)
            .FirstOrDefaultAsync(m => m.Id == dto.MachineId)
            ?? throw new Exception("Machine not found");

        var submission = new ChecklistSubmission
        {
            MachineId = dto.MachineId,
            OperatorId = operatorId,
            Shift = dto.Shift,
            KmOrHourMeter = dto.KmOrHourMeter,
            OperatorRemarks = dto.OperatorRemarks,
            FitnessDeclarationSigned = dto.FitnessDeclarationSigned,
            SubmittedAt = DateTime.UtcNow
        };

        var submissionItems = dto.Items.Select(i => new SubmissionItem
        {
            TemplateItemId = i.TemplateItemId,
            Status = i.Status,
            Notes = i.Notes,
            Submission = submission
        }).ToList();

        submission.Items = submissionItems;
        submission.Status = CalculateStatus(submissionItems, machine.Template!.Items.ToList());

        // Immobilise machine on NO-GO
        if (submission.Status == ChecklistStatus.NoGo)
        {
            machine.IsImmobilised = true;
            machine.ImmobilisedReason = $"NO-GO defect on {DateTime.UtcNow:yyyy-MM-dd HH:mm} by operator {operatorId}";
        }

        _db.ChecklistSubmissions.Add(submission);
        await _db.SaveChangesAsync();

        return submission;
    }

    /// <summary>
    /// Calculates overall status based on which items have defects and
    /// whether any are flagged as NO-GO items.
    /// </summary>
    public static ChecklistStatus CalculateStatus(
        List<SubmissionItem> items,
        List<ChecklistTemplateItem> templateItems)
    {
        var defects = items.Where(i => i.Status == ItemStatus.Defect).ToList();

        if (!defects.Any()) return ChecklistStatus.Go;

        // Any defect on a NO-GO template item → immediate NO-GO
        var noGoItemIds = templateItems.Where(t => t.IsNoGoItem).Select(t => t.Id).ToHashSet();
        if (defects.Any(d => noGoItemIds.Contains(d.TemplateItemId)))
            return ChecklistStatus.NoGo;

        // All other defects → GO-BUT, requiring supervisor sign-off
        return ChecklistStatus.GoButRepair24H;
    }

    /// <summary>
    /// Supervisor approves a GO-BUT submission (status W).
    /// </summary>
    public async Task SupervisorSignOffAsync(int submissionId, string supervisorId, ChecklistStatus resolvedStatus)
    {
        var submission = await _db.ChecklistSubmissions.FindAsync(submissionId)
            ?? throw new Exception("Submission not found");

        if (submission.Status is not (ChecklistStatus.GoButRepair24H or ChecklistStatus.GoTillNextService))
            throw new Exception("Only GO-BUT submissions require supervisor sign-off");

        submission.SupervisorId = supervisorId;
        submission.SupervisorSignedAt = DateTime.UtcNow;
        submission.Status = resolvedStatus;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Mechanic marks a defect order as resolved and clears machine immobilisation if all defects resolved.
    /// </summary>
    public async Task ResolveDefectAsync(int defectOrderId, string mechanicId, string notes)
    {
        var order = await _db.DefectOrders
            .Include(d => d.Submission)
            .ThenInclude(s => s.Machine)
            .FirstOrDefaultAsync(d => d.Id == defectOrderId)
            ?? throw new Exception("Defect order not found");

        order.RepairStatus = RepairStatus.Completed;
        order.AssignedMechanicId = mechanicId;
        order.ResolvedAt = DateTime.UtcNow;
        order.ResolutionNotes = notes;

        // If all defect orders for this machine's latest NO-GO are resolved → clear immobilisation
        var machine = order.Submission.Machine;
        var hasUnresolved = await _db.DefectOrders
            .Where(d => d.Submission.MachineId == machine.Id
                && d.RepairStatus != RepairStatus.Completed)
            .AnyAsync();

        if (!hasUnresolved)
        {
            machine.IsImmobilised = false;
            machine.ImmobilisedReason = null;
        }

        await _db.SaveChangesAsync();
    }

    public async Task SaveSubmissionOfflineAsync(ChecklistSubmission submission, LocalDbContext localDb)
    {
        // 1. Generate a unique LocalId for idempotency
        submission.LocalId = Guid.NewGuid();
        submission.SubmittedAt = DateTime.UtcNow;
        submission.IsSyncedToCloud = false;

        // 2. Save the submission and its items to SQLite
        localDb.ChecklistSubmissions.Add(submission);

        // 3. Queue it for the Background SyncService
        var syncRecord = new PendingSyncRecord
        {
            LocalSubmissionId = submission.LocalId,
            QueuedAt = DateTime.UtcNow,
            RetryCount = 0
        };
        localDb.PendingSyncRecords.Add(syncRecord);

        await localDb.SaveChangesAsync();
    }

    public async Task<DashboardStatsDto> GetDashboardStatsAsync()
    {
        var today = DateTime.UtcNow.Date;

        return new DashboardStatsDto
        {
            TotalMachines = await _db.Machines.CountAsync(m => m.IsActive),
            ImmobilisedMachines = await _db.Machines.CountAsync(m => m.IsImmobilised),
            GoMachines = await _db.Machines.CountAsync(m => !m.IsImmobilised && m.IsActive),
            PendingDefects = await _db.DefectOrders.CountAsync(d => d.RepairStatus != RepairStatus.Completed),
            TodaySubmissions = await _db.ChecklistSubmissions.CountAsync(s => s.SubmittedAt >= today),
            RecentSubmissions = await _db.ChecklistSubmissions
                .Include(s => s.Machine)
                .Include(s => s.Operator)
                .OrderByDescending(s => s.SubmittedAt)
                .Take(10)
                .Select(s => new RecentSubmissionDto
                {
                    SubmissionId = s.Id,
                    MachineName = s.Machine.MachineName,
                    MachineNumber = s.Machine.MachineNumber,
                    OperatorName = s.Operator.FullName,
                    Status = s.Status,
                    SubmittedAt = s.SubmittedAt
                })
                .ToListAsync()
        };
    }
}
