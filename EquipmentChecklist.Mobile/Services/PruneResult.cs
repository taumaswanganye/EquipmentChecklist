namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Outcome of a single <c>PruneOldAsync</c> pass. Shared across
/// <see cref="SubmissionQueue"/>, <see cref="ActionQueue"/>, and
/// <see cref="AuditQueue"/> so the SyncWorker can summarise them
/// uniformly.
/// </summary>
/// <param name="Dropped">Number of rows older than the configured age
/// limit that were removed.</param>
/// <param name="OldestRemainingAt">Timestamp of the oldest surviving
/// row, or null when the queue is now empty. Used by the UI to render
/// "oldest queued: 14 days ago" diagnostics.</param>
public record PruneResult(int Dropped, DateTime? OldestRemainingAt)
{
    public static PruneResult Empty { get; } = new(0, null);
}

/// <summary>
/// Aggregated summary of a single prune cycle, raised by the
/// SyncWorker on <c>PrunedStale</c>. Lets one subscriber render a
/// single toast like "Dropped 3 submissions, 1 action older than
/// 60 days." instead of the user seeing three separate notices.
/// </summary>
public record StalePruneInfo(
    int SubmissionsDropped,
    int ActionsDropped,
    int AuditDropped,
    TimeSpan MaxAge)
{
    public int Total => SubmissionsDropped + ActionsDropped + AuditDropped;
}
