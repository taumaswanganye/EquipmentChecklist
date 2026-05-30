using System.Text.Json;
using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Background worker that drains <see cref="SubmissionQueue"/> against the
/// server whenever the device has internet. Subscribes to
/// <c>Connectivity.ConnectivityChanged</c> so retries happen automatically the
/// moment a phone gets back on Wi-Fi or LTE.
///
/// One singleton instance per app. <c>MauiProgram</c> resolves it after
/// <c>builder.Build()</c> so the connectivity listener is wired up before any
/// page renders — operators don't have to open the dashboard for a drained
/// queue to start flushing.
/// </summary>
public class SyncWorker : IDisposable
{
    private readonly SubmissionQueue _queue;
    private readonly ActionQueue     _actions;
    private readonly ApiClient       _api;
    private readonly AuthService     _auth;
    private readonly SemaphoreSlim   _runLock = new(1, 1);
    private bool                     _disposed;

    /// <summary>Fires after a drain pass completes (success or otherwise) so
    /// the UI can refresh the badge.</summary>
    public event Action? DrainCompleted;

    public SyncWorker(SubmissionQueue queue, ActionQueue actions, ApiClient api, AuthService auth)
    {
        _queue   = queue;
        _actions = actions;
        _api     = api;
        _auth    = auth;

        Connectivity.ConnectivityChanged += OnConnectivityChanged;

        // Best-effort drain on startup if we're already online.
        if (Connectivity.NetworkAccess == NetworkAccess.Internet)
            _ = Task.Run(DrainAsync);
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        if (e.NetworkAccess == NetworkAccess.Internet)
            _ = Task.Run(DrainAsync);
    }

    /// <summary>
    /// Try to push every queued submission and every queued supervisor/mechanic
    /// action. Stops on the first network error or auth rejection — the next
    /// connectivity event re-triggers us.
    /// </summary>
    public async Task DrainAsync()
    {
        if (!await _runLock.WaitAsync(0)) return;  // another drain is in flight
        try
        {
            if (!_auth.IsSignedIn) return;          // no JWT yet → nothing to do

            await DrainSubmissionsAsync();
            await DrainActionsAsync();
        }
        finally
        {
            _runLock.Release();
            DrainCompleted?.Invoke();
        }
    }

    // ── Operator submissions ────────────────────────────────────────────────
    private async Task DrainSubmissionsAsync()
    {
        var items = await _queue.GetAllAsync();
        foreach (var item in items)
        {
            SyncSubmissionRequest? req = null;
            try { req = JsonSerializer.Deserialize<SyncSubmissionRequest>(item.JsonPayload); }
            catch { /* corrupted row → drop and continue */ }

            if (req == null)
            {
                await _queue.RemoveAsync(item.Id);
                continue;
            }

            try
            {
                var result = await _api.SubmitAsync(req);
                if (result != null)
                {
                    await _queue.RemoveAsync(item.Id);  // success → forget it
                }
                else
                {
                    // Non-2xx that didn't throw — usually 401 (token expired).
                    // No point hammering the server until the user signs in again.
                    await _queue.MarkRetryAsync(item.Id, "Server rejected (auth expired?)");
                    break;
                }
            }
            catch (HttpRequestException ex)
            {
                // Network blip mid-drain. Bail out — connectivity events
                // will fire us again the next time we have signal.
                await _queue.MarkRetryAsync(item.Id, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                // Other failure (serialization, 500). Bump retry, move on.
                await _queue.MarkRetryAsync(item.Id, ex.Message);
            }
        }
    }

    // ── Supervisor + Mechanic actions ───────────────────────────────────────

    /// <summary>
    /// After this many failed attempts we give up on a row and drop it — the
    /// server probably has a persistent problem we can't recover from from the
    /// device. The LastError column captures the most recent message for ops.
    /// </summary>
    private const int MAX_RETRIES = 5;

    private async Task DrainActionsAsync()
    {
        var items = await _actions.GetAllAsync();
        foreach (var item in items)
        {
            // Hard ceiling: don't loop forever on a row the server keeps refusing.
            if (item.RetryCount >= MAX_RETRIES)
            {
                await _actions.RemoveAsync(item.Id);
                continue;
            }

            try
            {
                var outcome = await ExecuteAsync(item);
                switch (outcome.Kind)
                {
                    case DrainOutcomeKind.Success:
                        await _actions.RemoveAsync(item.Id);
                        break;

                    case DrainOutcomeKind.Permanent:
                        // 4xx: the server has a definitive "no" for this row
                        // (submission already signed off, defect already claimed
                        // by someone else, etc). Drop it — retrying can't change
                        // the answer.
                        await _actions.RemoveAsync(item.Id);
                        break;

                    case DrainOutcomeKind.Transient:
                        // 5xx: server returned an error but the call shape is
                        // valid. Keep the row, bump the retry, try the next one
                        // — we don't break the whole drain on a single 500.
                        await _actions.MarkRetryAsync(item.Id, outcome.Error);
                        break;
                }
            }
            catch (HttpRequestException ex)
            {
                // Still offline mid-drain — bail and wait for the next event.
                await _actions.MarkRetryAsync(item.Id, ex.Message);
                break;
            }
            catch (Exception ex)
            {
                // Unexpected (serialization, deserialization). Treat as transient
                // so we get another shot after a fix / restart.
                await _actions.MarkRetryAsync(item.Id, ex.Message);
            }
        }
    }

    private enum DrainOutcomeKind { Success, Permanent, Transient }
    private record DrainOutcome(DrainOutcomeKind Kind, string? Error)
    {
        public static DrainOutcome FromApi(ApiResult r)
        {
            if (r.Ok)          return new(DrainOutcomeKind.Success,   null);
            if (r.IsTransient) return new(DrainOutcomeKind.Transient, r.Error);
            // Any 4xx, or status==null on the fall-through, gets treated as
            // permanent — null status means the call never reached its happy
            // path and isn't worth retrying without manual intervention.
            return new(DrainOutcomeKind.Permanent, r.Error);
        }
    }

    /// <summary>
    /// Dispatch one queued action to the matching ApiClient call. The returned
    /// <see cref="DrainOutcome"/> tells the drainer whether the row should be
    /// removed (success / permanent) or kept (transient).
    /// </summary>
    private async Task<DrainOutcome> ExecuteAsync(ActionQueue.QueuedAction item)
    {
        switch (item.ActionType)
        {
            case ActionQueue.ActionKinds.SignOff:
            {
                var p = JsonSerializer.Deserialize<SignOffPayload>(item.PayloadJson);
                if (p == null) return new(DrainOutcomeKind.Permanent, "corrupt payload");
                return DrainOutcome.FromApi(
                    await _api.SupervisorSignOffAsync(item.TargetId, p.Resolution, p.Signature));
            }

            case ActionQueue.ActionKinds.Reject:
            {
                var p = JsonSerializer.Deserialize<RejectPayload>(item.PayloadJson);
                if (p == null) return new(DrainOutcomeKind.Permanent, "corrupt payload");
                return DrainOutcome.FromApi(
                    await _api.SupervisorRejectAsync(item.TargetId, p.Reason, p.MechanicId));
            }

            case ActionQueue.ActionKinds.Claim:
                return DrainOutcome.FromApi(await _api.MechanicClaimAsync(item.TargetId));

            case ActionQueue.ActionKinds.OrderPart:
            {
                var p = JsonSerializer.Deserialize<OrderPartPayload>(item.PayloadJson);
                if (p == null) return new(DrainOutcomeKind.Permanent, "corrupt payload");
                return DrainOutcome.FromApi(
                    await _api.MechanicOrderPartAsync(item.TargetId, p.PartRequired, p.PartNumber));
            }

            case ActionQueue.ActionKinds.Complete:
            {
                var p = JsonSerializer.Deserialize<CompletePayload>(item.PayloadJson);
                if (p == null) return new(DrainOutcomeKind.Permanent, "corrupt payload");
                return DrainOutcome.FromApi(
                    await _api.MechanicCompleteAsync(item.TargetId, p.Notes, p.Signature));
            }

            default:
                // Unknown discriminator — drop the row so we don't loop forever
                // on it after a stale-app rollback.
                return new(DrainOutcomeKind.Permanent, $"unknown action: {item.ActionType}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Connectivity.ConnectivityChanged -= OnConnectivityChanged;
        _runLock.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Payload shapes (must match ActionQueue.Enqueue* anonymous objects) ─
    private class SignOffPayload   { public int    Resolution { get; set; } public string Signature { get; set; } = ""; }
    private class RejectPayload    { public string Reason     { get; set; } = ""; public string MechanicId { get; set; } = ""; }
    private class OrderPartPayload { public string PartRequired { get; set; } = ""; public string? PartNumber { get; set; } }
    private class CompletePayload  { public string? Notes      { get; set; } public string Signature { get; set; } = ""; }
}
