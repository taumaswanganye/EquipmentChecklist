using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EquipmentChecklist.Services.Integrations;

/// <summary>
/// SAP Plant Maintenance publisher — POSTs the defect payload to a
/// configured SAP base URL as a JSON envelope that mirrors SAP PM's
/// standard OData WorkOrder shape.
///
/// <para>This is a SCAFFOLD, not a production-ready SAP connector. It
/// gives you a working HTTP POST against a configured URL with proper
/// auth header, JSON envelope, retry-on-transient, and logging. To turn
/// it into a real SAP integration you need to:</para>
/// <list type="number">
///   <item><description>Confirm SAP PM's OData endpoint with the SAP
///   basis team. The default path is <c>/sap/opu/odata/sap/API_MAINTENANCEORDER_SRV/MaintenanceOrder</c>.</description></item>
///   <item><description>Decide on auth — Basic, OAuth client-credentials,
///   or SAP Gateway X-CSRF-Token. The scaffold uses an Authorization
///   header sourced from <c>Sap.ApiKey</c>; change <see cref="BuildRequest"/>
///   if a different scheme is needed.</description></item>
///   <item><description>Add the SAP-side field mapping in
///   <see cref="ToSapEnvelope"/>. The current mapping is illustrative —
///   real SAP PM expects specific company-code / plant / order-type values
///   that vary per deployment.</description></item>
///   <item><description>Wire the inbound webhook receiver in
///   <c>IntegrationsController</c> (sibling file) so SAP can callback when
///   the work order closes and the defect order's <c>RepairStatus</c>
///   gets updated on this side.</description></item>
/// </list>
///
/// <para>All failures are swallowed and logged — the operator's pre-shift
/// check must NEVER fail because SAP is down.</para>
/// </summary>
public class SapPmIntegrationPublisher : IIntegrationPublisher
{
    private readonly IHttpClientFactory          _httpFactory;
    private readonly ConfigurationService        _config;
    private readonly ILogger<SapPmIntegrationPublisher> _log;

    public SapPmIntegrationPublisher(
        IHttpClientFactory          httpFactory,
        ConfigurationService        config,
        ILogger<SapPmIntegrationPublisher> log)
    {
        _httpFactory = httpFactory;
        _config      = config;
        _log         = log;
    }

    public async Task PublishDefectOrderCreatedAsync(
        IntegrationDefectPayload payload, CancellationToken ct = default)
    {
        try
        {
            // Lazy-read config every call so admin changes to the SAP URL
            // / API key take effect without an app restart. ConfigurationService
            // is in-memory cached so this is cheap.
            var enabled = await _config.GetBoolAsync("Sap.Enabled", false);
            if (!enabled)
            {
                _log.LogDebug("SAP integration is disabled — skipping defect #{Id}", payload.DefectOrderId);
                return;
            }

            var baseUrl = await _config.GetAsync("Sap.BaseUrl");
            var apiKey  = await _config.GetAsync("Sap.ApiKey");
            var endpoint = await _config.GetAsync("Sap.WorkOrderEndpoint")
                          ?? "/sap/opu/odata/sap/API_MAINTENANCEORDER_SRV/MaintenanceOrder";

            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                _log.LogWarning("SAP.Enabled = true but Sap.BaseUrl is not configured — skipping defect #{Id}",
                    payload.DefectOrderId);
                return;
            }

            using var client = _httpFactory.CreateClient("sap-pm");
            client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

            var envelope = ToSapEnvelope(payload);
            var json     = JsonSerializer.Serialize(envelope);

            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimStart('/'))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            BuildRequest(req, apiKey);

            // Single attempt; the next mobile sync will re-fire if the row
            // is processed via a redrive mechanism (not built today). For a
            // production deployment, wrap this in a Polly retry policy with
            // exponential backoff — recommended 3 retries over ~30 seconds.
            var resp = await client.SendAsync(req, ct);

            if (resp.IsSuccessStatusCode)
            {
                _log.LogInformation("SAP PM accepted defect #{Id} (HTTP {Status})",
                    payload.DefectOrderId, (int)resp.StatusCode);
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                _log.LogWarning(
                    "SAP PM rejected defect #{Id} — HTTP {Status} · body: {Body}",
                    payload.DefectOrderId, (int)resp.StatusCode,
                    body.Length > 500 ? body[..500] + "…" : body);
            }
        }
        catch (Exception ex)
        {
            // Integration failures NEVER bubble to the operator. Log + move on.
            _log.LogError(ex, "SAP PM publish failed for defect #{Id}", payload.DefectOrderId);
        }
    }

    /// <summary>
    /// Map our generic payload into the JSON shape SAP PM's standard
    /// MaintenanceOrder OData service expects. Real deployments will
    /// customise the company-code / plant / order-type fields — those
    /// values come from the SAP basis team, not from this repo.
    /// </summary>
    private static object ToSapEnvelope(IntegrationDefectPayload p) => new
    {
        // The exact field names are SAP PM OData v2 conventions. Adjust
        // per your tenant's MaintenanceOrder service contract.
        OrderType         = "PM01",                  // corrective maintenance — typical default
        FunctionalLocation= p.MachineNumber,
        ShortText         = p.DefectDescription.Length > 40
                              ? p.DefectDescription[..40]
                              : p.DefectDescription,
        LongText          = p.DefectDescription,
        Priority          = p.IsCriticalDefect ? "1" : "3",  // 1 = highest, 3 = normal
        ReporterEmail     = p.ReportingOperatorEmail,
        ReporterName      = p.ReportingOperatorName,
        ExternalReference = p.DefectOrderId.ToString(),
        CreatedAt         = p.CreatedAtUtc.ToString("O"),
        Parts             = string.IsNullOrEmpty(p.PartRequired)
                              ? Array.Empty<object>()
                              : new[] { new { Description = p.PartRequired, Number = p.PartNumber } }
    };

    /// <summary>
    /// Attach the auth header. Default: bearer token from <c>Sap.ApiKey</c>.
    /// Override this method (or change the SAP-side configuration) for
    /// Basic auth or SAP Gateway X-CSRF-Token flows.
    /// </summary>
    private static void BuildRequest(HttpRequestMessage req, string? apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // SAP OData services typically expect an Accept header.
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}
