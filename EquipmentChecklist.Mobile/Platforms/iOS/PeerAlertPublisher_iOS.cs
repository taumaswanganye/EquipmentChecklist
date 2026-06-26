#if IOS || MACCATALYST
using CoreBluetooth;
using EquipmentChecklist.Mobile.Services;
using Foundation;
using Microsoft.Extensions.Logging;
using System.Text;

namespace EquipmentChecklist.Mobile.Platforms.iOS;

/// <summary>
/// Phase 8.7 — iOS implementation of <see cref="IPeerAlertService"/>
/// using <c>CBPeripheralManager</c> for broadcast +
/// <c>CBCentralManager</c> for scan.
///
/// <para><b>Caveats relative to Android + Windows:</b></para>
/// <list type="bullet">
///   <item><description><b>Service UUID in advertisement is mandatory.</b>
///   iOS doesn't surface manufacturer data when the app is backgrounded
///   — the OS strips it. We work around by stuffing the same payload
///   into a <c>CBAdvertisementDataLocalNameKey</c> short-name string
///   (encoded base64-no-padding) and let scanners read it from there.
///   Works in foreground; degrades gracefully in background.</description></item>
///   <item><description><b>Background scanning</b> requires the
///   <c>bluetooth-central</c> background mode in <c>Info.plist</c>
///   (added separately). Even with that, iOS heavily rate-limits BLE
///   scans in the background — your scanner will fire callbacks
///   intermittently rather than continuously.</description></item>
///   <item><description><b>Background advertising</b> requires the
///   <c>bluetooth-peripheral</c> background mode + an overflow area
///   advertisement (iOS doesn't allow the standard service-UUID
///   advertisement format in background). The current implementation
///   doesn't enable overflow; broadcasts only fire while the app is in
///   the foreground.</description></item>
/// </list>
///
/// <para>For a mine deployment that's primarily Android (Galaxy Tab
/// Active 5) + Windows (Surface Pro) this is acceptable — iPad would
/// be a secondary device that doesn't need 24/7 BLE coverage. If iOS
/// becomes a primary platform, plan for an overflow-area encoding +
/// continuous foreground keep-alive (the LayoutManager pattern). Out
/// of scope for the initial scaffold.</para>
///
/// <para>Info.plist additions required (declared in the iOS project
/// info plist, not this file):</para>
/// <code>
/// &lt;key&gt;NSBluetoothAlwaysUsageDescription&lt;/key&gt;
/// &lt;string&gt;Used to alert nearby tablets when a NO-GO is raised offline.&lt;/string&gt;
/// &lt;key&gt;UIBackgroundModes&lt;/key&gt;
/// &lt;array&gt;
///   &lt;string&gt;bluetooth-central&lt;/string&gt;
///   &lt;string&gt;bluetooth-peripheral&lt;/string&gt;
/// &lt;/array&gt;
/// </code>
/// </summary>
public class PeerAlertPublisherIos : NSObject, IPeerAlertService
{
    public string ServiceUuid => NoOpPeerAlertService.DefaultServiceUuid;
    public bool   IsAvailable => _central?.State == CBManagerState.PoweredOn
                              || _peripheral?.State == CBManagerState.PoweredOn;
    public event Action<PeerAlertPayload>? PeerAlertReceived;

    private readonly ILogger<PeerAlertPublisherIos> _log;
    private readonly CBUUID _serviceCbuuid;

    private CBCentralManager?    _central;
    private CBPeripheralManager? _peripheral;
    private CentralDelegate?     _centralDelegate;
    private PeripheralDelegate?  _peripheralDelegate;

    private NSDictionary?        _pendingAdvertisementData;
    private DateTime?            _advertisementStopAt;
    private readonly HashSet<string> _recentHashes = new();
    private DateTime _lastHashSweep = DateTime.UtcNow;

    public PeerAlertPublisherIos(ILogger<PeerAlertPublisherIos> log)
    {
        _log = log;
        _serviceCbuuid = CBUUID.FromString(ServiceUuid);
    }

    public Task StartBroadcastAsync(PeerAlertPayload payload, int durationSec = 300)
    {
        try
        {
            // Encode the payload as a base64 short-name. Same byte layout
            // as Android + Windows, then base64-no-padding so it fits in
            // the iOS local-name field (~ 20 byte limit on the wire).
            var bytes  = BuildPayloadBuffer(payload);
            var b64    = Convert.ToBase64String(bytes).TrimEnd('=');
            var localName = "ECL" + b64;     // "ECL" prefix lets scanners filter quickly

            _pendingAdvertisementData = NSDictionary.FromObjectsAndKeys(
                new NSObject[]
                {
                    NSArray.FromObjects(_serviceCbuuid),
                    new NSString(localName)
                },
                new NSObject[]
                {
                    CBAdvertisement.DataServiceUUIDsKey,
                    CBAdvertisement.DataLocalNameKey
                });

            _advertisementStopAt = DateTime.UtcNow.AddSeconds(durationSec);

            _peripheralDelegate ??= new PeripheralDelegate(this);
            _peripheral         ??= new CBPeripheralManager(_peripheralDelegate,
                                       DispatchQueue.DefaultGlobalQueue);

            // Defer actual StartAdvertising until the peripheral manager
            // reports PoweredOn (PeripheralDelegate.StateUpdated will fire
            // it). On a cold boot the state isn't ready immediately.
            if (_peripheral.State == CBManagerState.PoweredOn)
                TryStartAdvertisingPending();

            _log.LogInformation("PeerAlert iOS broadcast queued for {Machine} ({Sec}s)",
                payload.MachineNumber, durationSec);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PeerAlert iOS broadcast failed");
        }
        return Task.CompletedTask;
    }

    public Task StopBroadcastAsync()
    {
        try
        {
            if (_peripheral?.Advertising == true) _peripheral.StopAdvertising();
            _pendingAdvertisementData = null;
            _advertisementStopAt = null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "PeerAlert iOS stop-advertise failed"); }
        return Task.CompletedTask;
    }

    public Task StartListeningAsync()
    {
        try
        {
            _centralDelegate ??= new CentralDelegate(this);
            _central         ??= new CBCentralManager(_centralDelegate,
                                     DispatchQueue.DefaultGlobalQueue);

            if (_central.State == CBManagerState.PoweredOn)
                TryStartScanningPending();
        }
        catch (Exception ex) { _log.LogError(ex, "PeerAlert iOS listener start failed"); }
        return Task.CompletedTask;
    }

    public Task StopListeningAsync()
    {
        try { _central?.StopScan(); }
        catch (Exception ex) { _log.LogWarning(ex, "PeerAlert iOS listener stop failed"); }
        return Task.CompletedTask;
    }

    private void TryStartAdvertisingPending()
    {
        if (_peripheral == null || _pendingAdvertisementData == null) return;
        try
        {
            _peripheral.StartAdvertising(_pendingAdvertisementData);
            _log.LogInformation("PeerAlert iOS advertising started");

            // Auto-stop timer matching Android's SetTimeout. Capture the
            // exact stop time so a re-trigger before timeout doesn't
            // race with the previous timer.
            var stopAt = _advertisementStopAt;
            if (stopAt.HasValue)
            {
                var delay = stopAt.Value - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    _ = Task.Delay(delay).ContinueWith(_ =>
                    {
                        if (_advertisementStopAt == stopAt) StopBroadcastAsync();
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PeerAlert iOS deferred-advertise failed");
        }
    }

    private void TryStartScanningPending()
    {
        if (_central == null) return;
        try
        {
            // ScanForPeripherals with a service-UUID filter so we don't
            // surface every BLE beacon in the cab.
            _central.ScanForPeripherals(new[] { _serviceCbuuid });
            _log.LogInformation("PeerAlert iOS scanner started");
        }
        catch (Exception ex) { _log.LogError(ex, "PeerAlert iOS scanner start failed"); }
    }

    private void OnDiscovered(CBPeripheral peripheral, NSDictionary advertisementData)
    {
        try
        {
            // Pull the local name (where we packed the base64 payload).
            var localName = (advertisementData[CBAdvertisement.DataLocalNameKey] as NSString)?.ToString();
            if (string.IsNullOrEmpty(localName) || !localName.StartsWith("ECL")) return;

            var b64 = localName.Substring(3);
            // Restore base64 padding.
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "=";  break;
            }
            byte[] bytes;
            try { bytes = Convert.FromBase64String(b64); }
            catch { return; }

            var payload = ParsePayloadBuffer(bytes);
            if (payload == null) return;

            // Same dedupe pattern as Android + Windows.
            if ((DateTime.UtcNow - _lastHashSweep).TotalMinutes > 5)
            {
                _recentHashes.Clear();
                _lastHashSweep = DateTime.UtcNow;
            }
            var tsSec = (int)(payload.RaisedAtUtc - DateTime.UnixEpoch).TotalSeconds;
            var hash = $"{payload.MachineId}|{tsSec}|{payload.Status}";
            lock (_recentHashes)
            {
                if (_recentHashes.Contains(hash)) return;
                _recentHashes.Add(hash);
            }
            try { PeerAlertReceived?.Invoke(payload); }
            catch (Exception ex) { _log.LogError(ex, "PeerAlert iOS receive handler threw"); }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PeerAlert iOS advertisement parse failed");
        }
    }

    // ── Shared payload codec — must match Android + Windows byte-for-byte
    private static byte[] BuildPayloadBuffer(PeerAlertPayload payload)
    {
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
        var machineNumBytes = Encoding.UTF8.GetBytes(machineNum);
        var machineNumLen   = (byte)machineNumBytes.Length;
        var operatorBytes   = Encoding.UTF8.GetBytes(operatorTr);

        using var ms = new MemoryStream();
        ms.Write(statusBytes);
        ms.Write(idBytes);
        ms.Write(tsBytes);
        ms.WriteByte(machineNumLen);
        ms.Write(machineNumBytes);
        ms.Write(operatorBytes);
        return ms.ToArray();
    }

    private static PeerAlertPayload? ParsePayloadBuffer(byte[] bytes)
    {
        if (bytes.Length < 13) return null;
        try
        {
            var status     = Encoding.ASCII.GetString(bytes, 0, 4).Trim();
            var idBytes    = bytes.Skip(4).Take(4).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(idBytes);
            var machineId  = BitConverter.ToInt32(idBytes, 0);
            var tsBytes    = bytes.Skip(8).Take(4).ToArray();
            if (BitConverter.IsLittleEndian) Array.Reverse(tsBytes);
            var tsSec      = BitConverter.ToInt32(tsBytes, 0);
            var ts         = DateTime.UnixEpoch.AddSeconds(tsSec);
            var nameLen    = bytes[12];
            if (12 + 1 + nameLen > bytes.Length) return null;
            var machineNum = Encoding.UTF8.GetString(bytes, 13, nameLen);
            var operatorName = 13 + nameLen < bytes.Length
                ? Encoding.UTF8.GetString(bytes, 13 + nameLen, bytes.Length - 13 - nameLen)
                : "";
            return new PeerAlertPayload(machineNum, machineId, operatorName, status, ts);
        }
        catch { return null; }
    }

    private sealed class CentralDelegate : CBCentralManagerDelegate
    {
        private readonly PeerAlertPublisherIos _parent;
        public CentralDelegate(PeerAlertPublisherIos parent) { _parent = parent; }

        public override void UpdatedState(CBCentralManager central)
        {
            if (central.State == CBManagerState.PoweredOn)
                _parent.TryStartScanningPending();
        }

        public override void DiscoveredPeripheral(
            CBCentralManager central,
            CBPeripheral peripheral,
            NSDictionary advertisementData,
            NSNumber RSSI)
        {
            _parent.OnDiscovered(peripheral, advertisementData);
        }
    }

    private sealed class PeripheralDelegate : CBPeripheralManagerDelegate
    {
        private readonly PeerAlertPublisherIos _parent;
        public PeripheralDelegate(PeerAlertPublisherIos parent) { _parent = parent; }

        public override void StateUpdated(CBPeripheralManager peripheral)
        {
            if (peripheral.State == CBManagerState.PoweredOn)
                _parent.TryStartAdvertisingPending();
        }
    }
}
#endif
