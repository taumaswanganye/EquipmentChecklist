namespace EquipmentChecklist.Services.Integrations;

/// <summary>
/// Outbound integration publisher — fires when the system creates a new
/// defect order so an external CMMS (SAP PM, Pragma On Key, Maximo, etc.)
/// can mirror it as a work order without the operator having to enter
/// the defect twice.
///
/// <para>The scaffold ships with two implementations:</para>
/// <list type="bullet">
///   <item><description><see cref="NoOpIntegrationPublisher"/> — the
///   default. Logs the event and returns. Used when no CMMS is configured;
///   keeps the call site simple (no <c>if (sap != null)</c> noise).</description></item>
///   <item><description><see cref="SapPmIntegrationPublisher"/> —
///   POSTs a JSON payload to a configured SAP base URL. Designed for SAP
///   Plant Maintenance's standard OData WorkOrder intake endpoint but the
///   shape is generic enough to point at any REST-speaking CMMS.</description></item>
/// </list>
///
/// <para>Adding a different CMMS (Pragma On Key, Maximo) means writing
/// another implementation against this interface and switching the DI
/// registration in <c>Program.cs</c>. No call-site changes needed.</para>
/// </summary>
public interface IIntegrationPublisher
{
    /// <summary>
    /// Mirror a newly-created defect order to the external CMMS. Must
    /// not throw — implementations swallow + log, because integration
    /// failures cannot block the operator's pre-shift check.
    /// </summary>
    /// <param name="payload">The defect snapshot to publish. Generic
    /// shape so swapping the CMMS doesn't change the publisher contract.</param>
    Task PublishDefectOrderCreatedAsync(
        IntegrationDefectPayload payload,
        CancellationToken ct = default);
}

/// <summary>
/// Generic outbound payload — same shape regardless of which CMMS the
/// publisher targets. Concrete implementations map this into vendor-
/// specific JSON (SAP PM's OData WorkOrder, Maximo's WO_MASTER, etc.).
/// </summary>
public record IntegrationDefectPayload(
    long      DefectOrderId,
    int       MachineId,
    string    MachineNumber,
    string?   MachineName,
    string?   MachineTypeLabel,
    string    DefectDescription,
    string?   PartRequired,
    string?   PartNumber,
    string?   ReportingOperatorName,
    string?   ReportingOperatorEmail,
    DateTime  CreatedAtUtc,
    bool      IsCriticalDefect,
    string?   ExternalReference        // back-channel for SAP's WorkOrder Id when known
);
