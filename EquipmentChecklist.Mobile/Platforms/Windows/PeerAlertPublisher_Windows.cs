#if WINDOWS
using EquipmentChecklist.Mobile.Services;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace EquipmentChecklist.Mobile.Platforms.Windows;

/// <summary>
/// Phase 8.8 — Windows implementation of <see cref="IPeerAlertService"/>
/// using <c>BluetoothLEAdvertisementPublisher</c> +
/// <c>BluetoothLEAdvertisementWatcher</c> from the
/// <c>Windows.Devices.Bluetooth.Advertisement</c> namespace.
///
/// <para>Targets Surface Pro 11 (the Windows tablet on the mine
/// procurement RFQ) running Windows 11. The Windows BLE stack is
/// considerably more mature than the Android one — no permission
/// dialogs at runtime, no foreground-service shenanigans. The only
/// caveat is the <c>bluetooth</c> capability needs to be declared in
/// <c>Package.appxmanifest</c> (added separately in
/// <c>Platforms/Windows/Package.appxmanifest</c>).</para>
///
/// <para>Same wire-format as the Android implementation — both platforms
/// advertise + scan the same service UUID and the same manufacturer
/// data layout (status / machineId / timestamp / lengthPrefixedMachine /
/// operatorName). A Galaxy Tab Active 5 operator's NO-GO broadcast is
/// caught by a Surface Pro supervisor + vice versa.</para>
///
/// <para>Background advertising on Windows works similarly to foreground
/// — the OS doesn't kill the app's BLE publisher when the window
/// minimises. We don't need a "foreground service" abstraction here;
/// the publisher just keeps running. <see cref="IsAvailable"/> reports
/// true when the BluetoothAdapter is found + supports advertising
/// (modern Surface laptops do; some older Win10 desktops with
/// dongle-attached BLE don't).</para>
/// </summary>
public class PeerAlertPublisherWindows : IPeerAlertService
{
    public string ServiceUuid => NoOpPeerAlertService.DefaultServiceUuid;
    public bool   IsAvailable { get; private set; } = true;   // optimistic; set to false on first failure
    public event Action<PeerAlertPayload>? PeerAlertReceived;

    // Same Manufacturer ID + payload layout as the Android publisher so
    // Surface and Galaxy Tab tablets can talk to each other.
    private const ushort ManufacturerId = 0xFFFF;

    private readonly ILogger<PeerAlertPublisherWindows> _log;
    private BluetoothLEAdvertisementPublisher?           _publisher;
    private BluetoothLEAdvertisementWatcher?             _watcher;
    private readonly HashSet<string> _recentHashes = new();
    private DateTime _lastHashSweep = DateTime.UtcNow;
    private System.Threading.Timer? _autoStopTimer;

    public PeerAlertPublisherWindows(ILogger<PeerAlertPublisherWindows> log)
    {
        _log = log;
    }

    public Task StartBroadcastAsync(PeerAlertPayload payload, int durationSec = 300)
    {
        try
        {
            StopBroadcastInternal();   // never overlap two broadcasts

            // Build the manufacturer-data buffer in the same byte layout
            // the Android publisher uses. The two implementations stay in
            // sync via this shared format — change one, change both.
            var data = BuildPayloadBuffer(payload);

            var manufacturerData = new BluetoothLEManufacturerData
            {
                CompanyId = ManufacturerId,
                Data      = data.AsBuffer()
            };

            _publisher = new BluetoothLEAdvertisementPublisher();
            _publisher.Advertisement.ManufacturerData.Add(manufacturerData);
            // Add the service UUID as part of the advertisement so other
            // devices' watchers (Android scan filters) match correctly.
            _publisher.Advertisement.ServiceUuids.Add(Guid.Parse(ServiceUuid));

            _publisher.StatusChanged += (sender, args) =>
            {
                if (args.Status == BluetoothLEAdvertisementPublisherStatus.Started)
                    _log.LogInformation("PeerAlert Windows publisher started");
                else if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted)
                {
                    _log.LogWarning("PeerAlert Windows publisher aborted: {Error}", args.Error);
                    IsAvailable = false;
                }
            };

            _publisher.Start();

            // Windows publisher has no built-in timeout (unlike Android's
            // AdvertiseSettings.SetTimeout). We schedule our own auto-stop
            // so a forgotten broadcast doesn't run all shift.
            _autoStopTimer = new System.Threading.Timer(_ => StopBroadcastInternal(),
                state: null, dueTime: TimeSpan.FromSeconds(durationSec),
                period: System.Threading.Timeout.InfiniteTimeSpan);

            _log.LogInformation("PeerAlert Windows broadcasting NOGO for {Machine} ({Sec}s)",
                payload.MachineNumber, durationSec);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PeerAlert Windows broadcast failed");
        }
        return Task.CompletedTask;
    }

    public Task StopBroadcastAsync()
    {
        StopBroadcastInternal();
        return Task.CompletedTask;
    }

    private void StopBroadcastInternal()
    {
        try { _autoStopTimer?.Dispose(); } catch { }
        _autoStopTimer = null;
        try
        {
            if (_publisher != null && _publisher.Status == BluetoothLEAdvertisementPublisherStatus.Started)
                _publisher.Stop();
            _publisher = null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "PeerAlert Windows stop-publisher failed"); }
    }

    public Task StartListeningAsync()
    {
        if (_watcher != null) return Task.CompletedTask;
        try
        {
            _watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = BluetoothLEScanningMode.Passive   // power-friendly default
            };
            // Filter by our service UUID so we don't get every BLE
            // beacon in the building.
            _watcher.AdvertisementFilter.Advertisement.ServiceUuids.Add(Guid.Parse(ServiceUuid));

            _watcher.Received += OnAdvertisementReceived;
            _watcher.Stopped  += (sender, args) =>
                _log.LogInformation("PeerAlert Windows watcher stopped: {Error}", args.Error);

            _watcher.Start();
            _log.LogInformation("PeerAlert Windows watcher started");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PeerAlert Windows watcher start failed");
            IsAvailable = false;
        }
        return Task.CompletedTask;
    }

    public Task StopListeningAsync()
    {
        try
        {
            if (_watcher != null && _watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                _watcher.Stop();
            _watcher = null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "PeerAlert Windows watcher stop failed"); }
        return Task.CompletedTask;
    }

    private void OnAdvertisementReceived(
        BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            var mfg = args.Advertisement.ManufacturerData
                .FirstOrDefault(m => m.CompanyId == ManufacturerId);
            if (mfg == null) return;

            // Copy the WindowsRT buffer into a managed byte[] so we can
            // run our shared decode logic against it.
            var bytes = new byte[mfg.Data.Length];
            using (var reader = DataReader.FromBuffer(mfg.Data))
                reader.ReadBytes(bytes);

            if (bytes.Length < 13) return;

            var payload = ParsePayloadBuffer(bytes);
            if (payload == null) return;

            // Dedupe by machine + timestamp + status — same logic as
            // the Android publisher.
            if ((DateTime.UtcNow - _lastHashSweep).TotalMinutes > 5)
            {
                _recentHashes.Clear();
                _lastHashSweep = DateTime.UtcNow;
            }
            var hash = $"{payload.MachineId}|{(int)(payload.RaisedAtUtc - DateTime.UnixEpoch).TotalSeconds}|{payload.Status}";
            lock (_recentHashes)
            {
                if (_recentHashes.Contains(hash)) return;
                _recentHashes.Add(hash);
            }

            try { PeerAlertReceived?.Invoke(payload); }
            catch (Exception ex) { _log.LogError(ex, "PeerAlert Windows receive handler threw"); }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PeerAlert Windows advertisement parse failed");
        }
    }

    // ── Shared payload codec ─────────────────────────────────────────────
    // Same byte layout as Android: 4-byte ASCII status + 4-byte BE machine
    // id + 4-byte BE Unix-seconds timestamp + 1-byte machineNumber-length +
    // utf-8 machineNumber + utf-8 operatorName (trailing). Match the
    // Android impl byte-for-byte.

    internal static byte[] BuildPayloadBuffer(PeerAlertPayload payload)
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

    internal static PeerAlertPayload? ParsePayloadBuffer(byte[] bytes)
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
}
#endif
