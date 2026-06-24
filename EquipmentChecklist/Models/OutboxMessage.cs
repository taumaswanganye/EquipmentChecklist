using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EquipmentChecklist.Models;

/// <summary>
/// Transactional outbox row. Every external integration message (e.g. a
/// defect notification destined for SAP) is written here as part of the
/// SAME database transaction that creates the underlying entity (e.g. the
/// <c>DefectOrder</c>). A background worker (<c>OutboxPublishWorker</c>)
/// then polls Draft rows and publishes them to the actual broker.
///
/// <para>Why this pattern matters:</para>
/// <list type="bullet">
///   <item><description><b>Atomicity</b> — we can never have a defect on
///   disk with no integration message, OR a message with no underlying
///   defect. EF Core's <c>SaveChangesAsync</c> wraps both inserts in one
///   transaction; either both land or neither does.</description></item>
///   <item><description><b>At-least-once delivery</b> — the worker only
///   marks a row Sent AFTER the broker acknowledges the publish. A worker
///   crash mid-publish leaves the row Draft for the next pass to pick up.
///   Downstream consumers must be idempotent (they use
///   <see cref="AggregateId"/> as the dedup key).</description></item>
///   <item><description><b>Broker-independence</b> — the same outbox feeds
///   IBM MQ, RabbitMQ, Kafka, or direct HTTP. Swap the publisher; outbox
///   stays the same.</description></item>
/// </list>
///
/// <para>State machine: Draft → Sent (success) OR Draft → DeadLetter
/// (after <c>Outbox.MaxRetries</c> failed attempts). The Admin →
/// Outbox page surfaces dead-letter rows for manual replay.</para>
/// </summary>
[Table("OutboxMessages")]
public class OutboxMessage
{
    public long Id { get; set; }

    /// <summary>The domain entity this message is about — "DefectOrder",
    /// "MachineImmobilised", etc. Useful for filtering in the admin UI
    /// and routing to the right publisher method.</summary>
    [MaxLength(40)]
    [Required]
    public string AggregateType { get; set; } = "";

    /// <summary>Primary-key string of the source row. Used by downstream
    /// consumers as the idempotency key so re-deliveries are safely
    /// ignored.</summary>
    [MaxLength(80)]
    [Required]
    public string AggregateId { get; set; } = "";

    /// <summary>The event name — "DefectOrderCreated",
    /// "DefectOrderResolved", etc. The worker switches on this to
    /// decide which strongly-typed publisher method to invoke.</summary>
    [MaxLength(80)]
    [Required]
    public string MessageType { get; set; } = "";

    /// <summary>The serialised event payload. Whatever the publisher
    /// needs — for <c>DefectOrderCreated</c> this is
    /// <c>IntegrationDefectPayload</c> as JSON. Stored as <c>text</c> in
    /// Postgres so length is unlimited; a large defect with photo URLs
    /// fits comfortably.</summary>
    [Required]
    public string PayloadJson { get; set; } = "";

    /// <summary>"Draft" (unsent), "Sent" (broker ACKed), "DeadLetter"
    /// (max retries exceeded). Stored as text for readability in raw
    /// SQL queries; the enum would have been a tighter type but every
    /// audit-style admin who looks at this table wants to read it
    /// directly.</summary>
    [MaxLength(20)]
    [Required]
    public string Status { get; set; } = OutboxStatus.Draft;

    public int AttemptCount { get; set; }

    /// <summary>Last failure reason (truncated at 2 000 chars). Cleared
    /// on the next successful publish; persists on dead-letter rows so
    /// the admin can diagnose without grep'ing logs.</summary>
    [MaxLength(2000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set when the row transitions to Sent (or DeadLetter,
    /// which is a terminal state too).</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>When the next retry should fire. Used to implement
    /// exponential backoff — failed rows aren't re-tried immediately
    /// on the next worker pass.</summary>
    public DateTime? NextAttemptAt { get; set; } = DateTime.UtcNow;
}

/// <summary>String constants for <see cref="OutboxMessage.Status"/> so
/// callers don't pass typo'd literals.</summary>
public static class OutboxStatus
{
    public const string Draft      = "Draft";
    public const string Sent       = "Sent";
    public const string DeadLetter = "DeadLetter";
}
