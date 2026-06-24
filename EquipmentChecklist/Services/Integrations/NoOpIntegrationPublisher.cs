namespace EquipmentChecklist.Services.Integrations;

/// <summary>
/// Default <see cref="IIntegrationPublisher"/> implementation that does
/// nothing. Registered when no CMMS-specific publisher is configured, so
/// the call site stays <c>await _integration.PublishDefectOrderCreatedAsync(...)</c>
/// without null-checks. Logs at debug level so a configuration regression
/// (lost SAP settings, wrong DI binding) is observable in the log file.
/// </summary>
public class NoOpIntegrationPublisher : IIntegrationPublisher
{
    private readonly ILogger<NoOpIntegrationPublisher> _log;

    public NoOpIntegrationPublisher(ILogger<NoOpIntegrationPublisher> log) => _log = log;

    public Task PublishDefectOrderCreatedAsync(
        IntegrationDefectPayload payload, CancellationToken ct = default)
    {
        _log.LogDebug(
            "Integration publisher = NoOp. Defect order #{Id} on machine {Machine} not forwarded to any CMMS.",
            payload.DefectOrderId, payload.MachineNumber);
        return Task.CompletedTask;
    }
}
