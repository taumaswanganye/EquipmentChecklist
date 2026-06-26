using EquipmentChecklist.Models;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Phase 8.2 — Bluetooth LE peer-to-peer urgent broadcast.
///
/// <para>Companion to Phase 8.1 UrgentAlertService. When an operator
/// raises a NO-GO with no server connectivity, the device:</para>
/// <list type="number">
///   <item><description>Plays the loud siren (Phase 8.1) so anyone within
///   ~50m hears it.</description></item>
///   <item><description>Starts BLE advertising (this service) so any other
///   Equipment Checklist device within ~30m receives a digital alert with
///   the machine number + operator name + timestamp.</description></item>
/// </list>
///
/// <para>The two channels are complementary: BLE works through walls (siren
/// doesn't) and is silent (siren is loud). A supervisor 200m away on the
/// other side of a rock outcrop might miss the siren but their tablet's
/// BLE scanner could still catch the broadcast — provided they're within
/// the ~30m line-of-sight or ~10m through-rock range.</para>
///
/// <para>Protocol design (vendor-agnostic, fits any Equipment Checklist
/// device):</para>
/// <list type="bullet">
///   <item><description>Service UUID <see cref="ServiceUuid"/> is the
///   discriminator — only devices advertising this UUID belong to our app.
///   Random-generated v4 GUID, won't collide with other BLE services.</description></item>
///   <item><description>Local name = "ECL-NOGO" + 6 base64 chars of message
///   hash. The hash lets receivers dedupe the same broadcast from a
///   repeating advertiser without parsing manufacturer data.</description></item>
///   <item><description>Manufacturer data = compact payload (under 26 bytes
///   to fit in the BLE advertising packet): machineId (4) + statusByte (1)
///   + timestampSec (4) + operatorNameTruncated (up to 17). Receivers
///   reconstruct the message + show a popup.</description></item>
/// </list>
///
/// <para><b>Platform support:</b></para>
/// <list type="bullet">
///   <item><description><b>Android</b> — fully supported via the platform's
///   <c>BluetoothLeAdvertiser</c> + <c>BluetoothLeScanner</c>. Requires
///   <c>BLUETOOTH_ADVERTISE</c> + <c>BLUETOOTH_SCAN</c> + <c>BLUETOOTH_CONNECT</c>
///   permissions on Android 12+ (already requested in AndroidManifest.xml
///   for the existing biometric code path; this re-uses those grants).</description></item>
///   <item><description><b>Windows</b> — <c>BluetoothLEAdvertisementPublisher</c>
///   + <c>BluetoothLEAdvertisementWatcher</c> via Windows.Devices.Bluetooth.
///   Requires the <c>bluetooth</c> capability in Package.appxmanifest.</description></item>
///   <item><description><b>iOS</b> — <c>CBPeripheralManager</c> + <c>CBCentralManager</c>
///   via CoreBluetooth. Restricted in background; for foreground operator
///   use this is fine.</description></item>
/// </list>
///
/// <para>The current implementation ships the interface + no-op default.
/// Each platform's native code lives in <c>Platforms/{Android,Windows,iOS}/PeerAlertPublisher_*.cs</c>
/// (Android scaffold first because Galaxy Tab Active 5 is the primary
/// target hardware per the procurement RFQ). The interface lets the rest
/// of the app fire <c>BroadcastNoGoAsync</c> with confidence that on
/// Windows it currently no-ops + on Android it actually broadcasts.</para>
/// </summary>
public interface IPeerAlertService
{
    /// <summary>The BLE service UUID every Equipment Checklist device
    /// advertises + scans for. Hardcoded constant — never changes — so
    /// devices on different app versions still recognise each other.</summary>
    string ServiceUuid { get; }

    /// <summary>Is BLE supported + permitted on this device?</summary>
    bool IsAvailable { get; }

    /// <summary>Start broadcasting an urgent NO-GO alert. Other Equipment
    /// Checklist devices within range receive it via their scanner +
    /// raise <see cref="PeerAlertReceived"/>. Auto-stops after
    /// <paramref name="durationSec"/> so a forgotten broadcast doesn't
    /// drain the operator's battery for hours.</summary>
    Task StartBroadcastAsync(PeerAlertPayload payload, int durationSec = 300);

    /// <summary>Stop broadcasting immediately (e.g. operator dismissed
    /// the alert because they've now reached the supervisor in person).</summary>
    Task StopBroadcastAsync();

    /// <summary>Start the background scanner. Called once on app launch
    /// (per AuthService.SignedIn). Idempotent — calling twice does
    /// nothing. The scanner runs continuously while the operator is
    /// signed in.</summary>
    Task StartListeningAsync();

    /// <summary>Stop the scanner (sign-out / app suspend).</summary>
    Task StopListeningAsync();

    /// <summary>Fires when our scanner picks up an urgent broadcast from
    /// another Equipment Checklist device. The UI subscribes + shows a
    /// big modal popup with the machine number + operator + timestamp +
    /// haptic vibration. Dedupe by message hash inside this service so
    /// a repeating advertiser only fires the event once per minute.</summary>
    event Action<PeerAlertPayload>? PeerAlertReceived;
}

/// <summary>The payload broadcast over BLE — kept small enough to fit
/// in a single advertising packet (~26 bytes max for manufacturer data).</summary>
/// <param name="MachineNumber">Up to 12 chars (ADT-04, SHV-DT01 etc.).</param>
/// <param name="MachineId">Server-side primary key for trace-back when devices reconcile.</param>
/// <param name="OperatorName">Truncated to 16 chars to fit. "Sipho Khumalo" → "Sipho Khumalo".</param>
/// <param name="Status">"NOGO" today — reserved for future "DEFECT_RESOLVED" + other broadcasts.</param>
/// <param name="RaisedAtUtc">When the originating operator raised the NO-GO. Receivers can age it out.</param>
public record PeerAlertPayload(
    string   MachineNumber,
    int      MachineId,
    string   OperatorName,
    string   Status,
    DateTime RaisedAtUtc);

/// <summary>
/// Default no-op implementation. Registered when no platform-specific
/// implementation is available (or on platforms where BLE is unsupported
/// / disabled by user). Lets the rest of the app call
/// <see cref="IPeerAlertService.StartBroadcastAsync"/> without an
/// if-null check.
///
/// <para>The Phase 8.2 Android implementation lives in
/// <c>Platforms/Android/PeerAlertPublisher_Android.cs</c> and is wired
/// in via a conditional DI registration in MauiProgram.cs. Until that
/// file exists this NoOp is what the app uses across all platforms.</para>
/// </summary>
public class NoOpPeerAlertService : IPeerAlertService
{
    public const string DefaultServiceUuid = "5b1a0001-7e84-4f7e-9a02-belfast0001";

    private readonly ILogger<NoOpPeerAlertService> _log;
    public NoOpPeerAlertService(ILogger<NoOpPeerAlertService> log) { _log = log; }

    public string ServiceUuid => DefaultServiceUuid;
    public bool   IsAvailable => false;

    public Task StartBroadcastAsync(PeerAlertPayload payload, int durationSec = 300)
    {
        _log.LogInformation(
            "PeerAlert [NoOp] would broadcast NO-GO for machine {Machine} by {Operator} (duration {Sec}s)",
            payload.MachineNumber, payload.OperatorName, durationSec);
        return Task.CompletedTask;
    }

    public Task StopBroadcastAsync()
    {
        _log.LogDebug("PeerAlert [NoOp] would stop broadcast");
        return Task.CompletedTask;
    }

    public Task StartListeningAsync()
    {
        _log.LogDebug("PeerAlert [NoOp] would start scanner");
        return Task.CompletedTask;
    }

    public Task StopListeningAsync()
    {
        _log.LogDebug("PeerAlert [NoOp] would stop scanner");
        return Task.CompletedTask;
    }

    public event Action<PeerAlertPayload>? PeerAlertReceived;
}
