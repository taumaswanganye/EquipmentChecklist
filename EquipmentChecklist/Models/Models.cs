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
