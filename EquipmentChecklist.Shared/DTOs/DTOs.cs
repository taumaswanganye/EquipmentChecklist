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

    /// <summary>
    /// Optional defect photo as a base64 string (no <c>data:</c> prefix).
    /// Mobile clients build this from camera bytes; server decodes back into
    /// <c>SubmissionItem.PhotoData</c>. Null when the operator didn't attach
    /// a photo (always null on InOrder items).
    /// </summary>
    public string? PhotoBase64   { get; set; }
    /// <summary>MIME type that pairs with <see cref="PhotoBase64"/>. Defaults
    /// to image/jpeg on Android; clients should set it explicitly when known.</summary>
    public string? PhotoMimeType { get; set; }

    /// <summary>
    /// Optional voice memo as a base64 string (no <c>data:</c> prefix).
    /// Lets operators who can't type describe the defect by voice. Server
    /// decodes back into <c>SubmissionItem.AudioData</c>.
    /// </summary>
    public string? AudioBase64   { get; set; }
    /// <summary>MIME for the audio bytes. Plugin.Maui.Audio default on
    /// Android is audio/m4a; iOS audio/m4a; Windows audio/wav.</summary>
    public string? AudioMimeType { get; set; }
}

// ─── Checklist submission (web form) ─────────────────────────────────────────
public class SubmitChecklistDto
{
    public int MachineId { get; set; }
    public Shift Shift { get; set; }
    public int? KmOrHourMeter { get; set; }
    public string? OperatorRemarks { get; set; }
    public bool FitnessDeclarationSigned { get; set; }
    /// <summary>Operator's drawn signature (base64 PNG data URL).</summary>
    public string? OperatorSignature { get; set; }
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
    /// <summary>Total items inspected on this submission.</summary>
    public int ItemCount { get; set; }
    /// <summary>How many of those items were marked Defect.</summary>
    public int DefectCount { get; set; }
    public Shift Shift { get; set; }
    public int? KmOrHourMeter { get; set; }
}

// ═════════════════════════════════════════════════════════════════════════════
//                     SYNC API · used by the MAUI mobile client
// (Designed so these can be lifted into a shared class library later without
//  touching call sites on either end. Keep them pure-data, no EF / business
//  logic.)
// ═════════════════════════════════════════════════════════════════════════════

public class SyncLoginRequest
{
    public string Email    { get; set; } = "";
    public string Password { get; set; } = "";
}

public class SyncLoginResponse
{
    public string   Token        { get; set; } = "";
    public DateTime ExpiresAt    { get; set; }
    public SyncUserDto User      { get; set; } = new();
}

public class SyncUserDto
{
    public string   Id             { get; set; } = "";
    public string   FullName       { get; set; } = "";
    public string   Email          { get; set; } = "";
    public string   EmployeeNumber { get; set; } = "";
    public string[] Roles          { get; set; } = Array.Empty<string>();
}

public class SyncMachineSummaryDto
{
    public int     Id              { get; set; }
    public string  MachineNumber   { get; set; } = "";
    public string  MachineName     { get; set; } = "";
    public string  TypeDisplay     { get; set; } = "";
    public string? Description     { get; set; }
    public bool    IsImmobilised   { get; set; }
    public string? ImmobilisedReason { get; set; }
    public bool    HasTemplate     { get; set; }
}

public class SyncTemplateDto
{
    public int    MachineId   { get; set; }
    public int    TemplateId  { get; set; }
    public string Name        { get; set; } = "";
    public List<SyncTemplateItemDto> Items { get; set; } = new();
}

public class SyncTemplateItemDto
{
    public int     Id               { get; set; }
    public string  ItemName         { get; set; } = "";
    public string? Section          { get; set; }
    public int     SortOrder        { get; set; }
    public bool    IsNoGoItem       { get; set; }
    public string? StatusLabel      { get; set; }
    public string? Action           { get; set; }
    public string? InOrderCondition { get; set; }
    public string? DefectCondition  { get; set; }
    public string? IconPath         { get; set; }
}

public class SyncSubmissionRequest
{
    /// <summary>
    /// Client-generated UUID. Server uses this to dedupe on retry so a flaky
    /// network never produces double submissions.
    /// </summary>
    public Guid    LocalId                  { get; set; }
    public int     MachineId                { get; set; }
    public Shift   Shift                    { get; set; }
    public int?    KmOrHourMeter            { get; set; }
    public string? OperatorRemarks          { get; set; }
    public bool    FitnessDeclarationSigned { get; set; }
    /// <summary>Base64 PNG data URL captured on-device.</summary>
    public string? OperatorSignature        { get; set; }
    public DateTime SubmittedAt             { get; set; }
    public List<SubmissionItemDto> Items    { get; set; } = new();
}

public class SyncSubmissionResponse
{
    public int             SubmissionId { get; set; }
    public ChecklistStatus Status       { get; set; }
    public int             DefectCount  { get; set; }
}

public class SyncOperatorStatsDto
{
    public int TotalMachines { get; set; }
    public int Operational   { get; set; }
    public int Immobilised   { get; set; }
    public int OpenDefects   { get; set; }
    public int TodaysChecks  { get; set; }
}

// ── Supervisor-only DTOs ────────────────────────────────────────────────────
public class SupervisorQueueItemDto
{
    public int             SubmissionId    { get; set; }
    public string          MachineNumber   { get; set; } = "";
    public string          MachineName     { get; set; } = "";
    public string          OperatorName    { get; set; } = "";
    public string          OperatorEmployeeNumber { get; set; } = "";
    public DateTime        SubmittedAt     { get; set; }
    public Shift           Shift           { get; set; }
    public int?            KmOrHourMeter   { get; set; }
    public int             DefectCount     { get; set; }
    public ChecklistStatus Status          { get; set; }
}

public class SupervisorOperatorDto
{
    public string    UserId           { get; set; } = "";
    public string    FullName         { get; set; } = "";
    public string    EmployeeNumber   { get; set; } = "";
    public string    Email            { get; set; } = "";
    public int       Submissions30d   { get; set; }
    public int       PendingSignOff   { get; set; }
    public int       NoGoCount30d     { get; set; }
    public DateTime? LastSubmissionAt { get; set; }
}

public class NoGoMachineDto
{
    public int     MachineId         { get; set; }
    public string  MachineNumber     { get; set; } = "";
    public string  MachineName       { get; set; } = "";
    public string  TypeDisplay       { get; set; } = "";
    public string? ImmobilisedReason { get; set; }
    public int     OpenDefects       { get; set; }
    public string? AssignedOperator  { get; set; }
    public string? AssignedMechanic  { get; set; }
}

public class SupervisorReviewDto
{
    public int             SubmissionId           { get; set; }
    public ChecklistStatus Status                 { get; set; }
    public string          MachineNumber          { get; set; } = "";
    public string          MachineName            { get; set; } = "";
    public string          MachineType            { get; set; } = "";
    public string          OperatorName           { get; set; } = "";
    public string          OperatorEmployeeNumber { get; set; } = "";
    public DateTime        SubmittedAt            { get; set; }
    public Shift           Shift                  { get; set; }
    public int?            KmOrHourMeter          { get; set; }
    public string?         OperatorRemarks        { get; set; }
    public string?         OperatorSignature      { get; set; }  // base64 data URL
    public bool            FitnessDeclarationSigned { get; set; }
    public List<SupervisorReviewItemDto> Items   { get; set; } = new();
}

public class SupervisorReviewItemDto
{
    public int     TemplateItemId { get; set; }
    public string  ItemName       { get; set; } = "";
    public string? IconPath       { get; set; }
    public bool    IsNoGoItem     { get; set; }
    public ItemStatus Status      { get; set; }
    public string? Notes          { get; set; }

    /// <summary>Base64 photo bytes (no <c>data:</c> prefix) — only set on
    /// defect items where the operator attached a photo.</summary>
    public string? PhotoBase64   { get; set; }
    public string? PhotoMimeType { get; set; }

    /// <summary>Base64 audio bytes (no <c>data:</c> prefix) — only set on
    /// defect items where the operator recorded a voice memo.</summary>
    public string? AudioBase64   { get; set; }
    public string? AudioMimeType { get; set; }
}

public class SupervisorSignOffRequest
{
    /// <summary>2 = GO-BUT-Repair-24H, 3 = GO-Till-Next-Service (30 days).</summary>
    public int    Resolution { get; set; }
    /// <summary>Base64 PNG data URL of the supervisor's drawn signature.</summary>
    public string Signature  { get; set; } = "";
}

/// <summary>
/// Picker payload for the Reject modal — one row per active mechanic.
/// </summary>
public class SupervisorMechanicDto
{
    public string Id             { get; set; } = "";
    public string FullName       { get; set; } = "";
    public string EmployeeNumber { get; set; } = "";
    public string Email          { get; set; } = "";
    /// <summary>How many open defect orders this mechanic currently owns — helps the supervisor route work fairly.</summary>
    public int    OpenJobs       { get; set; }
}

public class SupervisorRejectRequest
{
    /// <summary>Why the submission was rejected (visible to the operator + mechanic).</summary>
    public string Reason     { get; set; } = "";
    /// <summary>FK to AspNetUsers.Id of the mechanic who will own the resulting defect orders.</summary>
    public string MechanicId { get; set; } = "";
}

// ── Mechanic-only DTOs ──────────────────────────────────────────────────────
/// <summary>
/// One row in the mechanic's defect queue. Carries enough context to render
/// a card without further round-trips: machine, item, operator, and current
/// repair status.
/// </summary>
public class MechanicDefectDto
{
    public int           DefectOrderId        { get; set; }
    public int           SubmissionId         { get; set; }
    public int           SubmissionItemId     { get; set; }
    public int           MachineId            { get; set; }
    public string        MachineNumber        { get; set; } = "";
    public string        MachineName          { get; set; } = "";
    public string        TypeDisplay          { get; set; } = "";
    /// <summary>True when the parent machine is currently immobilised (NO-GO).</summary>
    public bool          MachineImmobilised   { get; set; }
    public string        ItemName             { get; set; } = "";
    public string?       IconPath             { get; set; }
    /// <summary>The original checklist item was marked critical (immediate NO-GO on defect).</summary>
    public bool          IsCriticalItem       { get; set; }
    public string        DefectDescription    { get; set; } = "";
    /// <summary>Free-text note the operator added when reporting the defect.</summary>
    public string?       OperatorNotes        { get; set; }
    public string        OperatorName         { get; set; } = "";
    public DateTime      CreatedAt            { get; set; }
    public RepairStatus  RepairStatus         { get; set; }
    public string?       PartRequired         { get; set; }
    public string?       PartNumber           { get; set; }
    public string?       AssignedMechanicId   { get; set; }
    public string?       AssignedMechanicName { get; set; }
    /// <summary>Convenience flag: this defect is assigned to the calling mechanic.</summary>
    public bool          IsAssignedToMe       { get; set; }
}

/// <summary>
/// Combined queue payload — the mechanic's own open orders plus the pool of
/// unassigned pending orders they could claim.
/// </summary>
public class MechanicQueueDto
{
    public List<MechanicDefectDto> MyOrders   { get; set; } = new();
    public List<MechanicDefectDto> Unassigned { get; set; } = new();
}

public class OrderPartRequest
{
    public string  PartRequired { get; set; } = "";
    public string? PartNumber   { get; set; }
}

public class CompleteRepairRequest
{
    public string? Notes     { get; set; }
    /// <summary>Base64 PNG data URL of the mechanic's drawn signature. Required.</summary>
    public string  Signature { get; set; } = "";
}

/// <summary>
/// Stat tile values for the mechanic dashboard. Distinct shape from the
/// operator/supervisor stats because the meaningful numbers are different
/// (open jobs vs machines).
/// </summary>
public class MechanicStatsDto
{
    public int OpenAssigned   { get; set; }
    public int AwaitingParts  { get; set; }
    public int Unassigned     { get; set; }
    public int CompletedToday { get; set; }
    public int NoGoMachines   { get; set; }
}
