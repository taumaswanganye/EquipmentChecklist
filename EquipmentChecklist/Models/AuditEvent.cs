using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EquipmentChecklist.Models;

/// <summary>
/// Append-only audit-trail row for MHSA / DMR compliance.
///
/// <para>One row per meaningful action. Never updated, never deleted —
/// querying always returns the historical truth even if related entities
/// (User, Machine, Submission) are subsequently modified or removed.</para>
///
/// <para>Two timestamps:</para>
/// <list type="bullet">
///   <item><description><see cref="OccurredAtClient"/> — the device's UTC
///   clock at the moment the action happened. Trusted on the web (server
///   stamps it directly) but treated as advisory on mobile uploads.</description></item>
///   <item><description><see cref="OccurredAtServer"/> — set by the server
///   on insert. The audit-trail authoritative timestamp.</description></item>
/// </list>
///
/// <para>The <see cref="ActorRole"/> column is denormalised on purpose —
/// it captures the role snapshot at action time. If an Operator is later
/// promoted to Supervisor, audit rows from their Operator days still show
/// "Operator" instead of confusing future investigators.</para>
/// </summary>
[Table("AuditEvents")]
public class AuditEvent
{
    public long Id { get; set; }

    /// <summary>FK to <c>AspNetUsers</c>. Nullable so anonymous system
    /// events (e.g. background job) can be recorded.</summary>
    [MaxLength(450)]
    public string? ActorUserId { get; set; }

    /// <summary>Display name at action time. Captured here so an audit
    /// row remains readable after the user is renamed or deleted.</summary>
    [MaxLength(120)]
    public string? ActorName { get; set; }

    [MaxLength(256)]
    public string? ActorEmail { get; set; }

    /// <summary>Snapshot of the actor's primary role at action time —
    /// "Admin", "Supervisor", "Mechanic", "Operator". Denormalised so a
    /// later role change doesn't rewrite history.</summary>
    [MaxLength(40)]
    public string? ActorRole { get; set; }

    /// <summary>Action constant from <c>EquipmentChecklist.DTOs.AuditActions</c>
    /// — e.g. "submission.signoff". Use the constant, not a literal.</summary>
    [MaxLength(60)]
    [Required]
    public string Action { get; set; } = "";

    /// <summary>"Submission", "DefectOrder", "Machine", "User", or null
    /// for global / cross-cutting events like sign-in.</summary>
    [MaxLength(40)]
    public string? TargetType { get; set; }

    /// <summary>Primary key of the target row. <c>long</c> rather than
    /// <c>int</c> because mobile-side IDs may grow large over time.</summary>
    public long? TargetId { get; set; }

    /// <summary>Optional structured detail. Postgres column type is jsonb
    /// so admins can query into the payload (e.g. WHERE PayloadJson->>'reason' ILIKE '%brake%').</summary>
    [Column(TypeName = "jsonb")]
    public string? PayloadJson { get; set; }

    /// <summary>UTC timestamp from the device clock. On mobile-originated
    /// rows this may differ from <see cref="OccurredAtServer"/>.</summary>
    public DateTime OccurredAtClient { get; set; }

    /// <summary>UTC timestamp stamped on persist. Authoritative.</summary>
    public DateTime OccurredAtServer { get; set; }

    /// <summary>"web", "android", "windows".</summary>
    [MaxLength(20)]
    public string DeviceKind { get; set; } = "web";

    /// <summary>Source IP captured from the request when available. Useful
    /// for unusual sign-in detection.</summary>
    [MaxLength(64)]
    public string? IpAddress { get; set; }
}
