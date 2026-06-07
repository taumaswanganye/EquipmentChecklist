using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.AspNetCore.Identity;

namespace EquipmentChecklist.Models;

// ─── Display helpers ─────────────────────────────────────────────────────────
public static class MachineDisplayExtensions
{
    /// <summary>
    /// Friendly display labels for the built-in MachineType enum values.
    /// </summary>
    private static readonly Dictionary<MachineType, string> EnumLabels = new()
    {
        { MachineType.ADT,                  "ADT – Articulated Dump Truck" },
        { MachineType.ArticulatedWaterTruck,"Articulated Water Truck" },
        { MachineType.DieselBowser,         "Diesel Bowser" },
        { MachineType.Drills,               "Drills" },
        { MachineType.Excavator,            "Excavator" },
        { MachineType.FEL,                  "FEL – Front End Loader" },
        { MachineType.Forklift,             "Forklift" },
        { MachineType.Grader,               "Grader" },
        { MachineType.LDV,                  "LDV / Light Vehicle" },
        { MachineType.SRVWaterBowser,       "SRV / Water Bowser" },
        { MachineType.TrackDozer,           "Track Dozer" },
        { MachineType.RDT,                  "RDT – 773 Haul Truck" },
        { MachineType.TruckMountedCrane,    "Truck Mounted Crane" },
        { MachineType.TLB,                  "TLB" },
    };

    public static IReadOnlyDictionary<MachineType, string> MachineTypeLabels => EnumLabels;

    /// <summary>
    /// Returns the user-facing type label for a machine — prefers TypeName
    /// (custom free-text) when set, otherwise the built-in enum label.
    /// </summary>
    public static string TypeDisplay(this Machine m)
    {
        if (!string.IsNullOrWhiteSpace(m.TypeName)) return m.TypeName!;
        return EnumLabels.TryGetValue(m.Type, out var label) ? label : m.Type.ToString();
    }

    /// <summary>
    /// Try to resolve a free-text type string to a known MachineType.
    /// Matches case-insensitively against both enum names and display labels.
    /// </summary>
    public static MachineType? TryResolveMachineType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var q = input.Trim();

        // Match by display label first (so "FEL – Front End Loader" works)
        foreach (var kv in EnumLabels)
            if (string.Equals(kv.Value, q, StringComparison.OrdinalIgnoreCase))
                return kv.Key;

        // Then by enum name (e.g. "FEL", "Grader")
        if (Enum.TryParse<MachineType>(q, ignoreCase: true, out var t) && Enum.IsDefined(typeof(MachineType), t))
            return t;

        return null;
    }
}

// ─── Identity User ───────────────────────────────────────────────────────────
public class ApplicationUser : IdentityUser
{
    [Required, MaxLength(100)] public string FullName { get; set; } = "";
    [Required, MaxLength(20)]  public string EmployeeNumber { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.Operator;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public ICollection<ChecklistSubmission> Submissions { get; set; } = new List<ChecklistSubmission>();

    [InverseProperty("Operator")]
    public ICollection<MachineAssignment> Assignments { get; set; } = new List<MachineAssignment>();
}

// ─── Machine ─────────────────────────────────────────────────────────────────
public class Machine
{
    public int Id { get; set; }
    [Required, MaxLength(50)]  public string MachineNumber { get; set; } = "";
    [Required, MaxLength(100)] public string MachineName { get; set; } = "";
    public MachineType Type { get; set; }
    /// <summary>
    /// Free-text type label entered by the admin. Source of truth for display.
    /// When this matches a known <see cref="MachineType"/> enum name we also set
    /// <see cref="Type"/>; otherwise <see cref="Type"/> stays at the default and
    /// this string is the only place the human-readable type lives.
    /// </summary>
    [MaxLength(100)] public string? TypeName { get; set; }
    [MaxLength(200)] public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsImmobilised { get; set; } = false;
    public string? ImmobilisedReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Admin clearance gate ────────────────────────────────────────────────
    // When a mechanic completes the LAST open defect on a machine, the machine
    // stops auto-releasing — instead it flips to AwaitingAdminClearance so an
    // admin can inspect/sign off before the machine returns to service. Until
    // an admin clicks "Clear", IsImmobilised stays true and operators can't
    // start a checklist on it.
    public bool AwaitingAdminClearance { get; set; } = false;
    [MaxLength(450)] public string? ClearedByAdminId { get; set; }
    public ApplicationUser? ClearedByAdmin { get; set; }
    public DateTime? ClearedAt { get; set; }
    [MaxLength(500)] public string? AdminClearanceNotes { get; set; }

    // Navigation
    public ICollection<MachineAssignment> Assignments { get; set; } = new List<MachineAssignment>();
    public ICollection<ChecklistSubmission> Submissions { get; set; } = new List<ChecklistSubmission>();
    public ChecklistTemplate? Template { get; set; }
}

// ─── Machine Assignment ───────────────────────────────────────────────────────
public class MachineAssignment
{
    public int Id { get; set; }
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public string OperatorId { get; set; } = "";
    public ApplicationUser Operator { get; set; } = null!;
    /// <summary>Mechanic responsible for repairs if machine is immobilised.</summary>
    public string? MechanicId { get; set; }
    public ApplicationUser? Mechanic { get; set; }
    public DateTime AssignedFrom { get; set; } = DateTime.UtcNow;
    public DateTime? AssignedTo { get; set; }
    public bool IsActive { get; set; } = true;
}

// ─── Checklist Template ───────────────────────────────────────────────────────
public class ChecklistTemplate
{
    public int Id { get; set; }
    public MachineType MachineType { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = "";
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public ICollection<ChecklistTemplateItem> Items { get; set; } = new List<ChecklistTemplateItem>();
}

public class ChecklistTemplateItem
{
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public ChecklistTemplate Template { get; set; } = null!;
    [Required, MaxLength(200)] public string ItemName { get; set; } = "";
    [MaxLength(50)]   public string? Section { get; set; }
    public int SortOrder { get; set; }
    public bool IsNoGoItem { get; set; } = false; // if defect → immediate NO-GO

    // Rich checklist item fields (populated via wizard, optional for seeded items)
    [MaxLength(100)]   public string? StatusLabel { get; set; }       // e.g. "Go till next Service"
    [MaxLength(1000)]  public string? Action { get; set; }            // what the operator must do
    [MaxLength(1000)]  public string? InOrderCondition { get; set; }  // R-condition description
    [MaxLength(1000)]  public string? DefectCondition { get; set; }   // W-condition description
    [MaxLength(500)]   public string? IconPath { get; set; }          // relative path under wwwroot
}

// ─── Checklist Submission ─────────────────────────────────────────────────────
public class ChecklistSubmission
{
    public int Id { get; set; }
    public Guid LocalId { get; set; } = Guid.NewGuid(); // for offline sync
    public int MachineId { get; set; }
    public Machine Machine { get; set; } = null!;
    public string OperatorId { get; set; } = "";
    public ApplicationUser Operator { get; set; } = null!;
    public string? SupervisorId { get; set; }
    public ApplicationUser? Supervisor { get; set; }
    public string? MechanicId { get; set; }
    public ApplicationUser? Mechanic { get; set; }

    public Shift Shift { get; set; }
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public int? KmOrHourMeter { get; set; }
    [MaxLength(500)] public string? OperatorRemarks { get; set; }

    // Fatigue / fitness declaration
    public bool FitnessDeclarationSigned { get; set; } = false;

    public ChecklistStatus Status { get; set; } = ChecklistStatus.InProgress;
    public DateTime? SupervisorSignedAt { get; set; }
    public DateTime? MechanicSignedAt { get; set; }
    [MaxLength(500)] public string? MechanicNotes { get; set; }

    // ── Digital signatures (base64 PNG data URLs) ────────────────────────────
    /// <summary>Operator's drawn signature captured at submission.</summary>
    public string? OperatorSignature   { get; set; }
    /// <summary>Supervisor's drawn signature captured at sign-off / approval.</summary>
    public string? SupervisorSignature { get; set; }

    [MaxLength(500)] public string? RejectionReason { get; set; }
    public string? RejectedMechanicId { get; set; }
    public ApplicationUser? RejectedMechanic { get; set; }

    public bool IsSyncedToCloud { get; set; } = true; // false when submitted offline

    public ICollection<SubmissionItem> Items { get; set; } = new List<SubmissionItem>();
    public ICollection<DefectOrder> DefectOrders { get; set; } = new List<DefectOrder>();
}

// ─── Submission Item ──────────────────────────────────────────────────────────
public class SubmissionItem
{
    public int Id { get; set; }
    public int SubmissionId { get; set; }
    public ChecklistSubmission Submission { get; set; } = null!;
    public int TemplateItemId { get; set; }
    public ChecklistTemplateItem TemplateItem { get; set; } = null!;
    public ItemStatus Status { get; set; } = ItemStatus.InOrder;
    [MaxLength(500)] public string? Notes { get; set; }

    // ── Defect-photo evidence (optional) ────────────────────────────────────
    /// <summary>
    /// Raw image bytes captured at submit time. Persisted inline so a single
    /// query pulls back the photo with the submission. Typical size: 0.5–3 MB
    /// per defect. Round 1 stores whatever the camera hands us; a follow-up
    /// will resize/compress before upload.
    /// </summary>
    public byte[]? PhotoData { get; set; }

    /// <summary>MIME type so the client can show it correctly without sniffing
    /// the bytes. Usually "image/jpeg" from MAUI's MediaPicker.</summary>
    [MaxLength(50)] public string? PhotoMimeType { get; set; }

    // ── Defect-voice-memo evidence (optional) ───────────────────────────────
    /// <summary>
    /// Raw audio bytes captured at submit time. Lets operators who can't
    /// (or won't) type describe the defect by voice. Typical size: 50–300 KB
    /// for a 30-second M4A/AAC at default Plugin.Maui.Audio bitrate.
    /// </summary>
    public byte[]? AudioData { get; set; }

    /// <summary>MIME type for the audio bytes. Plugin.Maui.Audio outputs
    /// audio/m4a on iOS/Android by default; audio/wav is also possible
    /// depending on recorder options.</summary>
    [MaxLength(50)] public string? AudioMimeType { get; set; }
}

// ─── Operator → Supervisor Assignment ────────────────────────────────────────
public class OperatorSupervisorAssignment
{
    public int Id { get; set; }
    public string OperatorId { get; set; } = "";
    public ApplicationUser Operator { get; set; } = null!;
    public string SupervisorId { get; set; } = "";
    public ApplicationUser Supervisor { get; set; } = null!;
    public DateTime AssignedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
}

// ─── Defect / Parts Order ─────────────────────────────────────────────────────
public class DefectOrder
{
    public int Id { get; set; }
    public int SubmissionId { get; set; }
    public ChecklistSubmission Submission { get; set; } = null!;
    public int SubmissionItemId { get; set; }
    public SubmissionItem SubmissionItem { get; set; } = null!;
    [Required, MaxLength(200)] public string DefectDescription { get; set; } = "";
    [MaxLength(200)] public string? PartRequired { get; set; }
    [MaxLength(50)]  public string? PartNumber { get; set; }
    public RepairStatus RepairStatus { get; set; } = RepairStatus.Pending;
    public string? AssignedMechanicId { get; set; }
    public ApplicationUser? AssignedMechanic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    [MaxLength(500)] public string? ResolutionNotes { get; set; }
    /// <summary>Mechanic's drawn signature captured when the defect is closed (base64 PNG data URL).</summary>
    public string? MechanicSignature { get; set; }
}

// ─── Tracks offline submissions that need to be synced to the cloud. ─────────────────────────────────────────────────────

public class PendingSyncRecord
{
    public int Id { get; set; }
    public Guid LocalSubmissionId { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; } = 0;
    public string? LastError { get; set; }
}

// ─── Per-device WebAuthn credential (biometric / passwordless login) ─────────
public class UserCredential
{
    public int Id { get; set; }
    /// <summary>FK to AspNetUsers.Id (the operator this credential belongs to).</summary>
    [Required, MaxLength(450)] public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;

    /// <summary>FIDO2 CredentialId returned by the authenticator at registration time.</summary>
    [Required] public byte[] CredentialId { get; set; } = Array.Empty<byte>();
    /// <summary>COSE-encoded public key for verifying assertions.</summary>
    [Required] public byte[] PublicKey { get; set; } = Array.Empty<byte>();
    /// <summary>Sign-counter to detect cloned authenticators (per spec).</summary>
    public uint SignCount { get; set; }

    /// <summary>Friendly label set at enrollment ("John's iPhone").</summary>
    [MaxLength(80)] public string? DeviceLabel { get; set; }
    /// <summary>AAGUID of the authenticator (helps identify model).</summary>
    public Guid AaGuid { get; set; }

    /// <summary>PBKDF2 hash of the user's offline-fallback PIN (4–6 digits).</summary>
    public string? PinHash { get; set; }

    public DateTime CreatedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

// ─── In-app notification (cross-role notifications) ────────────────────────
/// <summary>
/// Append-only feed of notifications addressed to a specific user. Written by
/// <c>NotificationService</c> from the business-logic layer whenever a state
/// change happens that someone outside the actor should know about
/// (operator submits NO-GO → supervisor; supervisor signs off → operator;
/// mechanic completes repair → operator; etc.).
///
/// <para>Read paths:</para>
/// <list type="bullet">
///   <item><description>The mobile bell icon polls <c>/api/sync/notifications/unread-count</c> for a badge.</description></item>
///   <item><description>Connected SignalR clients receive a live "notification" event the moment the row is written.</description></item>
///   <item><description>The notification dropdown lists the most recent N rows via <c>/api/sync/notifications</c>.</description></item>
/// </list>
/// </summary>
public class Notification
{
    public int Id { get; set; }

    /// <summary>FK to <see cref="ApplicationUser"/> — the recipient.</summary>
    [Required, MaxLength(450)] public string UserId { get; set; } = "";
    public ApplicationUser User { get; set; } = null!;

    /// <summary>Discriminator constant from <see cref="NotificationKinds"/>.
    /// Stored as a string rather than an enum so old payloads stay readable
    /// after we add new kinds.</summary>
    [Required, MaxLength(60)] public string Kind { get; set; } = "";

    /// <summary>Short human-readable headline shown in the dropdown.</summary>
    [Required, MaxLength(160)] public string Title { get; set; } = "";

    /// <summary>Optional second line of detail.</summary>
    [MaxLength(500)] public string? Body { get; set; }

    /// <summary>Optional JSON payload — anything extra the UI may want.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>Optional FKs for deep-linking from the dropdown into the
    /// related submission / machine view.</summary>
    public int? RelatedSubmissionId { get; set; }
    public int? RelatedMachineId    { get; set; }

    public DateTime  CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadAt    { get; set; }
}

/// <summary>
/// String constants for <see cref="Notification.Kind"/>. New kinds get added
/// to the bottom of this list; old ones never get removed so historical
/// notifications keep rendering correctly.
/// </summary>
public static class NotificationKinds
{
    /// <summary>Operator submitted a NO-GO; recipient is the team's supervisor.</summary>
    public const string SubmissionNoGo     = "submission.nogo";
    /// <summary>Operator submitted a GO-BUT awaiting supervisor approval.</summary>
    public const string SubmissionGoBut    = "submission.gobut";
    /// <summary>Supervisor approved a GO-BUT; recipient is the operator.</summary>
    public const string SubmissionApproved = "submission.approved";
    /// <summary>Supervisor rejected; recipient is the operator.</summary>
    public const string SubmissionRejected = "submission.rejected";
    /// <summary>Mechanic completed a defect repair; recipient is the operator.</summary>
    public const string DefectResolved     = "defect.resolved";

    /// <summary>
    /// A queued action drained from a mobile client lost the race with
    /// somebody else's earlier action — e.g. two supervisors approve the
    /// same submission offline, the second one's drain finds it already
    /// signed and is told "another supervisor signed first".
    ///
    /// <para>Recipient is the LOSING actor (the one whose queued action
    /// got rejected) so they know their offline work didn't take effect.
    /// Before this kind existed, the queued row was silently dropped by
    /// the 4xx polish in the SyncWorker drainer and the user had no way
    /// to learn their decision was overridden.</para>
    /// </summary>
    public const string ConflictRejected   = "conflict.rejected";

    /// <summary>
    /// A mechanic completed the last open defect on an immobilised
    /// machine. The machine is held in <c>AwaitingAdminClearance</c> until
    /// an admin signs off — this notification fires to every admin so
    /// someone picks it up promptly.
    /// </summary>
    public const string MachineAwaitingClearance = "machine.awaiting_clearance";

    /// <summary>
    /// Admin cleared a machine back into service. Recipient is the
    /// mechanic who completed the last defect, so they know their work
    /// has been signed off.
    /// </summary>
    public const string MachineCleared           = "machine.cleared";
}

// ─── Application setting (DB-backed config) ──────────────────────────────────
/// <summary>
/// One row per key/value setting that used to live in <c>appsettings.json</c>.
/// Moves operational config (mine name, manager email, SMTP host) into the
/// database so an admin can change them at runtime without redeploying,
/// while audit-logging every change.
///
/// <para>What stays in <c>appsettings.json</c>:</para>
/// <list type="bullet">
///   <item><description><b>ConnectionStrings</b> — bootstrap chicken-and-egg.</description></item>
///   <item><description><b>Jwt:Key</b> — cryptographic secret. DB compromise
///   would also be JWT-key compromise; keep it on the host instead.</description></item>
///   <item><description><b>Email:Password</b> — SMTP secret. Same reasoning.
///   Stored masked in DB if set there too, but appsettings wins.</description></item>
/// </list>
/// </summary>
public class AppSetting
{
    public int Id { get; set; }

    /// <summary>Dotted key, e.g. <c>Mine.Name</c>, <c>Email.SmtpHost</c>.
    /// The category prefix doubles as a sidebar grouping hint.</summary>
    [Required, MaxLength(120)] public string Key { get; set; } = "";

    [Required, MaxLength(60)]  public string Category { get; set; } = "General";

    /// <summary>Current value. Stored as text; consumers parse to int / bool
    /// as needed. Null means "not set" — caller falls back to the
    /// IConfiguration value (the original appsettings.json).</summary>
    public string? Value { get; set; }

    /// <summary>Default value used when the row is first seeded — also shown
    /// as a "reset to default" hint in the admin UI.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Human-readable explanation rendered on the edit form.</summary>
    [MaxLength(500)] public string? Description { get; set; }

    /// <summary>True for credentials / API keys / anything that should be
    /// password-masked in the admin UI and excluded from audit payloads.</summary>
    public bool IsSecret { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(450)] public string? UpdatedById { get; set; }
    public ApplicationUser? UpdatedBy { get; set; }
}

// ─── Operator competency (MHSA Section 22(a)) ────────────────────────────────
/// <summary>
/// One row per (operator, machine type, certificate issuance).
///
/// <para>Status of an operator on a machine type is derived: the operator
/// is competent on type T iff there's at least one row for (OperatorId=op,
/// MachineType=T) with <c>IsActive=true</c> AND <c>ExpiresAt &gt; NOW()</c>.</para>
///
/// <para>Append-only: old rows are kept (revoked, expired, replaced) so an
/// MHSA inspector can answer "was this operator competent on this machine
/// on date X?" by looking at historical rows. A renewal creates a NEW row
/// pointing at the renewed certificate, never overwrites the old one.</para>
///
/// <para>Scan PDF is stored inline as bytea following the same pattern as
/// the defect-photo / audio columns — single-query retrieval, no separate
/// blob store to manage. Typical certificate scan is 100–800 KB.</para>
/// </summary>
public class OperatorCompetency
{
    public int Id { get; set; }

    [Required, MaxLength(450)] public string OperatorId { get; set; } = "";
    public ApplicationUser Operator { get; set; } = null!;

    public MachineType MachineType { get; set; }

    /// <summary>Certificate identifier as printed on the licence.</summary>
    [MaxLength(80)] public string? CertificateNumber { get; set; }

    /// <summary>Issuing authority — TETA, MQA, internal training school, etc.</summary>
    [MaxLength(120)] public string? IssuedBy { get; set; }

    public DateTime IssuedAt  { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Scanned certificate. Same storage approach as defect-photo / audio
    /// — bytea column, served back by a controller action with the right
    /// Content-Type so the admin can view the original document.
    /// </summary>
    public byte[]? ScanData { get; set; }
    [MaxLength(50)]  public string? ScanContentType { get; set; }
    [MaxLength(255)] public string? ScanFileName    { get; set; }

    /// <summary>
    /// True when this is the active record. Admin can revoke (sets to false)
    /// without deleting so the history stays intact. Revoked rows never
    /// satisfy the competency check even if their expiry is still in the
    /// future.
    /// </summary>
    public bool IsActive { get; set; } = true;

    [MaxLength(500)] public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Admin who entered the certificate. Denormalised display
    /// name stored on the AuditEvent — the FK here is for filtering.</summary>
    [MaxLength(450)] public string? AddedByAdminId { get; set; }
    public ApplicationUser? AddedByAdmin { get; set; }

    /// <summary>Admin who revoked the certificate, if revoked.</summary>
    [MaxLength(450)] public string? RevokedByAdminId { get; set; }
    public ApplicationUser? RevokedByAdmin { get; set; }
    public DateTime? RevokedAt { get; set; }
    [MaxLength(300)] public string? RevocationReason { get; set; }

    // ── Reminder debouncing ──────────────────────────────────────────────
    // Set by the daily CompetencyExpiryWorker the first time each band's
    // email is sent. Re-running the worker the same day, week, or month
    // doesn't re-send because the columns are non-null.
    public DateTime? Reminder30DaysSentAt { get; set; }
    public DateTime? Reminder7DaysSentAt  { get; set; }
    public DateTime? ExpiryNoticeSentAt   { get; set; }
}

// ─── Device allowlisting (MDM-lite) ──────────────────────────────────────────
/// <summary>
/// One row per phone / tablet that's authorised to run the mobile app.
///
/// <para>Enforcement model:</para>
/// <list type="bullet">
///   <item><description>On first launch, the mobile app reads a stable
///   hardware identifier (Android: Settings.Secure.ANDROID_ID; Windows:
///   machine GUID) and POSTs it to <c>/api/sync/device/check</c>.</description></item>
///   <item><description>If no <see cref="AllowedDevice"/> row matches AND
///   <see cref="IsActive"/> is true, the mobile app shows its own
///   fingerprint to the operator and blocks them from signing in. The
///   operator phones the admin and reads out the fingerprint; the admin
///   adds it via Admin → Devices.</description></item>
///   <item><description>The login endpoint AND every authenticated call
///   re-validate the device against this table, so a deactivation takes
///   effect within seconds.</description></item>
///   <item><description>Optional <see cref="AssignedUserId"/> ties a device
///   to one specific user — if set, ONLY that user can sign in on that
///   device. Useful for personal-issue phones.</description></item>
/// </list>
///
/// <para>The fingerprint is not a secret in the cryptographic sense — an
/// attacker on the device can forge it, and an attacker who steals the
/// physical phone has both the fingerprint AND the user's offline-sign-in
/// hash. The control protects against the "operator downloaded the APK
/// onto their personal phone" scenario, not against a targeted device
/// attack.</para>
/// </summary>
public class AllowedDevice
{
    public int Id { get; set; }

    /// <summary>Stable hardware identifier reported by the mobile app.
    /// Android: <c>Settings.Secure.ANDROID_ID</c>. Windows: machine GUID
    /// from the registry. ~16–32 chars hex on either platform.</summary>
    [Required, MaxLength(128)] public string DeviceFingerprint { get; set; } = "";

    /// <summary>Admin-friendly name so the device list is navigable —
    /// "Site Foreman's iPhone", "Spare 1 — Workshop", etc.</summary>
    [MaxLength(100)] public string? Label { get; set; }

    /// <summary>Reported by the mobile app on registration.</summary>
    [MaxLength(80)] public string? Manufacturer { get; set; }
    [MaxLength(80)] public string? Model        { get; set; }
    [MaxLength(40)] public string? Platform     { get; set; }   // "Android" / "Windows"
    [MaxLength(40)] public string? OsVersion    { get; set; }

    /// <summary>Optional pin to a specific user. When set, only that user
    /// can sign in on this device — useful for personal-issue phones. Null
    /// = any allowed user can use the device.</summary>
    [MaxLength(450)] public string? AssignedUserId { get; set; }
    public ApplicationUser? AssignedUser { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Admin who approved the registration. Captured for audit.</summary>
    [MaxLength(450)] public string? ApprovedByAdminId { get; set; }
    public ApplicationUser? ApprovedByAdmin { get; set; }

    public DateTime CreatedAt   { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? DeactivatedAt { get; set; }

    /// <summary>Free-text admin notes ("replaced screen 2025-04-01").</summary>
    [MaxLength(500)] public string? Notes { get; set; }
}

// ─── Reusable icon library (managed by Admin, used by checklist items) ────────
public class IconLibraryItem
{
    public int Id { get; set; }
    /// <summary>Human-readable display name (defaults to original file name without extension).</summary>
    [Required, MaxLength(120)] public string Name { get; set; } = "";
    /// <summary>Original filename at upload time.</summary>
    [Required, MaxLength(255)] public string OriginalFileName { get; set; } = "";
    /// <summary>Web-relative path under wwwroot, e.g. /icon-library/abc123.png.</summary>
    [Required, MaxLength(255)] public string FilePath { get; set; } = "";
    [MaxLength(80)]  public string? ContentType { get; set; }
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(450)] public string? UploadedById { get; set; }
}
