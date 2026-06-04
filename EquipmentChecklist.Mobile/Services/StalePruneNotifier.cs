namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Tiny bridge service: subscribes to <see cref="SyncWorker.PrunedStale"/>
/// and converts the event into a user-visible toast via
/// <see cref="ToastService"/>.
///
/// <para>This lives as its own service (rather than a few lines inside
/// <c>MainLayout</c>) for three reasons:</para>
/// <list type="bullet">
///   <item><description>It needs to be wired up BEFORE the layout renders,
///   so a prune fired during startup isn't missed.</description></item>
///   <item><description>The subscription has to live for the whole app
///   lifetime — a transient component would unsubscribe on dispose and we'd
///   lose later events.</description></item>
///   <item><description>It's testable in isolation: pass a fake
///   <see cref="ToastService"/> and assert on the message text.</description></item>
/// </list>
/// </summary>
public class StalePruneNotifier : IDisposable
{
    private readonly ToastService _toasts;
    private readonly SyncWorker   _sync;
    private bool                  _disposed;

    public StalePruneNotifier(ToastService toasts, SyncWorker sync)
    {
        _toasts = toasts;
        _sync   = sync;
        _sync.PrunedStale += OnPrunedStale;
    }

    private void OnPrunedStale(StalePruneInfo info)
    {
        // Compose a sentence that calls out each non-zero category. Users
        // care about "what didn't ship" more than the exact numbers, so
        // the message leads with the human consequence and includes the
        // counts as supporting detail.
        var days = (int)Math.Round(info.MaxAge.TotalDays);
        var parts = new List<string>(3);
        if (info.SubmissionsDropped > 0)
            parts.Add($"{info.SubmissionsDropped} submission{(info.SubmissionsDropped == 1 ? "" : "s")}");
        if (info.ActionsDropped > 0)
            parts.Add($"{info.ActionsDropped} action{(info.ActionsDropped == 1 ? "" : "s")}");
        if (info.AuditDropped > 0)
            parts.Add($"{info.AuditDropped} audit log{(info.AuditDropped == 1 ? "" : "s")}");

        if (parts.Count == 0) return;   // defensive — shouldn't fire on zero
        var what = string.Join(", ", parts);
        var msg  = $"Dropped {what} older than {days} days — they never reached the server.";

        // Warning, not Error: the work is gone but the app is fine, and the
        // operator's CURRENT submissions are unaffected. Surfacing this as
        // a hard red banner would scare people unnecessarily.
        _toasts.Warning(msg, durationMs: 10000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sync.PrunedStale -= OnPrunedStale;
        GC.SuppressFinalize(this);
    }
}
