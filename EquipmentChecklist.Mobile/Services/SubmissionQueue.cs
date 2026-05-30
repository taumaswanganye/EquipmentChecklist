using System.Text.Json;
using EquipmentChecklist.DTOs;
using SQLite;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// SQLite-backed queue of checklist submissions that couldn't reach the server.
///
/// Lives in the same DB file as <see cref="LocalCache"/> but uses its own
/// connection. Each row stores the serialized <see cref="SyncSubmissionRequest"/>
/// plus housekeeping for retry attempts. The server dedupes on
/// <c>LocalId</c>, so even a double-replay can't produce duplicate submissions.
///
/// Lifecycle:
///   - <see cref="Checklist"/> enqueues on a network error.
///   - <see cref="SyncWorker"/> drains the queue when connectivity returns.
///   - Dashboard / Checklist subscribe to <see cref="Changed"/> to refresh
///     their "N queued" badge.
/// </summary>
public class SubmissionQueue
{
    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim         _initLock = new(1, 1);
    private bool                           _initialized;

    public event Action? Changed;

    public SubmissionQueue()
    {
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
            await _db.CreateTableAsync<PendingSubmission>();
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    /// <summary>Add a submission to the back of the queue. Idempotent on LocalId.</summary>
    public async Task EnqueueAsync(SyncSubmissionRequest req)
    {
        await EnsureInitAsync();

        // If we already queued this LocalId (e.g. user tapped Submit twice
        // while offline), don't duplicate it — the server would dedupe anyway
        // but the queue is tidier this way.
        var existing = await _db.Table<PendingSubmission>()
            .Where(p => p.LocalId == req.LocalId)
            .FirstOrDefaultAsync();
        if (existing != null) return;

        await _db.InsertAsync(new PendingSubmission
        {
            LocalId     = req.LocalId,
            MachineId   = req.MachineId,
            JsonPayload = JsonSerializer.Serialize(req),
            QueuedAt    = DateTime.UtcNow,
            RetryCount  = 0
        });
        Changed?.Invoke();
    }

    public async Task<List<PendingSubmission>> GetAllAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<PendingSubmission>()
            .OrderBy(p => p.QueuedAt)
            .ToListAsync();
    }

    public async Task<int> CountAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<PendingSubmission>().CountAsync();
    }

    public async Task RemoveAsync(int id)
    {
        await EnsureInitAsync();
        await _db.DeleteAsync<PendingSubmission>(id);
        Changed?.Invoke();
    }

    public async Task MarkRetryAsync(int id, string? error)
    {
        await EnsureInitAsync();
        var row = await _db.FindAsync<PendingSubmission>(id);
        if (row == null) return;
        row.RetryCount  += 1;
        row.LastError    = error;
        row.LastTriedAt  = DateTime.UtcNow;
        await _db.UpdateAsync(row);
    }

    public async Task ClearAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<PendingSubmission>();
        Changed?.Invoke();
    }

    [Table("pending_submissions")]
    public class PendingSubmission
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed(Unique = true)]
        public Guid     LocalId      { get; set; }
        public int      MachineId    { get; set; }
        public string   JsonPayload  { get; set; } = "";
        public DateTime QueuedAt     { get; set; }
        public int      RetryCount   { get; set; }
        public string?  LastError    { get; set; }
        public DateTime? LastTriedAt { get; set; }
    }
}
