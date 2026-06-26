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

    /// <summary>
    /// Operator's current competencies — one entry per machine type they're
    /// licensed for, with the soonest-expiring date. Mobile uses this to
    /// pre-block at machine selection so an unauthorised operator never
    /// even opens the form. Empty array = no current competencies.
    /// </summary>
    public List<CompetencySummaryDto> Competencies { get; set; } = new();
}

/// <summary>
/// One row per (machine type, expires-at) for an operator's current
/// competencies. The mobile decides "can John open the checklist for
/// machine 14?" by looking up machine 14's type and checking if there's
/// a non-expired matching row in here.
/// </summary>
public class CompetencySummaryDto
{
    /// <summary>The MachineType enum value (int).</summary>
    public int      MachineType { get; set; }
    public DateTime ExpiresAt   { get; set; }
}

public class SyncMachineSummaryDto
{
    public int     Id              { get; set; }
    public string  MachineNumber   { get; set; } = "";
    public string  MachineName     { get; set; } = "";
    /// <summary>MachineType enum value (int) — used by the mobile to
    /// match the machine against the operator's cached competencies for
    /// the pre-block gate.</summary>
    public int     Type            { get; set; }
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

/// <summary>Phase 6.7 — a machine the operator raised NO-GO on, that
/// admin has since cleared, and where the operator hasn't done a fresh
/// re-check yet. Surfaced on the mobile dashboard as an "Awaiting your
/// re-check" tile so operators who missed the push notification still
/// see what's expected of them at start of shift.</summary>
public class AwaitingRecheckDto
{
    public int       MachineId            { get; set; }
    public string    MachineNumber        { get; set; } = "";
    public string    MachineName          { get; set; } = "";
    public string    TypeDisplay          { get; set; } = "";
    public DateTime  OriginalNoGoAt       { get; set; }
    public DateTime? ClearedAt            { get; set; }
    public string?   AdminClearanceNotes  { get; set; }
    public int       HoursSinceCleared    { get; set; }
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

/// <summary>
/// Site-specific labels the mobile app reads on sign-in via
/// <c>GET /api/sync/mine</c>. Same fields as the server's MineSettings —
/// kept in <c>Shared</c> so both sides bind to the same shape without
/// duplicating it.
/// </summary>
public class MineDto
{
    public string Name           { get; set; } = "";
    public string ShortName      { get; set; } = "";
    public string Tagline        { get; set; } = "";
    public string ComplianceText { get; set; } = "";
}

/// <summary>
/// One row in the notification inbox feed. Read by the mobile bell-icon
/// dropdown via <c>GET /api/sync/notifications</c>.
/// </summary>
public class NotificationDto
{
    public int      Id                  { get; set; }
    public string   Kind                { get; set; } = "";
    public string   Title               { get; set; } = "";
    public string?  Body                { get; set; }
    public int?     RelatedSubmissionId { get; set; }
    public int?     RelatedMachineId    { get; set; }
    public DateTime CreatedAt           { get; set; }
    public DateTime? ReadAt             { get; set; }
}

// ─── Audit trail (MHSA-style append-only event log) ───────────────────────────
//
// Append-only: never updated or deleted. Every meaningful action on the
// system writes one row. Mobile-originated events buffer locally in
// AuditQueue and ship to /api/sync/audit when the device is online.
//
// Naming: action names use the pattern "&lt;subject&gt;.&lt;verb&gt;" (lower-case,
// dot-separated) so they group cleanly in filters and ad-hoc SQL queries.
// Add new constants here rather than passing magic strings — typos in
// audit names are silently undetectable for months otherwise.

/// <summary>Stable list of action names recognised by the audit pipeline.
/// Server + mobile must agree on these strings; using the constant prevents
/// drift between callers.</summary>
public static class AuditActions
{
    // Submission lifecycle
    public const string SubmissionCreated  = "submission.created";
    public const string SubmissionSignedOff = "submission.signoff";
    public const string SubmissionRejected = "submission.reject";
    public const string SubmissionViewedPdf = "submission.viewpdf";

    // Defect / repair lifecycle
    public const string DefectCreated      = "defect.created";
    public const string DefectClaimed      = "defect.claimed";
    public const string DefectPartOrdered  = "defect.part_ordered";
    public const string DefectCompleted    = "defect.completed";

    // Machine lifecycle
    public const string MachineImmobilised = "machine.immobilised";
    public const string MachineReleased    = "machine.released";

    // Auth lifecycle
    public const string UserSignedIn          = "user.signin";
    public const string UserSignedInOffline   = "user.signin_offline";
    public const string UserSignedOut         = "user.signout";
    public const string UserBiometricUnlocked = "user.biometric_unlock";

    // Privileged-user lifecycle.
    // Tracked separately from generic user creation because granting Admin
    // is high-stakes — the SHE / DMR audit trail needs to show who promoted
    // whom and when, even if a later employee record is edited or removed.
    public const string AdminCreated         = "admin.created";
    public const string AdminDeactivated     = "admin.deactivated";

    // Admin-initiated password reset. Distinct from a user-initiated reset
    // so a security review can see which credential changes were performed
    // ON BEHALF OF the user (admin override) vs BY the user themselves.
    public const string UserPasswordReset    = "user.password_reset";

    // Runtime configuration change via Admin → Settings. Captures the
    // setting key + before/after values (or "***" for secrets) so a later
    // investigation can answer "when did the SMTP host change and to what".
    public const string SettingsChanged      = "settings.changed";
    public const string SettingsReset        = "settings.reset";

    // Block / unblock by admin. When a user is deactivated, both the
    // server-side JWT validator AND the mobile-side AuthService refuse
    // further sign-ins, online or offline. Audit captures the actor +
    // target so a later "why is this account locked" investigation has
    // a clear trail.
    public const string UserDeactivated      = "user.deactivated";
    public const string UserReactivated      = "user.reactivated";

    // Operator competency (MHSA Section 22(a)). Six events covering the
    // licence lifecycle. The "blocked" / "attempted" pair distinguishes
    // server-side enforcement (rare — operator's phone had the gate but
    // they synced anyway) from mobile-side enforcement (operator opened
    // the machine on the phone and was stopped at the form). Together
    // they paint a picture of compliance behaviour over time.
    public const string CompetencyAdded                    = "competency.added";
    public const string CompetencyRevoked                  = "competency.revoked";
    public const string CompetencyRenewed                  = "competency.renewed";
    public const string CompetencyExpired                  = "competency.expired";
    public const string SubmissionBlockedNoCompetency      = "submission.blocked_no_competency";
    public const string SubmissionAttemptedNoCompetency    = "submission.attempted_no_competency";
}

/// <summary>
/// Wire shape for an audit event. Used both for server-internal logging
/// and for the mobile → server batch upload at <c>POST /api/sync/audit</c>.
///
/// <para>The split between <see cref="OccurredAtClient"/> and
/// <see cref="OccurredAtServer"/> is deliberate: mobile devices set the
/// former from their (possibly wrong) clock, the server stamps the latter
/// when it persists the row. The two together let an investigator answer
/// "what time did the operator think it was when they signed off" vs
/// "what time did the server see the action".</para>
/// </summary>
public class AuditEventDto
{
    /// <summary>Action constant from <see cref="AuditActions"/>.</summary>
    public string Action { get; set; } = "";

    /// <summary>What kind of thing the action targets — "Submission",
    /// "DefectOrder", "Machine", "User", or null for global events.</summary>
    public string? TargetType { get; set; }

    /// <summary>Primary key of the target row. Null for global events.</summary>
    public long? TargetId { get; set; }

    /// <summary>UTC timestamp on the device that captured the event.</summary>
    public DateTime OccurredAtClient { get; set; }

    /// <summary>"web", "android", or "windows" — recorded by the originating
    /// client so the admin trail shows which surface did it.</summary>
    public string DeviceKind { get; set; } = "web";

    /// <summary>Optional structured detail (JSON-serialisable). Stored as
    /// jsonb on Postgres so the admin can query into it later.</summary>
    public string? PayloadJson { get; set; }
}

/// <summary>Batch wrapper for mobile uploads — one HTTP round-trip drains
/// many queued events.</summary>
public class AuditEventBatchRequest
{
    public List<AuditEventDto> Events { get; set; } = new();
}

/// <summary>Server-projected shape for the admin browse view. Carries the
/// resolved actor name + role snapshot so an old row still makes sense
/// even after the user's role changes or they're deleted.</summary>
public class AuditEventViewDto
{
    public long      Id               { get; set; }
    public string?   ActorUserId      { get; set; }
    public string?   ActorName        { get; set; }
    public string?   ActorEmail       { get; set; }
    public string?   ActorRole        { get; set; }
    public string    Action           { get; set; } = "";
    public string?   TargetType       { get; set; }
    public long?     TargetId         { get; set; }
    public string?   PayloadJson      { get; set; }
    public DateTime  OccurredAtClient { get; set; }
    public DateTime  OccurredAtServer { get; set; }
    public string    DeviceKind       { get; set; } = "web";
    public string?   IpAddress        { get; set; }
}
