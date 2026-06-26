using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EquipmentChecklist.Services.Integrations;

/// <summary>
/// HTTP-based Key Control publisher — posts JSON to a configured vendor
/// URL with a Bearer auth header. Targets the common pattern shared by
/// the modern cabinets (Morse Watchmans web service, KEYper web API,
/// Traka Web — the latter has SOAP envelopes that wrap the same fields).
///
/// <para><b>This is a SCAFFOLD, not a production-ready connector.</b>
/// What you get out of the box: a working HTTP POST with proper auth,
/// configurable URL + API key, JSON envelope shape that's good enough
/// for proof-of-concept, structured logging at every step, and clean
/// rethrow semantics so the outbox worker can manage retries.</para>
///
/// <para>To turn this into a real integration:</para>
/// <list type="number">
///   <item><description><b>Pick the vendor</b> (Traka / Morse / KEYper /
///   eLockers). Each has slightly different JSON / SOAP shape.</description></item>
///   <item><description><b>Replace <see cref="ToVendorEnvelope"/></b> with
///   the vendor-specific field mapping. The current shape is illustrative
///   — real cabinets want their own field names (Morse: <c>keyBoxId</c>,
///   Traka: <c>SystemId + KeyPosition</c>, KEYper: <c>slotIndex</c>).</description></item>
///   <item><description><b>Add the inbound webhook receiver</b> in
///   <see cref="Controllers.Api.IntegrationsController"/> so the cabinet
///   can call back when an operator scans for a locked key (creates an
///   audit row + supervisor notification).</description></item>
///   <item><description><b>Add slot-mapping import</b> — a CSV upload on
///   <c>/Admin/Devices</c> that bulk-populates <c>Machine.KeyControlSlotId</c>
///   from the cabinet's slot register. Mines typically have 50–500 keys;
///   manually entering them per machine is tedious.</description></item>
/// </list>
///
/// <para><b>Failure handling:</b> on a non-2xx response or transport
/// exception, this class <i>throws</i>. The OutboxPublishWorker catches
/// + applies exponential backoff + ultimately dead-letters after the
/// configured retry budget. We do NOT swallow here because a dead-letter
/// row is what surfaces in <c>/Admin/Outbox</c> with the red badge — the
/// admin needs to see that a NO-GO didn't physically lock the key.</para>
/// </summary>
public class VendorKeyControlPublisher : IKeyControlPublisher
{
    private readonly IHttpClientFactory                 _httpFactory;
    private readonly ConfigurationService               _config;
    private readonly ILogger<VendorKeyControlPublisher> _log;

    /// <summary>Named HttpClient identifier — must match the
    /// <c>AddHttpClient("keycontrol", ...)</c> registration in Program.cs.</summary>
    public const string HttpClientName = "keycontrol";

    public VendorKeyControlPublisher(
        IHttpClientFactory httpFactory,
        ConfigurationService config,
        ILogger<VendorKeyControlPublisher> log)
    {
        _httpFactory = httpFactory;
        _config      = config;
        _log         = log;
    }

    public Task ImmobiliseAsync(KeyControlPayload payload, CancellationToken ct = default) =>
        SendAsync(action: "lock", payload, ct);

    public Task UnlockAsync(KeyControlPayload payload, CancellationToken ct = default) =>
        SendAsync(action: "unlock", payload, ct);

    /// <summary>
    /// Shared send path — lock + unlock are the same envelope with a
    /// different <c>action</c> verb. Vendor APIs typically expose two
    /// distinct endpoints (e.g. <c>/api/keys/{id}/lock</c> +
    /// <c>/api/keys/{id}/unlock</c>); to support that, change this
    /// method to derive the path from <paramref name="action"/>.
    /// </summary>
    private async Task SendAsync(string action, KeyControlPayload payload, CancellationToken ct)
    {
        // Lazy config read every call so the admin's edits to KeyControl.BaseUrl /
        // KeyControl.ApiKey take effect without an app restart. The settings layer
        // is in-memory cached so this is cheap.
        var enabled = await _config.GetBoolAsync("KeyControl.Enabled", false);
        if (!enabled)
        {
            _log.LogDebug(
                "Key Control disabled — skipping {Action} for machine {Machine}",
                action, payload.MachineNumber);
            return;
        }

        var baseUrl = await _config.GetAsync("KeyControl.BaseUrl");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // Treat "enabled but unconfigured" as a hard error — that's
            // an admin misconfiguration that should hit Outbox dead-letter
            // so somebody fixes it.
            throw new InvalidOperationException(
                "KeyControl.Enabled is true but KeyControl.BaseUrl is not configured. " +
                "Set it in Admin → Settings → Integrations.");
        }

        var apiKey   = await _config.GetAsync("KeyControl.ApiKey");
        var endpoint = action == "lock"
            ? await _config.GetAsync("KeyControl.LockEndpoint")   ?? "/api/keys/lock"
            : await _config.GetAsync("KeyControl.UnlockEndpoint") ?? "/api/keys/unlock";

        using var client = _httpFactory.CreateClient(HttpClientName);
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");

        var envelope = ToVendorEnvelope(action, payload);
        var json     = JsonSerializer.Serialize(envelope);

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimStart('/'))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        _log.LogInformation(
            "Key Control [{Action}] → {BaseUrl}{Endpoint} for slot {Slot} (machine {Machine})",
            action, baseUrl, endpoint, payload.SlotId, payload.MachineNumber);

        var resp = await client.SendAsync(req, ct);

        if (resp.IsSuccessStatusCode)
        {
            _log.LogInformation(
                "Key Control [{Action}] accepted (HTTP {Status}) for slot {Slot}",
                action, (int)resp.StatusCode, payload.SlotId);
            return;
        }

        // Non-2xx — throw so the outbox worker counts this toward the
        // retry budget. After MaxRetries this row will dead-letter and
        // surface on /Admin/Outbox with the red badge.
        var body = await resp.Content.ReadAsStringAsync(ct);
        var truncated = body.Length > 500 ? body[..500] + "…" : body;
        throw new HttpRequestException(
            $"Key Control rejected {action} for slot {payload.SlotId} — HTTP {(int)resp.StatusCode} · {truncated}");
    }

    /// <summary>
    /// Vendor-neutral envelope. Replace this entire method with the
    /// vendor-specific shape once you've picked Traka / Morse / KEYper /
    /// other. The field names below are illustrative — they're modelled
    /// loosely on the Morse Watchmans Web Service API because that's
    /// the most common in SA mining sites.
    /// </summary>
    private static object ToVendorEnvelope(string action, KeyControlPayload p) => new
    {
        action,                  // "lock" | "unlock"
        slotId       = p.SlotId,
        machineId    = p.MachineId,
        machineRef   = p.MachineNumber,
        reason       = p.Reason,
        requestedBy  = p.RequestedByUserId,
        requestedAt  = p.RequestedAtUtc.ToString("O"),
        // External reference so the cabinet's audit log can link back
        // to our DefectOrder / Machine. Most vendors expose a free-form
        // metadata field that survives round-trip.
        sourceSystem = "EquipmentChecklist",
        sourceMachineId = p.MachineId
    };
}
