using EquipmentChecklist.Models;

namespace EquipmentChecklist.DTOs;

// ─── Sync ─────────────────────────────────────────────────────────────────────
public class SyncPayloadDto
{
    public List<SubmissionSyncDto> Submissions { get; set; } = new();
}

public class SubmissionSyncDto
{
    public Guid LocalId { get; set; }
    public int MachineId { get; set; }
    public string OperatorId { get; set; } = "";
    public Shift Shift { get; set; }
    public DateTime SubmittedAt { get; set; }
    public int? KmOrHourMeter { get; set; }
    public string? OperatorRemarks { get; set; }
    public bool FitnessDeclarationSigned { get; set; }
    public List<SubmissionItemDto> Items { get; set; } = new();
}

public class SubmissionItemDto
{
    public int TemplateItemId { get; set; }
    public ItemStatus Status { get; set; }
    public string? Notes { get; set; }
}

// ─── Checklist submission (web form) ─────────────────────────────────────────
public class SubmitChecklistDto
{
    public int MachineId { get; set; }
    public Shift Shift { get; set; }
    public int? KmOrHourMeter { get; set; }
    public string? OperatorRemarks { get; set; }
    public bool FitnessDeclarationSigned { get; set; }
    public List<SubmissionItemDto> Items { get; set; } = new();
}

// ─── Defect order ────────────────────────────────────────────────────────────
public class CreateDefectOrderDto
{
    public int SubmissionItemId { get; set; }
    public int SubmissionId { get; set; }
    public string DefectDescription { get; set; } = "";
    public string? PartRequired { get; set; }
    public string? PartNumber { get; set; }
}

// ─── Dashboard stats ─────────────────────────────────────────────────────────
public class DashboardStatsDto
{
    public int TotalMachines { get; set; }
    public int ImmobilisedMachines { get; set; }
    public int GoMachines { get; set; }
    public int GoButMachines { get; set; }
    public int PendingDefects { get; set; }
    public int TodaySubmissions { get; set; }
    public List<RecentSubmissionDto> RecentSubmissions { get; set; } = new();
}

public class RecentSubmissionDto
{
    public int SubmissionId { get; set; }
    public string MachineName { get; set; } = "";
    public string MachineNumber { get; set; } = "";
    public string OperatorName { get; set; } = "";
    public ChecklistStatus Status { get; set; }
    public DateTime SubmittedAt { get; set; }
}
