#if ANDROID
using Android.Content;
using Android.Net;
using Android.Provider;
using EquipmentChecklist.Mobile.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using static Microsoft.Maui.ApplicationModel.Permissions;

namespace EquipmentChecklist.Mobile.Platforms.Android;

/// <summary>
/// Phase 8.4 — Android implementation of <see cref="IPeerAlertPermissions"/>.
///
/// <para>Wraps the four Android runtime permissions the peer-alert
/// feature needs:</para>
/// <list type="bullet">
///   <item><description><c>BLUETOOTH_ADVERTISE</c> (API 31+) — peripheral
///   broadcast when this device raises a NO-GO offline.</description></item>
///   <item><description><c>BLUETOOTH_SCAN</c> (API 31+) — central scanner
///   that picks up other operators' broadcasts.</description></item>
///   <item><description><c>BLUETOOTH_CONNECT</c> (API 31+) — needed to
///   access the BluetoothAdapter properties even though we don't
///   establish a GATT connection.</description></item>
///   <item><description><c>POST_NOTIFICATIONS</c> (API 33+) — the
///   foreground service notification that keeps the scanner alive in
///   the background.</description></item>
/// </list>
///
/// <para>On pre-API-31 devices the BLE permissions were declared in the
/// manifest with the old normal-grant <c>BLUETOOTH</c> permission (we
/// don't actually use it anymore but Android automatically maps it for
/// back-compat). On pre-API-33 devices <c>POST_NOTIFICATIONS</c> doesn't
/// exist — notifications work without runtime grant. We use
/// <c>OperatingSystem.IsAndroidVersionAtLeast</c> guards to only request
/// what's actually applicable on this device.</para>
/// </summary>
public class PeerAlertPermissionsAndroid : IPeerAlertPermissions
{
    /// <summary>Key in <c>Preferences</c> that records "we've shown the
    /// explainer + prompted once". Prevents re-pestering.</summary>
    private const string PrefKeyPrompted = "peer-alert-permission-prompted";

    public bool IsApplicable => OperatingSystem.IsAndroidVersionAtLeast(31);

    public bool HasPromptedBefore
    {
        get => Preferences.Default.Get(PrefKeyPrompted, false);
        private set => Preferences.Default.Set(PrefKeyPrompted, value);
    }

    public async Task<PeerAlertPermissionStatus> StatusAsync()
    {
        if (!IsApplicable) return PeerAlertPermissionStatus.Granted;

        try
        {
            var ble = await CheckStatusAsync<BluetoothScanPermission>();
            if (ble != PermissionStatus.Granted) return PeerAlertPermissionStatus.Denied;

            // BLUETOOTH_CONNECT is grouped with SCAN in the platform but
            // we check separately for completeness.
            var connect = await CheckStatusAsync<BluetoothConnectPermission>();
            if (connect != PermissionStatus.Granted) return PeerAlertPermissionStatus.Denied;

            var advertise = await CheckStatusAsync<BluetoothAdvertisePermission>();
            if (advertise != PermissionStatus.Granted) return PeerAlertPermissionStatus.Denied;

            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var notif = await CheckStatusAsync<PostNotificationsPermission>();
                if (notif != PermissionStatus.Granted) return PeerAlertPermissionStatus.Denied;
            }

            return PeerAlertPermissionStatus.Granted;
        }
        catch
        {
            // Permission API throws on edge-case devices (older custom
            // ROMs). Treat as Unknown so we don't block startup; the
            // user can still operate, just without peer-alert coverage.
            return PeerAlertPermissionStatus.Unknown;
        }
    }

    public async Task<PeerAlertPermissionStatus> PromptAsync()
    {
        // Mark prompted IMMEDIATELY (before the OS dialogs) so even if
        // the user kills the app mid-dialog we don't pester them again
        // on next launch.
        HasPromptedBefore = true;

        if (!IsApplicable) return PeerAlertPermissionStatus.Granted;

        try
        {
            // Each RequestAsync triggers its own dialog on Android. We
            // chain them serially because Android queues multiple
            // permission requests into a single grouped dialog from
            // API 31+ — the user sees them as one prompt.
            await RequestAsync<BluetoothScanPermission>();
            await RequestAsync<BluetoothConnectPermission>();
            await RequestAsync<BluetoothAdvertisePermission>();
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                await RequestAsync<PostNotificationsPermission>();
            }

            return await StatusAsync();
        }
        catch
        {
            return PeerAlertPermissionStatus.Unknown;
        }
    }

    /// <summary>
    /// Phase 8.5 — fire an intent to the OS app-details Settings page.
    /// This is the ONLY path back for an operator who tapped "Don't ask
    /// again" on a permission dialog — after the second decline Android
    /// permanently suppresses the in-app runtime prompt until they
    /// manually re-grant via Settings. The intent format is the same
    /// across Android 5+ so no API-level guards needed.
    /// </summary>
    public Task<bool> OpenAppSettingsAsync()
    {
        try
        {
            var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            // Fully-qualify Android.Net.Uri to disambiguate from System.Uri
            // which is also imported via the implicit MAUI usings.
            var uri = global::Android.Net.Uri.FromParts("package", ctx.PackageName!, null);
            var intent = new Intent(Settings.ActionApplicationDetailsSettings, uri);
            // FLAG_ACTIVITY_NEW_TASK is required because we're starting
            // an activity from a service-tier context (the app process
            // root, not the current Activity stack).
            intent.AddFlags(ActivityFlags.NewTask);
            ctx.StartActivity(intent);
            return Task.FromResult(true);
        }
        catch
        {
            // Extremely defensive — every Android device should resolve
            // ActionApplicationDetailsSettings. If it somehow doesn't
            // (custom ROM stripping settings activities) we fall back
            // to AppInfo.ShowSettingsUI in the caller.
            return Task.FromResult(false);
        }
    }

    // ── Custom permission shims for the Android 12+ runtime grants ──────
    // Microsoft.Maui.Essentials ships built-in permission types for the
    // common ones (Camera, Microphone, etc.) but the Android 12+ BLE
    // role-specific permissions need custom subclasses of
    // BasePlatformPermission. Each subclass declares the manifest
    // permission string and whether it's "runtime" (true = needs grant).

    private class BluetoothScanPermission : BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            new[] { ("android.permission.BLUETOOTH_SCAN", true) };
    }

    private class BluetoothConnectPermission : BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            new[] { ("android.permission.BLUETOOTH_CONNECT", true) };
    }

    private class BluetoothAdvertisePermission : BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            new[] { ("android.permission.BLUETOOTH_ADVERTISE", true) };
    }

    private class PostNotificationsPermission : BasePlatformPermission
    {
        public override (string androidPermission, bool isRuntime)[] RequiredPermissions =>
            new[] { ("android.permission.POST_NOTIFICATIONS", true) };
    }
}
#endif
