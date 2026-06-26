#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;
using Android.Content.PM;
using AndroidX.Core.App;
using EquipmentChecklist.Mobile.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EquipmentChecklist.Mobile.Platforms.Android;

/// <summary>
/// Phase 8.3 — Android foreground service that keeps the Phase 8.2 BLE
/// peer-alert scanner alive while the app is backgrounded or the screen
/// is off.
///
/// <para><b>Why this is needed:</b> Android's battery optimisation kills
/// background services (and any BLE scanner they own) within minutes of
/// the app leaving the foreground. For a mining app where the operator's
/// tablet sits on a haul-truck dashboard with the screen off for most of
/// the shift, that means the peer-alert scanner is essentially never
/// running when it's most useful.</para>
///
/// <para><b>How a foreground service fixes it:</b> Android exempts a
/// service from battery optimisation while it has called
/// <c>startForeground()</c> with a visible persistent notification.
/// We post a low-importance "Equipment Checklist · monitoring for
/// nearby NO-GO alerts" notification at start; Android then guarantees
/// the service stays alive until we explicitly stop it. The notification
/// is intentionally muted (no sound, no vibration, no lock-screen
/// surface) so it doesn't pester the operator — it just sits in the
/// status bar as evidence that monitoring is active.</para>
///
/// <para><b>Lifecycle:</b></para>
/// <list type="bullet">
///   <item><description>Started via <see cref="StartService(Context)"/>
///   when <c>IPeerAlertService.StartListeningAsync()</c> is called.</description></item>
///   <item><description>On <see cref="OnStartCommand"/>: posts the
///   persistent notification + asks the publisher singleton to start
///   the actual BLE scanner.</description></item>
///   <item><description>Stopped via <see cref="StopService(Context)"/>
///   when the user signs out or the app explicitly quits.</description></item>
///   <item><description><see cref="StartCommandResult.Sticky"/> means
///   Android will recreate the service if it's killed under memory
///   pressure — so the operator doesn't lose peer-alert coverage
///   silently.</description></item>
/// </list>
/// </summary>
[Service(
    Exported              = false,
    ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
public class PeerAlertForegroundService : Service
{
    public const  int    NotificationId      = 1810;     // Phase 8.1.0 — unique within app
    public const  string NotificationChannel = "ecl-peer-alert-monitor";
    private const string NotificationChannelName = "Peer alert monitoring";

    private ILogger<PeerAlertForegroundService>? _log;

    public override IBinder? OnBind(Intent? intent) => null;   // not bound, started-only

    public override void OnCreate()
    {
        base.OnCreate();
        EnsureNotificationChannel();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Post the foreground notification IMMEDIATELY. Android gives us
        // 5 seconds between Service.startForeground in Java / our
        // StartForeground call here and a crash if we don't comply.
        try
        {
            var notification = BuildNotification();
            // The ForegroundService type-int overload is required on
            // Android 14+. Older platforms ignore the extra parameter.
            if (OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                StartForeground(
                    NotificationId,
                    notification,
                    ForegroundService.TypeConnectedDevice);
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            // If startForeground fails, the service dies — log loudly so
            // the operator's tablet eventually surfaces it.
            System.Diagnostics.Debug.WriteLine($"PeerAlertForegroundService.StartForeground failed: {ex}");
            return StartCommandResult.NotSticky;
        }

        // Resolve the publisher singleton from MAUI's service provider
        // and call its real StartListening. We can't call it directly
        // from here because the publisher needs to keep its instance
        // state consistent with what other code resolves.
        try
        {
            var publisher = IPlatformApplication.Current?.Services
                .GetService<IPeerAlertService>();
            if (publisher is PeerAlertPublisherAndroid android)
            {
                _log = IPlatformApplication.Current?.Services
                    .GetService<ILogger<PeerAlertForegroundService>>();
                _log?.LogInformation("PeerAlertForegroundService starting BLE scanner");
                _ = android.StartScannerInternalAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PeerAlertForegroundService resolve-publisher failed: {ex}");
        }

        // STICKY: if Android kills us, recreate the service automatically
        // (intent will be null on recreate; we re-run the same start path).
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        try
        {
            var publisher = IPlatformApplication.Current?.Services
                .GetService<IPeerAlertService>();
            if (publisher is PeerAlertPublisherAndroid android)
            {
                _ = android.StopScannerInternalAsync();
            }
        }
        catch { /* dispose path — best effort */ }

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
                StopForeground(StopForegroundFlags.Remove);
            else
                #pragma warning disable CA1422 // StopForeground(bool) deprecated in API 24 but still works
                StopForeground(true);
                #pragma warning restore CA1422
        }
        catch { }
        base.OnDestroy();
    }

    /// <summary>
    /// Build the persistent notification. Low importance, no sound, no
    /// vibration — it's pure "I'm still alive" evidence, not a real
    /// notification the operator should act on.
    /// </summary>
    private Notification BuildNotification()
    {
        var ctx = global::Android.App.Application.Context;
        var builder = new NotificationCompat.Builder(ctx, NotificationChannel)
            .SetContentTitle("Equipment Checklist")
            .SetContentText("Monitoring for nearby NO-GO alerts")
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuInfoDetails)  // generic; replace with app icon when assets land
            .SetPriority(NotificationCompat.PriorityLow)
            .SetOngoing(true)                       // user can't swipe it away
            .SetCategory(NotificationCompat.CategoryService)
            .SetShowWhen(false);
        return builder.Build()!;
    }

    /// <summary>
    /// Create the notification channel (Android 8+ required for ANY
    /// notification including foreground-service ones). Idempotent —
    /// safe to call every service start. Low importance so it doesn't
    /// make sound or pop up on the lock screen.
    /// </summary>
    private void EnsureNotificationChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

        var mgr = (NotificationManager?)GetSystemService(NotificationService);
        if (mgr == null) return;
        if (mgr.GetNotificationChannel(NotificationChannel) != null) return;

        var channel = new NotificationChannel(
            NotificationChannel,
            NotificationChannelName,
            NotificationImportance.Low)
        {
            Description = "Background scanner for peer NO-GO alerts via Bluetooth LE."
        };
        // Quiet defaults — no sound, no vibration, no lock-screen visibility.
        channel.SetSound(null, null);
        channel.EnableVibration(false);
        channel.LockscreenVisibility = NotificationVisibility.Secret;
        mgr.CreateNotificationChannel(channel);
    }

    // ── Helpers for the publisher to start/stop us ───────────────────────
    /// <summary>Start the foreground service. Called from
    /// <see cref="PeerAlertPublisherAndroid.StartListeningAsync"/> instead
    /// of starting the scanner directly.</summary>
    public static void StartService(Context ctx)
    {
        var intent = new Intent(ctx, typeof(PeerAlertForegroundService));
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
            ctx.StartForegroundService(intent);
        else
            ctx.StartService(intent);
    }

    /// <summary>Stop the foreground service. Called from
    /// <see cref="PeerAlertPublisherAndroid.StopListeningAsync"/> or on
    /// user sign-out.</summary>
    public static void StopService(Context ctx)
    {
        var intent = new Intent(ctx, typeof(PeerAlertForegroundService));
        ctx.StopService(intent);
    }
}
#endif
