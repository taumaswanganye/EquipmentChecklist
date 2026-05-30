using System.Text.Json;
using SQLite;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// SQLite-backed queue for supervisor and mechanic actions that couldn't reach
/// the server. Distinct from <see cref="SubmissionQueue"/> (operators) because
/// the payloads and target IDs differ — sign-off targets a SubmissionId, while
/// claim/order-part/complete target a DefectOrderId — and we want each queue to
/// stay independently drainable.
///
/// <para>One row per queued action. Each carries:</para>
/// <list type="bullet">
///   <item><description><c>ActionType</c> discriminator (sign-off, reject, claim, order-part, complete)</description></item>
///   <item><description><c>TargetId</c> — the server-side id the action operates on</description></item>
///   <item><description><c>PayloadJson</c> — request body, serialized at enqueue time</description></item>
///   <item><description>retry / last-error housekeeping</description></item>
/// </list>
///
/// <para>Dedupe: rows are uniqued on <c>(ActionType, TargetId)</c>. If the user
/// taps Approve twice while offline the second tap overwrites the first — which
/// is what they expect: the latest tap wins.</para>
/// </summary>
public class ActionQueue
{
    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim         _initLock = new(1, 1);
    private bool                           _initialized;

    public event Action? Changed;

    public ActionQueue()
    {
        // Shares the same DB file as LocalCache + SubmissionQueue.
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
            await _db.CreateTableAsync<QueuedAction>();
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    // ── Enqueue helpers (one per supported action) ──────────────────────────

    public Task EnqueueSignOffAsync(int submissionId, int resolution, string signature) =>
        EnqueueAsync(ActionKinds.SignOff, submissionId, new { Resolution = resolution, Signature = signature });

    public Task EnqueueRejectAsync(int submissionId, string reason, string mechanicId) =>
        EnqueueAsync(ActionKinds.Reject, submissionId, new { Reason = reason, MechanicId = mechanicId });

    public Task EnqueueClaimAsync(int defectOrderId) =>
        EnqueueAsync(ActionKinds.Claim, defectOrderId, new { });

    public Task EnqueueOrderPartAsync(int defectOrderId, string partRequired, string? partNumber) =>
        EnqueueAsync(ActionKinds.OrderPart, defectOrderId, new { PartRequired = partRequired, PartNumber = partNumber });

    public Task EnqueueCompleteAsync(int defectOrderId, string? notes, string signature) =>
        EnqueueAsync(ActionKinds.Complete, defectOrderId, new { Notes = notes, Signature = signature });

    /// <summary>
    /// Generic enqueue. Overwrites any existing row with the same
    /// (ActionType, TargetId) so retries reflect the user's latest intent.
    /// </summary>
    private async Task EnqueueAsync(string actionType, int targetId, object payload)
    {
        await EnsureInitAsync();

        var json = JsonSerializer.Serialize(payload);

        // Look for an existing row to update (lets the user "change their mind"
        // while still offline — e.g. re-sign with a cleaner signature).
        var existing = await _db.Table<QueuedAction>()
            .Where(q => q.ActionType == actionType && q.TargetId == targetId)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.PayloadJson = json;
            existing.QueuedAt    = DateTime.UtcNow;
            existing.RetryCount  = 0;
            existing.LastError   = null;
            await _db.UpdateAsync(existing);
        }
        else
        {
            await _db.InsertAsync(new QueuedAction
            {
                ActionType  = actionType,
                TargetId    = targetId,
                PayloadJson = json,
                QueuedAt    = DateTime.UtcNow,
                RetryCount  = 0
            });
        }
        Changed?.Invoke();
    }

    // ── Drain helpers ───────────────────────────────────────────────────────

    public async Task<List<QueuedAction>> GetAllAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<QueuedAction>()
            .OrderBy(q => q.QueuedAt)
            .ToListAsync();
    }

    public async Task<int> CountAsync()
    {
        await EnsureInitAsync();
        return await _db.Table<QueuedAction>().CountAsync();
    }

    public async Task RemoveAsync(int id)
    {
        await EnsureInitAsync();
        await _db.DeleteAsync<QueuedAction>(id);
        Changed?.Invoke();
    }

    public async Task MarkRetryAsync(int id, string? error)
    {
        await EnsureInitAsync();
        var row = await _db.FindAsync<QueuedAction>(id);
        if (row == null) return;
        row.RetryCount += 1;
        row.LastError   = error;
        row.LastTriedAt = DateTime.UtcNow;
        await _db.UpdateAsync(row);
    }

    public async Task ClearAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<QueuedAction>();
        Changed?.Invoke();
    }

    /// <summary>
    /// Constants for the <see cref="QueuedAction.ActionType"/> discriminator.
    /// Kept as strings (not enums) so the column stays human-readable when
    /// inspecting the SQLite file during debugging.
    /// </summary>
    public static class ActionKinds
    {
        public const string SignOff   = "signoff";
        public const string Reject    = "reject";
        public const string Claim     = "claim";
        public const string OrderPart = "order-part";
        public const string Complete  = "complete";
    }

    [Table("queued_actions")]
    public class QueuedAction
    {
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        /// <summary>One of <see cref="ActionKinds"/>.</summary>
        [Indexed] public string ActionType { get; set; } = "";
        /// <summary>SubmissionId for sign-off / reject; DefectOrderId otherwise.</summary>
        [Indexed] public int TargetId { get; set; }
        public string  PayloadJson { get; set; } = "{}";
        public DateTime QueuedAt    { get; set; }
        public int      RetryCount  { get; set; }
        public string?  LastError   { get; set; }
        public DateTime? LastTriedAt { get; set; }
    }
}
