using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Unified result shape for write endpoints. <see cref="Ok"/> is the happy-path
/// flag the UI usually consumes; <see cref="Status"/> lets the offline drainer
/// distinguish transient 5xx (retry) from permanent 4xx (drop). <see cref="Error"/>
/// carries the server's response body so toast UIs can surface it.
/// </summary>
public record ApiResult(bool Ok, HttpStatusCode? Status, string? Error)
{
    /// <summary>True for 5xx — the drainer should retry these later.</summary>
    public bool IsTransient =>
        Status.HasValue && (int)Status.Value >= 500 && (int)Status.Value < 600;

    /// <summary>True for 4xx — the drainer should give up; retrying won't help.</summary>
    public bool IsPermanent =>
        Status.HasValue && (int)Status.Value >= 400 && (int)Status.Value < 500;

    public static ApiResult Success() => new(true, HttpStatusCode.OK, null);
}

/// <summary>
/// Thin wrapper over the server's <c>/api/sync/*</c> endpoints.
///
/// <para>
/// Everything goes through one shared <see cref="HttpClient"/>. The base URL
/// is configured in <c>MauiProgram.cs</c>. Every authed call stamps the JWT
/// from <see cref="AuthService"/> as an <c>Authorization: Bearer …</c> header.
/// </para>
///
/// <para>Errors:</para>
/// <list type="bullet">
///   <item><description>Non-2xx responses return <c>null</c> from the helper.</description></item>
///   <item><description>Network exceptions propagate so callers can show "you're offline".</description></item>
/// </list>
/// </summary>
public class ApiClient
{
    private readonly HttpClient   _http;
    private readonly AuthService  _auth;

    public ApiClient(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    /// <summary>Base URL of the server — handy for building &lt;img&gt; src for
    /// icons / item images that live in the server's wwwroot.</summary>
    public Uri? BaseAddress => _http.BaseAddress;

    /// <summary>Site labels read on app launch and re-pulled after sign-in.
    /// Anonymous endpoint so it works pre-login.</summary>
    public async Task<MineDto?> MineConfigAsync()
    {
        try   { return await _http.GetFromJsonAsync<MineDto>("api/sync/mine"); }
        catch { return null; }
    }

    /// <summary>
    /// Lightweight liveness check used by <see cref="ApiHealth"/>. Anonymous
    /// (no token applied) and capped at a short timeout so it doesn't stall
    /// the UI when the server is down or unreachable. Returns <c>true</c> on
    /// any 2xx, <c>false</c> on anything else (including timeouts and
    /// connection refusals).
    /// </summary>
    public async Task<bool> PingAsync(TimeSpan? timeout = null)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(5));
            var resp = await _http.GetAsync("api/sync/ping", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Attaches the Bearer token (if any) to the next request.</summary>
    private async Task ApplyAuthAsync()
    {
        var token = await _auth.GetTokenAsync();
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// Convert an <see cref="HttpResponseMessage"/> into the common
    /// <see cref="ApiResult"/> shape. Reads the body on non-success so the UI
    /// (and the offline drainer logs) can surface the server's error string.
    /// </summary>
    private static async Task<ApiResult> ToResultAsync(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode)
            return new ApiResult(true, resp.StatusCode, null);

        string? body = null;
        try { body = await resp.Content.ReadAsStringAsync(); }
        catch { /* body read failures shouldn't mask the status */ }
        return new ApiResult(false, resp.StatusCode, body);
    }

    // ── Login ────────────────────────────────────────────────────────────────
    public async Task<SyncLoginResponse?> LoginAsync(string email, string password)
    {
        var resp = await _http.PostAsJsonAsync("api/sync/login",
            new SyncLoginRequest { Email = email, Password = password });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SyncLoginResponse>();
    }

    public async Task<SyncUserDto?> MeAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<SyncUserDto>("api/sync/me");
    }

    // ── Machines ─────────────────────────────────────────────────────────────
    public async Task<List<SyncMachineSummaryDto>?> MyMachinesAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<List<SyncMachineSummaryDto>>("api/sync/machines");
    }

    public async Task<SyncTemplateDto?> TemplateAsync(int machineId)
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<SyncTemplateDto>(
            $"api/sync/machines/{machineId}/template");
    }

    // ── Submissions ──────────────────────────────────────────────────────────
    public async Task<SyncSubmissionResponse?> SubmitAsync(SyncSubmissionRequest req)
    {
        await ApplyAuthAsync();
        var resp = await _http.PostAsJsonAsync("api/sync/submissions", req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SyncSubmissionResponse>();
    }

    public async Task<List<RecentSubmissionDto>?> RecentSubmissionsAsync(int take = 30)
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<List<RecentSubmissionDto>>(
            $"api/sync/submissions/recent?take={take}");
    }

    /// <summary>
    /// Full details for one submission — items, defect notes, operator
    /// signature, remarks. Reuses <see cref="SupervisorReviewDto"/> because
    /// it already has exactly the right shape.
    /// </summary>
    public async Task<SupervisorReviewDto?> GetSubmissionDetailsAsync(int submissionId)
    {
        await ApplyAuthAsync();
        try
        {
            return await _http.GetFromJsonAsync<SupervisorReviewDto>(
                $"api/sync/submissions/{submissionId}/details");
        }
        catch (HttpRequestException)
        {
            // Surfaced to UI as "offline" — caller falls back to row summary.
            return null;
        }
    }

    /// <summary>
    /// Fetch the rendered PDF for a checklist submission as bytes so the mobile
    /// app can wrap them in a blob URL and display them inside an in-app modal.
    /// Returns null on any non-2xx (caller should show a friendly error).
    /// </summary>
    public async Task<byte[]?> GetSubmissionPdfAsync(int submissionId)
    {
        await ApplyAuthAsync();
        var resp = await _http.GetAsync($"api/sync/submissions/{submissionId}/pdf");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync();
    }

    // ── Stats ────────────────────────────────────────────────────────────────
    public async Task<SyncOperatorStatsDto?> StatsAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<SyncOperatorStatsDto>("api/sync/stats");
    }

    // ── Supervisor ───────────────────────────────────────────────────────────
    public async Task<List<SupervisorQueueItemDto>?> SupervisorQueueAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<List<SupervisorQueueItemDto>>("api/sync/supervisor/queue");
    }

    public async Task<List<SupervisorOperatorDto>?> SupervisorOperatorsAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<List<SupervisorOperatorDto>>("api/sync/supervisor/operators");
    }

    public async Task<List<NoGoMachineDto>?> SupervisorNoGoAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<List<NoGoMachineDto>>("api/sync/supervisor/nogo");
    }

    public async Task<SupervisorReviewDto?> SupervisorReviewAsync(int submissionId)
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<SupervisorReviewDto>(
            $"api/sync/supervisor/queue/{submissionId}");
    }

    /// <summary>resolution: 2 = 24H · 3 = 30D. Signature is a base64 PNG data URL.</summary>
    public async Task<ApiResult> SupervisorSignOffAsync(int submissionId, int resolution, string signature)
    {
        await ApplyAuthAsync();
        var resp = await _http.PostAsJsonAsync(
            $"api/sync/supervisor/queue/{submissionId}/signoff",
            new SupervisorSignOffRequest { Resolution = resolution, Signature = signature });
        return await ToResultAsync(resp);
    }

    /// <summary>List of mechanics available for the Reject picker. Ordered by workload.</summary>
    public async Task<List<SupervisorMechanicDto>?> SupervisorMechanicsAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<List<SupervisorMechanicDto>>("api/sync/supervisor/mechanics");
    }

    /// <summary>
    /// Reject a submission: flips it to Rejected, immobilises the machine, and
    /// routes every defective item to the chosen mechanic.
    /// </summary>
    public async Task<ApiResult> SupervisorRejectAsync(int submissionId, string reason, string mechanicId)
    {
        await ApplyAuthAsync();
        var resp = await _http.PostAsJsonAsync(
            $"api/sync/supervisor/queue/{submissionId}/reject",
            new SupervisorRejectRequest { Reason = reason, MechanicId = mechanicId });
        return await ToResultAsync(resp);
    }

    // ── Mechanic ─────────────────────────────────────────────────────────────
    public async Task<MechanicQueueDto?> MechanicDefectsAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<MechanicQueueDto>("api/sync/mechanic/defects");
    }

    public async Task<MechanicDefectDto?> MechanicDefectAsync(int defectOrderId)
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<MechanicDefectDto>(
            $"api/sync/mechanic/defects/{defectOrderId}");
    }

    public async Task<ApiResult> MechanicClaimAsync(int defectOrderId)
    {
        await ApplyAuthAsync();
        var resp = await _http.PostAsync(
            $"api/sync/mechanic/defects/{defectOrderId}/claim", null);
        return await ToResultAsync(resp);
    }

    /// <summary>Records the parts requirement and flips the order into AwaitingParts.</summary>
    public async Task<ApiResult> MechanicOrderPartAsync(int defectOrderId, string partRequired, string? partNumber)
    {
        await ApplyAuthAsync();
        var resp = await _http.PostAsJsonAsync(
            $"api/sync/mechanic/defects/{defectOrderId}/order-part",
            new OrderPartRequest { PartRequired = partRequired, PartNumber = partNumber });
        return await ToResultAsync(resp);
    }

    /// <summary>
    /// Marks the defect as closed. Signature is required (base64 PNG data URL).
    /// Failures surface the server's error string so the UI can show why
    /// (e.g. "Defect already resolved").
    /// </summary>
    public async Task<ApiResult> MechanicCompleteAsync(int defectOrderId, string? notes, string signature)
    {
        await ApplyAuthAsync();
        var resp = await _http.PostAsJsonAsync(
            $"api/sync/mechanic/defects/{defectOrderId}/complete",
            new CompleteRepairRequest { Notes = notes, Signature = signature });
        return await ToResultAsync(resp);
    }

    /// <summary>
    /// Batch upload audit events buffered locally in <see cref="AuditQueue"/>.
    /// One round-trip per drain pass (up to AUDIT_BATCH events). The server
    /// rejects events whose actor doesn't match the JWT subject, so we never
    /// need to send an explicit ActorUserId — the server stamps it from the
    /// token.
    /// </summary>
    public async Task<ApiResult> PostAuditBatchAsync(System.Collections.Generic.List<AuditEventDto> events)
    {
        await ApplyAuthAsync();
        var resp = await _http.PostAsJsonAsync(
            "api/sync/audit",
            new AuditEventBatchRequest { Events = events });
        return await ToResultAsync(resp);
    }

    public async Task<MechanicStatsDto?> MechanicStatsAsync()
    {
        await ApplyAuthAsync();
        return await _http.GetFromJsonAsync<MechanicStatsDto>("api/sync/mechanic/stats");
    }

    // ── Notifications inbox ──────────────────────────────────────────────────
    public async Task<List<NotificationDto>?> NotificationsAsync(int take = 30)
    {
        await ApplyAuthAsync();
        try
        {
            return await _http.GetFromJsonAsync<List<NotificationDto>>(
                $"api/sync/notifications?take={take}");
        }
        catch (HttpRequestException) { return null; }
    }

    public async Task<int?> NotificationsUnreadCountAsync()
    {
        await ApplyAuthAsync();
        try
        {
            return await _http.GetFromJsonAsync<int>("api/sync/notifications/unread-count");
        }
        catch (HttpRequestException) { return null; }
    }

    public async Task<bool> NotificationsMarkReadAsync(int id)
    {
        await ApplyAuthAsync();
        try
        {
            var resp = await _http.PostAsync($"api/sync/notifications/{id}/read", null);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }

    public async Task<bool> NotificationsMarkAllReadAsync()
    {
        await ApplyAuthAsync();
        try
        {
            var resp = await _http.PostAsync("api/sync/notifications/read-all", null);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
    }
}
