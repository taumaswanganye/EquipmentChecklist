namespace EquipmentChecklist.Services.Integrations;

/// <summary>
/// Outbound publisher to a physical Key Control cabinet (Traka, Morse
/// Watchmans, KEYper, eLockers, etc.). Lets the system lock the physical
/// vehicle key when a NO-GO is raised, and unlock it when the admin
/// clears the machine back to service.
///
/// <para><b>Why a physical interlock matters:</b> the database-level
/// <c>Machine.IsImmobilised</c> flag prevents the operator from passing
/// the pre-shift check on the mobile app — but if the operator decides
/// to ignore the system and grab the physical key off the cabinet anyway,
/// nothing stops them. Locking the cabinet slot adds the missing physical
/// layer of defence and creates an independent audit trail (the cabinet
/// logs every refusal).</para>
///
/// <para><b>Why not stop a running vehicle:</b> by design this publisher
/// only fires AT KEY-OUT TIME. We do not push "kill the engine" signals
/// to a moving machine — that's an MHSA-functional-safety violation
/// without a certified FMEDA and creates unsafe states (stopping mid-haul
/// on a grade kills people). The pre-shift gate is the right place to
/// physically interlock.</para>
///
/// <para>The scaffold ships with two implementations:</para>
/// <list type="bullet">
///   <item><description><see cref="NoOpKeyControlPublisher"/> — the
///   default. Logs the event and returns. Used when no cabinet is wired
///   up; keeps the call site clean (no <c>if (keyControl != null)</c>
///   noise).</description></item>
///   <item><description><see cref="VendorKeyControlPublisher"/> —
///   POSTs JSON to a configured cabinet vendor URL. Designed to be the
///   one file Tatenda picks up + wraps once the mine has chosen a vendor
///   (Traka has a SOAP API; Morse + KEYper expose REST).</description></item>
/// </list>
///
/// <para>Failures are SWALLOWED, not thrown. The outbox worker fronts
/// this interface with its own retry loop; this publisher just signals
/// pass/fail via thrown exceptions for the worker to count toward the
/// outbox retry budget.</para>
/// </summary>
public interface IKeyControlPublisher
{
    /// <summary>
    /// Lock the cabinet slot mapped to <paramref name="payload.SlotId"/>
    /// so the next operator who scans the key cabinet is refused. Called
    /// when a NO-GO submission lands.
    /// </summary>
    Task ImmobiliseAsync(KeyControlPayload payload, CancellationToken ct = default);

    /// <summary>
    /// Unlock the cabinet slot so the next operator (or supervisor) can
    /// retrieve the key. Called when an admin clears the machine back
    /// to service via <c>/Admin/PendingClearances</c>.
    /// </summary>
    Task UnlockAsync(KeyControlPayload payload, CancellationToken ct = default);
}

/// <summary>
/// Generic Key Control payload — same shape regardless of which cabinet
/// vendor the publisher targets. Concrete implementations map this into
/// vendor-specific JSON (Traka's SOAP envelope, Morse's REST schema,
/// KEYper's HTTP webhook, etc.).
/// </summary>
/// <param name="MachineId">FK back to <c>Machines</c> for audit traceability.</param>
/// <param name="MachineNumber">Human-readable machine identifier (ADT-04, etc.).
/// Surfaced on the cabinet display so the operator knows WHY the key is locked.</param>
/// <param name="SlotId">The cabinet-specific slot identifier — opaque to us,
/// meaningful to the vendor. Comes from <c>Machine.KeyControlSlotId</c>.</param>
/// <param name="Reason">Short reason string ("NO-GO: brake test failed").
/// Some cabinets show this on the slot LCD; all of them store it in their audit log.</param>
/// <param name="RequestedByUserId">FK to the user who triggered the lock
/// (operator who raised the NO-GO, or admin who cleared it). For trace-back.</param>
/// <param name="RequestedAtUtc">When the lock/unlock request was raised in our system.
/// Lets the cabinet's audit log line up with our AuditEvent timeline.</param>
public record KeyControlPayload(
    int       MachineId,
    string    MachineNumber,
    string    SlotId,
    string    Reason,
    string?   RequestedByUserId,
    DateTime  RequestedAtUtc
);

/// <summary>
/// Constants for the outbox <c>MessageType</c> column so the worker's
/// dispatch switch + the producer sites use the same string literally.
/// Two messages — one for each direction of the interlock.
/// </summary>
public static class KeyControlMessageTypes
{
    public const string Immobilise = "keycontrol.immobilise";
    public const string Unlock     = "keycontrol.unlock";
}
