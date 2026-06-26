namespace EquipmentChecklist.Services.Integrations;

/// <summary>
/// Default <see cref="IKeyControlPublisher"/> — does nothing on the wire.
///
/// <para>Used when <c>KeyControl.Enabled</c> setting is false (the default
/// on fresh installs) so the rest of the system can still emit lock /
/// unlock messages to the outbox without those messages dead-lettering.
/// Treating "no cabinet wired up yet" as a valid state means the
/// submission code path is identical whether or not the integration is
/// live — the only difference is whether the publisher actually does
/// anything.</para>
///
/// <para>Logs at Debug so a verbose log scan reveals what WOULD have
/// fired if Key Control were enabled — useful when commissioning the
/// real cabinet, the operator can see the message flow before flipping
/// the switch.</para>
/// </summary>
public class NoOpKeyControlPublisher : IKeyControlPublisher
{
    private readonly ILogger<NoOpKeyControlPublisher> _log;

    public NoOpKeyControlPublisher(ILogger<NoOpKeyControlPublisher> log)
    {
        _log = log;
    }

    public Task ImmobiliseAsync(KeyControlPayload payload, CancellationToken ct = default)
    {
        _log.LogDebug(
            "Key Control [NoOp] would IMMOBILISE slot {SlotId} for machine {Machine} (reason: {Reason})",
            payload.SlotId, payload.MachineNumber, payload.Reason);
        return Task.CompletedTask;
    }

    public Task UnlockAsync(KeyControlPayload payload, CancellationToken ct = default)
    {
        _log.LogDebug(
            "Key Control [NoOp] would UNLOCK slot {SlotId} for machine {Machine}",
            payload.SlotId, payload.MachineNumber);
        return Task.CompletedTask;
    }
}
