using Microsoft.JSInterop;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Phase 8.1 — Loud Alert mode for offline NO-GO submissions.
///
/// <para>The problem: an operator deep in a pit cut raises a NO-GO,
/// device has no cellular and no WiFi, the submission queues locally
/// for later sync — but nobody else on the mine knows yet. The supervisor
/// might be 200m away in another part of the pit.</para>
///
/// <para>The pragmatic solution (this service): siren + continuous
/// vibration + full-screen red flash for 30 seconds. The siren is loud
/// enough that anyone within ~50m audio range hears it. The supervisor
/// walks over, sees the screen, knows there's a NO-GO. Primitive but
/// reliable — zero infrastructure dependency, works in the deepest dead
/// zone, doesn't require pairing or scanning or any peer device.</para>
///
/// <para>Pairs with Phase 8.2 (BLE peer broadcast) — that's the digital
/// fallback for devices within ~30m; this is the analog "I'm waving"
/// fallback for everyone else nearby.</para>
///
/// <para>Trigger conditions: NO-GO submission AND <see cref="ApiHealth"/>
/// says we're offline. If the device has connectivity, the standard
/// notification path handles it and we don't need to scream.</para>
///
/// <para>Lifecycle: TriggerAsync starts the siren (via JS), vibration
/// (via Microsoft.Maui.Devices.Vibration), and broadcasts a
/// <c>FlashChanged</c> event so a top-level component can paint the
/// screen red. StopAsync clears all three. The siren auto-stops after
/// 30s even without an explicit Stop call so a forgotten alert doesn't
/// drain the battery overnight.</para>
/// </summary>
public class UrgentAlertService
{
    private readonly IJSRuntime _js;
    private readonly ILogger<UrgentAlertService> _log;
    private CancellationTokenSource? _vibrationCts;

    /// <summary>Default + max alert duration in seconds. 30s is long
    /// enough for someone within earshot to walk over but short enough
    /// that a dropped device on a haul-truck seat doesn't ruin the
    /// shift. Configurable per-trigger if a specific use case needs
    /// shorter.</summary>
    public const int DefaultDurationSec = 30;

    /// <summary>Whether the alert is currently firing. Read-only signal
    /// for components that want to paint a flashing red overlay.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Fires when <see cref="IsActive"/> changes. UI hosts
    /// (e.g. MainLayout) subscribe and call StateHasChanged to repaint.</summary>
    public event Action? StateChanged;

    public UrgentAlertService(IJSRuntime js, ILogger<UrgentAlertService> log)
    {
        _js  = js;
        _log = log;
    }

    /// <summary>
    /// Start the loud alert. Idempotent — calling twice in a row stops
    /// the old siren before starting the new one so there's no overlap.
    /// </summary>
    public async Task TriggerAsync(int durationSec = DefaultDurationSec)
    {
        if (durationSec <= 0 || durationSec > 120) durationSec = DefaultDurationSec;

        // Stop any previous trigger first.
        await StopInternalAsync();

        _log.LogWarning("UrgentAlertService.Trigger — siren + vibrate + flash for {Sec}s", durationSec);

        IsActive = true;
        try { StateChanged?.Invoke(); } catch { /* repaint must not block */ }

        // ── Siren (JS Web Audio loop) ────────────────────────────────────
        try
        {
            await _js.InvokeVoidAsync("MineAlerts.startUrgent", durationSec);
        }
        catch (Exception ex)
        {
            // Audio failure must NEVER block the rest of the alert chain
            // — vibration + flash still work, and the operator can still
            // wave at people manually.
            _log.LogWarning(ex, "UrgentAlertService — siren failed to start");
        }

        // ── Vibration loop (until duration or explicit Stop) ─────────────
        // Microsoft.Maui.Devices.Vibration only supports single bursts
        // up to ~5s on most platforms. We loop bursts in a fire-and-
        // forget Task with cancellation so StopAsync can interrupt early.
        _vibrationCts = new CancellationTokenSource();
        var token = _vibrationCts.Token;
        _ = Task.Run(async () =>
        {
            var ended = DateTime.UtcNow.AddSeconds(durationSec);
            while (!token.IsCancellationRequested && DateTime.UtcNow < ended)
            {
                try
                {
                    Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(800));
                }
                catch
                {
                    // Some devices don't have a motor (Surface Pro often
                    // lacks one). Silent skip — siren + flash still fire.
                }
                try { await Task.Delay(TimeSpan.FromMilliseconds(1000), token); }
                catch { break; }
            }
            // Mark the alert finished if no explicit Stop arrived.
            if (!token.IsCancellationRequested)
            {
                IsActive = false;
                try { StateChanged?.Invoke(); } catch { }
            }
        }, token);
    }

    /// <summary>
    /// Stop the alert immediately. Called when the operator (or whoever
    /// they handed the tablet to) taps the "I've notified somebody"
    /// dismiss button on the full-screen overlay.
    /// </summary>
    public async Task StopAsync()
    {
        await StopInternalAsync();
        IsActive = false;
        try { StateChanged?.Invoke(); } catch { }
    }

    private async Task StopInternalAsync()
    {
        // Cancel the vibration loop.
        try { _vibrationCts?.Cancel(); } catch { }
        try { Vibration.Default.Cancel(); } catch { }
        _vibrationCts = null;

        // Stop the JS siren.
        try
        {
            await _js.InvokeVoidAsync("MineAlerts.stopUrgent");
        }
        catch
        {
            // JS failures here are benign — the auto-stop timer inside
            // startUrgent will clean up within 30s regardless.
        }
    }
}
