using Microsoft.Maui.Devices;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Resolves a stable hardware fingerprint for this device, used by the
/// MDM-lite allowlisting feature. The server (Program.cs OnTokenValidated +
/// SyncController.Login) refuses any request whose
/// <c>X-Device-Fingerprint</c> header doesn't match a row in the
/// <c>AllowedDevices</c> table.
///
/// <para>Platform-specific identifiers:</para>
/// <list type="bullet">
///   <item><description><b>Android</b> — <c>Settings.Secure.ANDROID_ID</c>.
///   16-char hex, stable across reinstalls of the same app-signing-key,
///   changes only on factory-reset. No permissions required.</description></item>
///   <item><description><b>Windows</b> — machine GUID read from
///   <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>. Stable
///   until OS reinstall.</description></item>
/// </list>
///
/// <para>Caching: the resolved fingerprint is stashed in SecureStorage so
/// subsequent reads are O(1) and survive the app being killed. If
/// SecureStorage is wiped (rare — Android system policy or user "clear
/// data"), the platform read is re-run and the same fingerprint comes back.</para>
///
/// <para>NOT a cryptographic secret: an attacker with root can spoof the
/// fingerprint. The control protects against "operator downloaded the APK
/// onto their personal phone" — it doesn't stop a determined attacker who
/// can read another device's fingerprint and replay it.</para>
/// </summary>
public class DeviceFingerprint
{
    private const string CACHE_KEY = "eq_device_fp";

    private string? _cached;

    /// <summary>
    /// Returns the fingerprint, resolving + caching it on first call.
    /// Synchronous reads (after the first call) are guaranteed by the
    /// in-memory <c>_cached</c> field.
    /// </summary>
    public async Task<string> GetAsync()
    {
        if (!string.IsNullOrEmpty(_cached)) return _cached!;

        // Try SecureStorage first — survives app restarts cheaply.
        try
        {
            var stored = await SecureStorage.Default.GetAsync(CACHE_KEY);
            if (!string.IsNullOrEmpty(stored))
            {
                _cached = stored;
                return _cached!;
            }
        }
        catch { /* SecureStorage may be unavailable on emulator with broken keystore */ }

        // No cached value — resolve from the platform.
        var fp = ResolvePlatformFingerprint();

        // Persist for next time. Failure is tolerable — we'll just resolve
        // again on next launch.
        try { await SecureStorage.Default.SetAsync(CACHE_KEY, fp); }
        catch { }

        _cached = fp;
        return fp;
    }

    /// <summary>
    /// Friendly metadata reported alongside the fingerprint on first
    /// registration so the admin's device list isn't just rows of hex.
    /// </summary>
    public DeviceMetadata GetMetadata() => new(
        Manufacturer: DeviceInfo.Current.Manufacturer ?? "",
        Model:        DeviceInfo.Current.Model        ?? "",
        Platform:     DeviceInfo.Current.Platform.ToString(),
        OsVersion:    DeviceInfo.Current.VersionString ?? "");

    /// <summary>
    /// Platform-specific resolver. Wrapped in try/catch — any platform
    /// failure falls back to a hash of DeviceInfo.Name + Manufacturer +
    /// Model so the app still has SOMETHING to send. The fallback is less
    /// stable (a phone rename changes it) but it's better than no
    /// allowlisting at all.
    /// </summary>
    private static string ResolvePlatformFingerprint()
    {
        try
        {
#if ANDROID
            var ctx = Android.App.Application.Context;
            var androidId = Android.Provider.Settings.Secure.GetString(
                ctx.ContentResolver,
                Android.Provider.Settings.Secure.AndroidId);
            if (!string.IsNullOrWhiteSpace(androidId))
                return androidId!;
#elif WINDOWS
            // Read the persistent machine GUID Windows generates at OS
            // install time. Survives reboots and app reinstalls; only
            // changes on OS reinstall.
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid") as string;
            if (!string.IsNullOrWhiteSpace(guid))
                return guid!;
#endif
        }
        catch { /* fall through to the manufacturer/model fallback */ }

        // ── Last-resort fallback ──
        // SHA-256 of manufacturer + model + device name. Stable enough
        // for development but a phone rename invalidates it.
        var seed = $"{DeviceInfo.Current.Manufacturer}|{DeviceInfo.Current.Model}|{DeviceInfo.Current.Name}";
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(bytes).ToLowerInvariant().Substring(0, 32);
    }
}

/// <summary>
/// Platform metadata reported alongside the fingerprint on first
/// registration. Helps an admin distinguish "Samsung A14" from "iPhone 15"
/// when reading their device list.
/// </summary>
public record DeviceMetadata(
    string Manufacturer,
    string Model,
    string Platform,
    string OsVersion);
