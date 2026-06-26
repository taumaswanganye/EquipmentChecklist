namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Phase 8.4 — Runtime permission orchestration for the BLE peer-alert
/// feature. Android 12+ split the old <c>BLUETOOTH</c> permission into
/// three runtime-grant ones (<c>BLUETOOTH_ADVERTISE</c>, <c>SCAN</c>,
/// <c>CONNECT</c>) and Android 13+ added <c>POST_NOTIFICATIONS</c> for
/// any notification including the Phase 8.3 foreground service one.
///
/// <para>iOS and Windows don't have this permission model (iOS has the
/// static Info.plist usage strings; Windows handles BLE at app-capability
/// level). The <see cref="NoOpPeerAlertPermissions"/> implementation
/// covers those platforms with a "we're fine, nothing to ask" response.</para>
///
/// <para>UX flow on Android:</para>
/// <list type="number">
///   <item><description>App launches; <see cref="StatusAsync"/> is called
///   from <c>MainLayout.OnInitializedAsync</c>.</description></item>
///   <item><description>If any required permission is <c>Denied</c> AND we
///   haven't prompted before, an explainer modal renders ("We need
///   Bluetooth + Notifications so your tablet receives NO-GO alerts from
///   other operators nearby"). The modal has one button: "Continue".</description></item>
///   <item><description>Tapping Continue calls <see cref="PromptAsync"/>
///   which fires the Android runtime permission dialog(s).</description></item>
///   <item><description><see cref="HasPromptedBefore"/> flips true after
///   the prompt completes — regardless of outcome — so we never re-pester
///   the operator if they tapped Deny. They can re-grant from
///   <c>Settings → Apps → Equipment Checklist → Permissions</c>.</description></item>
/// </list>
/// </summary>
public interface IPeerAlertPermissions
{
    /// <summary>Whether this platform actually needs the permission flow.
    /// False on iOS / Windows / pre-Android-12 — the modal stays hidden.</summary>
    bool IsApplicable { get; }

    /// <summary>True after the explainer + prompt has run at least once
    /// (success OR denial). Persisted in <c>Preferences</c> so it survives
    /// app restarts. We use this to avoid re-pestering an operator who
    /// has already made their choice; the OS settings page is the right
    /// place for them to change their mind.</summary>
    bool HasPromptedBefore { get; }

    /// <summary>Current grant status. Returns <c>Granted</c> when every
    /// required permission is in place; <c>Denied</c> when any is
    /// missing; <c>Unknown</c> on platforms where it doesn't apply.</summary>
    Task<PeerAlertPermissionStatus> StatusAsync();

    /// <summary>Trigger the OS permission dialog(s). Returns the final
    /// status after the operator's response. Sets <see cref="HasPromptedBefore"/>
    /// true regardless of outcome.</summary>
    Task<PeerAlertPermissionStatus> PromptAsync();

    /// <summary>Phase 8.5 — Open the OS app-details Settings page so the
    /// operator can re-grant a permission they previously tapped "Don't
    /// ask again" on. After two declines Android refuses to re-show the
    /// runtime dialog from inside the app; the Settings deep-link is
    /// the only path back. Returns true if the intent fired, false if
    /// the platform couldn't resolve it.</summary>
    Task<bool> OpenAppSettingsAsync();
}

/// <summary>Tri-state status for the peer-alert permission set.</summary>
public enum PeerAlertPermissionStatus
{
    /// <summary>Platform doesn't need any permissions (iOS, Windows,
    /// pre-Android-12) OR every permission is granted.</summary>
    Granted,

    /// <summary>At least one required permission is denied. Peer-alert
    /// still works in degraded mode (no background scanning); the
    /// foreground siren + screen flash (Phase 8.1) are unaffected.</summary>
    Denied,

    /// <summary>Couldn't determine status — typically because the
    /// platform API isn't reachable. Treat as Granted to avoid blocking
    /// the user on an unrelated runtime issue.</summary>
    Unknown
}

/// <summary>Default implementation — does nothing, reports Granted.
/// Registered on iOS, Windows, and Android &lt; 12 where the permission
/// flow doesn't apply.</summary>
public class NoOpPeerAlertPermissions : IPeerAlertPermissions
{
    public bool IsApplicable      => false;
    public bool HasPromptedBefore => true;
    public Task<PeerAlertPermissionStatus> StatusAsync() => Task.FromResult(PeerAlertPermissionStatus.Granted);
    public Task<PeerAlertPermissionStatus> PromptAsync() => Task.FromResult(PeerAlertPermissionStatus.Granted);

    // No platform-specific Settings page on iOS/Windows from this NoOp;
    // those platforms either don't need it (Windows) or handle it via
    // their own settings flow (iOS uses UIApplication.OpenSettingsURLString,
    // wired separately when an iOS implementation lands). Returning false
    // tells the caller "no deep-link available — surface a fallback hint".
    public Task<bool> OpenAppSettingsAsync() => Task.FromResult(false);
}
