using System.Text.Json;
using EquipmentChecklist.DTOs;
using SQLite;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// SQLite-backed append-only buffer for audit events generated on the device.
///
/// <para>Every interesting user action (sign-in, submit, sign-off, claim,
/// part order, complete, biometric unlock) writes one row here first. The
/// <see cref="SyncWorker"/> drains the buffer to <c>POST /api/sync/audit</c>
/// when the device is online and the server is reachable.</para>
///
/// <para>Why a separate queue from <see cref="SubmissionQueue"/> /
/// <see cref="ActionQueue"/>?</para>
/// <list type="bullet">
///   <item><description>Different retention semantics: audit data is forever
///   — we never drop on a 4xx the way an "already-claimed" defect would.</description></item>
///   <item><description>Different volume curve: a busy supervisor can
///   generate dozens of audit rows per minute (every view click eventually,
///   every modal open), so it deserves its own batch-sized drain.</description></item>
///   <item><description>Different priority: audit drain is lowest priority
///   in the SyncWorker — submissions and actions ship first because they
///   block downstream workflow.</description></item>
/// </list>
/// </summary>
public class AuditQueue
{
    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim         _initLock = new(1, 1);
    private bool                           _initialized;

    public AuditQueue()
    {
        // Same DB file as the other queues — fewer SQLite handles, simpler
        // lifecycle. Different table name keeps the rows separate.
        var path = Path.Combine(FileSystem.AppDataDirectory, "eqcache.db");
        _db = new SQLiteAsyncConnection(path,
            SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.SharedCache);
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await _db.CreateTableAsync<PendingAuditRow>();
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    /// <summary>Append one event. Never throws — audit writes must not take
    /// down the calling UI path.</summary>
    public async Task EnqueueAsync(AuditEventDto evt)
    {
        try
        {
            await EnsureInitAsync();
            if (string.IsNullOrWhiteSpace(evt.Action)) return;

            await _db.InsertAsync(new PendingAuditRow
            {
                Action           = evt.Action,
                TargetType       = evt.TargetType,
                TargetId         = evt.TargetId,
                OccurredAtClient = evt.OccurredAtClient == default ? DateTime.UtcNow : evt.OccurredAtClient,
                DeviceKind       = string.IsNullOrEmpty(evt.DeviceKind) ? DefaultDeviceKind() : evt.DeviceKind,
                PayloadJson      = evt.PayloadJson,
                QueuedAt         = DateTime.UtcNow
            });
        }
        catch
        {
            // Swallow — never block a user action because the audit write
            // failed. Lost audit rows are recoverable (Sentry, the surrounding
            // domain rows still tell the story); a crashed sign-in is not.
        }
    }

    /// <summary>
    /// Read up to <paramref name="max"/> rows in insertion order. The SyncWorker
    /// uses this to build a batch payload.
    /// </summary>
    public async Task<List<PendingAuditRow>> TakeBatchAsync(int max = 100)
    {
        await EnsureInitAsync();
        return await _db.Table<PendingAuditRow>()
            .OrderBy(r => r.Id)
            .Take(max)
            .ToListAsync();
    }

    /// <summary>Delete the rows whose ids are listed. Called from the worker
    /// after a successful POST.</summary>
    public async Task DeleteAsync(IEnumerable<int> ids)
    {
        await EnsureInitAsync();
        foreach (var id in ids)
        {
            try { await _db.DeleteAsync<PendingAuditRow>(id); } catch { }
        }
    }

    /// <summary>Total queued — surfaced in dev diagnostics.</summary>
    public async Task<int> CountAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<PendingAuditRow>().CountAsync();
    }

    /// <summary>
    /// Drop audit rows older than <paramref name="maxAge"/>. Bounded
    /// retention applies here too — even though audit is "forever" in the
    /// authoritative store on the server, the LOCAL buffer can't be allowed
    /// to grow forever on a phone that's been offline for months. We're
    /// conservative on age (default 90 days from SyncWorker) since dropping
    /// audit rows means permanently losing local-timing detail that the
    /// server-side equivalent doesn't capture.
    /// </summary>
    public async Task<PruneResult> PruneOldAsync(TimeSpan maxAge)
    {
        await EnsureInitAsync();
        var cutoff = DateTime.UtcNow - maxAge;

        var stale = await _db.Table<PendingAuditRow>()
            .Where(r => r.QueuedAt < cutoff)
            .ToListAsync();
        foreach (var s in stale)
            try { await _db.DeleteAsync(s); } catch { /* races */ }

        DateTime? oldest = null;
        var survivor = await _db.Table<PendingAuditRow>()
            .OrderBy(r => r.QueuedAt)
            .FirstOrDefaultAsync();
        if (survivor != null) oldest = survivor.QueuedAt;

        return new PruneResult(stale.Count, oldest);
    }

    /// <summary>Map a queued row → DTO ready for the wire.</summary>
    public static AuditEventDto ToDto(PendingAuditRow row) => new()
    {
        Action           = row.Action,
        TargetType       = row.TargetType,
        TargetId         = row.TargetId,
        OccurredAtClient = row.OccurredAtClient,
        DeviceKind       = row.DeviceKind,
        PayloadJson      = row.PayloadJson
    };

    private static string DefaultDeviceKind()
    {
#if ANDROID
        return "android";
#elif WINDOWS
        return "windows";
#else
        return "web";
#endif
    }

    /// <summary>SQLite-mapped row. Internal to this file.</summary>
    public class PendingAuditRow
    {
        [PrimaryKey, AutoIncrement] public int       Id               { get; set; }
                                    public string    Action           { get; set; } = "";
                                    public string?   TargetType       { get; set; }
                                    public long?     TargetId         { get; set; }
                                    public DateTime  OccurredAtClient { get; set; }
                                    public string    DeviceKind       { get; set; } = "android";
                                    public string?   PayloadJson      { get; set; }
                                    public DateTime  QueuedAt         { get; set; }
    }
}
