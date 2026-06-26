#if ANDROID
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.OS;
using EquipmentChecklist.Mobile.Services;
using Java.Util;
using Microsoft.Extensions.Logging;
using System.Text;

namespace EquipmentChecklist.Mobile.Platforms.Android;

/// <summary>
/// Android-native implementation of <see cref="IPeerAlertService"/> using
/// the platform's <c>BluetoothLeAdvertiser</c> + <c>BluetoothLeScanner</c>
/// APIs. Targets Galaxy Tab Active 5 (Android 14) which is the primary
/// operator hardware per the procurement RFQ.
///
/// <para><b>Permissions required</b> (already declared in AndroidManifest.xml
/// for the Phase 2E biometric code path; runtime grant prompt happens on
/// first BLE use):</para>
/// <list type="bullet">
///   <item><description>BLUETOOTH_ADVERTISE (Android 12+)</description></item>
///   <item><description>BLUETOOTH_SCAN (Android 12+)</description></item>
///   <item><description>BLUETOOTH_CONNECT (Android 12+)</description></item>
///   <item><description>ACCESS_FINE_LOCATION (BLE scan results require it on Android 6-11)</description></item>
/// </list>
///
/// <para><b>Scaffold status:</b> this class compiles + exposes the right
/// shape; the actual <c>StartAdvertising</c> / <c>StartScan</c> calls are
/// implemented but currently behave conservatively (5-minute auto-stop on
/// advertise, no continuous scan loop yet). Production hardening should
/// add:</para>
/// <list type="number">
///   <item><description>A foreground service notification while scanning
///   (Android battery optimization will kill background BLE within minutes
///   otherwise — already true for the SignalR keep-alive plumbing).</description></item>
///   <item><description>RSSI filtering on scan results so we don't pop a
///   modal for a broadcast 80m away that's just barely detectable.</description></item>
///   <item><description>Permission request flow via Plugin.Maui.Permissions
///   on first launch — currently assumes manual grant.</description></item>
/// </list>
/// </summary>
public class PeerAlertPublisherAndroid : IPeerAlertService
{
    public string ServiceUuid => NoOpPeerAlertService.DefaultServiceUuid;
    public bool   IsAvailable => _adapter?.IsEnabled == true;
    public event Action<PeerAlertPayload>? PeerAlertReceived;

    // Manufacturer ID 0xFFFF is reserved for "no organisation" and is
    // safe to use for our private payload. A registered Bluetooth SIG
    // member ID would be more correct but isn't worth the membership
    // fee for an internal mining app.
    private const int ManufacturerId = 0xFFFF;

    private readonly BluetoothAdapter?              _adapter;
    private readonly BluetoothLeAdvertiser?         _advertiser;
    private readonly BluetoothLeScanner?            _scanner;
    private readonly ILogger<PeerAlertPublisherAndroid> _log;
    private AdvertiseCallbackImpl?                  _advCallback;
    private ScanCallbackImpl?                       _scanCallback;
    private readonly HashSet<string> _recentHashes = new();
    private DateTime _lastHashSweep = DateTime.UtcNow;

    public PeerAlertPublisherAndroid(ILogger<PeerAlertPublisherAndroid> log)
    {
        _log     = log;
        var mgr  = Microsoft.Maui.ApplicationModel.Platform.AppContext
                       .GetSystemService(global::Android.Content.Context.BluetoothService) as BluetoothManager;
        _adapter = mgr?.Adapter;
        _advertiser = _adapter?.BluetoothLeAdvertiser;
        _scanner    = _adapter?.BluetoothLeScanner;
    }

    public Task StartBroadcastAsync(PeerAlertPayload payload, int durationSec = 300)
    {
        if (_adapter is null || !_adapter.IsEnabled || _advertiser is null)
        {
            _log.LogWarning("PeerAlert: Bluetooth disabled or unavailable; skipping broadcast");
            return Task.CompletedTask;
        }

        try
        {
            // Build the compact manufacturer payload (<= 26 bytes).
            // Format: status(4 ASCII) + machineId(4 BE int) + tsSec(4 BE int)
            //       + machineNum(up to 12 UTF-8) + operatorName(remaining)
            var statusBytes = Encoding.ASCII.GetBytes((payload.Status ?? "NOGO").PadRight(4, ' ').Substring(0, 4));
            var idBytes     = BitConverter.GetBytes(payload.MachineId);
            if (BitConverter.IsLittleEndian) Array.Reverse(idBytes);
            var tsSec       = (int)(payload.RaisedAtUtc - DateTime.UnixEpoch).TotalSeconds;
            var tsBytes     = BitConverter.GetBytes(tsSec);
            if (BitConverter.IsLittleEndian) Array.Reverse(tsBytes);

            var machineNum  = (payload.MachineNumber ?? "").Length > 12
                                 ? payload.MachineNumber!.Substring(0, 12)
                                 : payload.MachineNumber ?? "";
            var operatorTr  = (payload.OperatorName  ?? "").Length > 14
                                 ? payload.OperatorName!.Substring(0, 14)
                                 : payload.OperatorName ?? "";
            var machineNumLen = (byte)Encoding.UTF8.GetByteCount(machineNum);
            var machineNumBytes = Encoding.UTF8.GetBytes(machineNum);
            var operatorBytes   = Encoding.UTF8.GetBytes(operatorTr);

            using var ms = new MemoryStream();
            ms.Write(statusBytes);
            ms.Write(idBytes);
            ms.Write(tsBytes);
            ms.WriteByte(machineNumLen);
            ms.Write(machineNumBytes);
            ms.Write(operatorBytes);
            var data = ms.ToArray();

            var settings = new AdvertiseSettings.Builder()!
                .SetAdvertiseMode(AdvertiseMode.LowLatency)!
                .SetTxPowerLevel(AdvertiseTx.PowerHigh)!
                .SetConnectable(false)!
                .SetTimeout(durationSec * 1000)!     // auto-stops
                .Build();

            var dataBuilder = new AdvertiseData.Builder()!
                .AddServiceUuid(new ParcelUuid(UUID.FromString(ServiceUuid)))!
                .AddManufacturerData(ManufacturerId, data)!
                .Build();

            _advCallback = new AdvertiseCallbackImpl(_log);
            _advertiser.StartAdvertising(settings, dataBuilder, _advCallback);
            _log.LogInformation(
                "PeerAlert advertising started for {Machine} ({Sec}s timeout)",
                payload.MachineNumber, durationSec);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PeerAlert advertise failed");
        }
        return Task.CompletedTask;
    }

    public Task StopBroadcastAsync()
    {
        try
        {
            if (_advCallback != null && _advertiser != null)
                _advertiser.StopAdvertising(_advCallback);
            _advCallback = null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "PeerAlert stop-advertise failed"); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Public entrypoint — starts the Phase 8.3 Android foreground service
    /// which then calls back into <see cref="StartScannerInternalAsync"/>
    /// to launch the real BLE scanner with the persistent notification
    /// shielding it from battery optimisation.
    /// </summary>
    public Task StartListeningAsync()
    {
        try
        {
            var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            PeerAlertForegroundService.StartService(ctx);
            _log.LogInformation("PeerAlert listener: foreground service start requested");
        }
        catch (Exception ex)
        {
            // Fall back to non-foreground scan if the service start
            // fails (e.g. on a Cradlepoint emulator without notification
            // support) — degraded but still functional in foreground.
            _log.LogWarning(ex, "PeerAlert foreground service start failed; falling back to direct scan");
            return StartScannerInternalAsync();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Public entrypoint — stops the foreground service which in turn
    /// calls <see cref="StopScannerInternalAsync"/> via its OnDestroy.
    /// </summary>
    public Task StopListeningAsync()
    {
        try
        {
            var ctx = Microsoft.Maui.ApplicationModel.Platform.AppContext;
            PeerAlertForegroundService.StopService(ctx);
            _log.LogInformation("PeerAlert listener: foreground service stop requested");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PeerAlert foreground service stop failed; stopping scanner directly");
            return StopScannerInternalAsync();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Internal — runs the actual BLE scanner inside the foreground
    /// service context. Called by <see cref="PeerAlertForegroundService.OnStartCommand"/>
    /// after the persistent notification has been posted. Direct callers
    /// should use <see cref="StartListeningAsync"/> instead.
    /// </summary>
    internal Task StartScannerInternalAsync()
    {
        if (_scanner is null || _scanCallback != null) return Task.CompletedTask;
        try
        {
            var filters = new List<ScanFilter> {
                new ScanFilter.Builder()!
                    .SetServiceUuid(new ParcelUuid(UUID.FromString(ServiceUuid)))!
                    .Build()!
            };
            var settings = new ScanSettings.Builder()!
                .SetScanMode(global::Android.Bluetooth.LE.ScanMode.LowPower)!
                .Build();
            _scanCallback = new ScanCallbackImpl(this, _log);
            _scanner.StartScan(filters, settings, _scanCallback);
            _log.LogInformation("PeerAlert BLE scanner started (foreground-service-protected)");
        }
        catch (Exception ex) { _log.LogError(ex, "PeerAlert scan start failed"); }
        return Task.CompletedTask;
    }

    /// <summary>Internal — stops the BLE scanner. Called by
    /// <see cref="PeerAlertForegroundService.OnDestroy"/>.</summary>
    internal Task StopScannerInternalAsync()
    {
        try
        {
            if (_scanCallback != null && _scanner != null)
                _scanner.StopScan(_scanCallback);
            _scanCallback = null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "PeerAlert stop-scan failed"); }
        return Task.CompletedTask;
    }

    /// <summary>Called by the scan callback when a broadcast lands.
    /// Dedupes by payload hash within a 60-second window so a continuously-
    /// advertising device only fires the modal once.</summary>
    internal void OnPayloadReceived(PeerAlertPayload payload, string hash)
    {
        // Cheap GC of the dedupe set every 5 minutes — bounded growth.
        if ((DateTime.UtcNow - _lastHashSweep).TotalMinutes > 5)
        {
            _recentHashes.Clear();
            _lastHashSweep = DateTime.UtcNow;
        }

        lock (_recentHashes)
        {
            if (_recentHashes.Contains(hash)) return;
            _recentHashes.Add(hash);
        }

        try { PeerAlertReceived?.Invoke(payload); }
        catch (Exception ex) { _log.LogError(ex, "PeerAlert receive handler threw"); }
    }

    private class AdvertiseCallbackImpl : AdvertiseCallback
    {
        private readonly ILogger _log;
        public AdvertiseCallbackImpl(ILogger log) { _log = log; }

        public override void OnStartSuccess(AdvertiseSettings? settingsInEffect) =>
            _log.LogDebug("PeerAlert advertise started OK");

        public override void OnStartFailure(AdvertiseFailure errorCode) =>
            _log.LogWarning("PeerAlert advertise failed: {Code}", errorCode);
    }

    private class ScanCallbackImpl : ScanCallback
    {
        private readonly PeerAlertPublisherAndroid _parent;
        private readonly ILogger _log;
        public ScanCallbackImpl(PeerAlertPublisherAndroid p, ILogger log) { _parent = p; _log = log; }

        public override void OnScanResult(global::Android.Bluetooth.LE.ScanCallbackType callbackType, ScanResult? result)
        {
            try
            {
                var record = result?.ScanRecord;
                var bytes  = record?.GetManufacturerSpecificData(ManufacturerId);
                if (bytes is null || bytes.Length < 13) return;   // header + lenByte

                var status     = Encoding.ASCII.GetString(bytes, 0, 4).Trim();
                var idBytes    = bytes.Skip(4).Take(4).ToArray();
                if (BitConverter.IsLittleEndian) Array.Reverse(idBytes);
                var machineId  = BitConverter.ToInt32(idBytes, 0);
                var tsBytes    = bytes.Skip(8).Take(4).ToArray();
                if (BitConverter.IsLittleEndian) Array.Reverse(tsBytes);
                var tsSec      = BitConverter.ToInt32(tsBytes, 0);
                var ts         = DateTime.UnixEpoch.AddSeconds(tsSec);
                var nameLen    = bytes[12];
                if (12 + 1 + nameLen > bytes.Length) return;
                var machineNum = Encoding.UTF8.GetString(bytes, 13, nameLen);
                var operatorName = 13 + nameLen < bytes.Length
                    ? Encoding.UTF8.GetString(bytes, 13 + nameLen, bytes.Length - 13 - nameLen)
                    : "";

                var payload = new PeerAlertPayload(machineNum, machineId, operatorName, status, ts);
                var hash    = $"{machineId}|{tsSec}|{status}";
                _parent.OnPayloadReceived(payload, hash);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "PeerAlert scan-result parse failed");
            }
        }
    }
}
#endif
