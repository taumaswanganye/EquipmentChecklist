using System.Text.Json;
using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.AspNetCore.SignalR;

namespace EquipmentChecklist.Services;

/// <summary>
/// Single entry point for emitting notifications. Every push:
/// <list type="number">
///   <item><description>Writes a row to <c>notifications</c> so the recipient sees it even if they're offline / not connected to SignalR right now.</description></item>
///   <item><description>Broadcasts to the user's SignalR group so connected clients update instantly without polling.</description></item>
/// </list>
///
/// <para>Side effects are best-effort — a SignalR send failure must NEVER
/// break the calling business operation (e.g. submission persistence). The
/// row in the table is the source of truth; the broadcast is just sugar.</para>
/// </summary>
public class NotificationService
{
    private readonly ApplicationDbContext       _db;
    private readonly IHubContext<NotificationHub> _hub;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(
        ApplicationDbContext db,
        IHubContext<NotificationHub> hub,
        ILogger<NotificationService> log)
    {
        _db  = db;
        _hub = hub;
        _log = log;
    }

    public async Task PushAsync(
        string  userId,
        string  kind,
        string  title,
        string? body                = null,
        object? payload             = null,
        int?    relatedSubmissionId = null,
        int?    relatedMachineId    = null)
    {
        if (string.IsNullOrWhiteSpace(userId)) return;

        var notif = new Notification
        {
            UserId              = userId,
            Kind                = kind,
            Title               = title,
            Body                = body,
            PayloadJson         = payload is null ? null : JsonSerializer.Serialize(payload),
            RelatedSubmissionId = relatedSubmissionId,
            RelatedMachineId    = relatedMachineId,
            CreatedAt           = DateTime.UtcNow
        };

        // Defensive: notification writes MUST NEVER break the surrounding
        // business operation. If the SaveChanges throws (table missing,
        // FK violation, etc.) we:
        //   1. Detach the failed entity from the change tracker, otherwise
        //      the NEXT caller of this scoped DbContext's SaveChanges
        //      re-throws on the still-Added row (same trap as AuditService).
        //   2. Log + swallow so the caller's work succeeds anyway.
        //
        // The cost of swallowing: this one notification is lost. The cost
        // of not swallowing: the operator can't submit a checklist because
        // their supervisor's mailbox can't be written to. The first is the
        // better trade-off every time.
        try
        {
            _db.Notifications.Add(notif);
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            try { _db.Entry(notif).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
            catch { /* tracker may already be disposed */ }
            _log.LogError(ex,
                "Failed to persist notification ({Kind}) for user {UserId}. " +
                "Caller's work proceeds without the notification.",
                kind, userId);
            return;   // skip the SignalR push — nothing to broadcast
        }

        // Real-time broadcast — wrapped in try/catch so a hub blip never
        // backs out the DB write. The client will still see the
        // notification next time it polls /notifications/unread-count or
        // re-opens the dropdown.
        try
        {
            await _hub.Clients
                .Group(NotificationHub.GroupNameFor(userId))
                .SendAsync("notification", new
                {
                    id                  = notif.Id,
                    kind                = notif.Kind,
                    title               = notif.Title,
                    body                = notif.Body,
                    relatedSubmissionId = notif.RelatedSubmissionId,
                    relatedMachineId    = notif.RelatedMachineId,
                    createdAt           = notif.CreatedAt
                });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "SignalR broadcast failed for notification {NotifId} to user {UserId}. " +
                "Row is persisted; client will see it on next poll.",
                notif.Id, userId);
        }
    }
}
