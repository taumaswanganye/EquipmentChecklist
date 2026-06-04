using System.Net.Http.Json;
using EquipmentChecklist.DTOs;
using Microsoft.AspNetCore.SignalR.Client;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Mobile-side counterpart of the server's NotificationService.
///
/// <para>Responsibilities:</para>
/// <list type="bullet">
///   <item><description>Maintain a SignalR connection to <c>/hubs/notifications</c> with the user's JWT.</description></item>
///   <item><description>Fire <see cref="Received"/> + <see cref="UnreadCountChanged"/> events when a notification arrives so the topbar bell badge updates instantly.</description></item>
///   <item><description>Fall back to HTTP polling for the inbox list / unread count when the hub is offline — the data layer is the source of truth.</description></item>
/// </list>
///
/// <para>One singleton for the app. Force-resolved in MauiProgram so the
/// connection starts as soon as the user is signed in.</para>
/// </summary>
public class NotificationService : IAsyncDisposable
{
    private readonly ApiClient   _api;
    private readonly AuthService _auth;
    private readonly ToastService _toasts;

    private HubConnection? _hub;
    private bool           _disposed;
    private int            _unreadCount;

    public NotificationService(ApiClient api, AuthService auth, ToastService toasts)
    {
        _api    = api;
        _auth   = auth;
        _toasts = toasts;

        _auth.SignedIn  += OnSignedIn;
        _auth.SignedOut += OnSignedOut;

        // If the app boots already signed in (offline-cached token), kick
        // the connection right away.
        if (_auth.IsSignedIn) _ = StartAsync();
    }

    /// <summary>Latest known unread count. Mobile UI binds to this via
    /// <see cref="UnreadCountChanged"/>.</summary>
    public int UnreadCount => _unreadCount;

    /// <summary>Fired every time the server pushes a "notification" frame.</summary>
    public event Action<NotificationDto>? Received;

    /// <summary>Fired when the unread badge needs to repaint.</summary>
    public event Action<int>? UnreadCountChanged;

    private void OnSignedIn()  => _ = StartAsync();
    private void OnSignedOut() => _ = StopAsync();

    /// <summary>
    /// Idempotent: safe to call multiple times. Skips if already connected.
    /// Also fetches the initial unread count via HTTP so the badge is
    /// accurate before the first push lands.
    /// </summary>
    public async Task StartAsync()
    {
        if (_disposed) return;
        if (_hub is not null && _hub.State != HubConnectionState.Disconnected) return;

        var token = await _auth.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return;

        var baseAddr = _api.BaseAddress?.ToString().TrimEnd('/');
        if (string.IsNullOrEmpty(baseAddr)) return;

        try
        {
            _hub = new HubConnectionBuilder()
                // SignalR's WebSocket transport can't set headers — append
                // the token as a query-string parameter that the server's
                // JwtBearer OnMessageReceived handler picks up.
                .WithUrl($"{baseAddr}/hubs/notifications?access_token={token}")
                .WithAutomaticReconnect()
                .Build();

            _hub.On<NotificationDto>("notification", OnPush);

            await _hub.StartAsync();
        }
        catch
        {
            // Server unreachable / WebSocket blocked. The unread count and
            // inbox list keep working via HTTP — the badge just won't
            // update in real time.
        }

        await RefreshUnreadCountAsync();
    }

    public async Task StopAsync()
    {
        if (_hub is not null)
        {
            try { await _hub.StopAsync();   } catch { }
            try { await _hub.DisposeAsync(); } catch { }
            _hub = null;
        }
        SetUnread(0);
    }

    private void OnPush(NotificationDto n)
    {
        // Fire the event for the dropdown to prepend the row, raise a toast
        // so the user can't miss it, and bump the unread badge.
        Received?.Invoke(n);
        _toasts.Info(n.Title);
        SetUnread(_unreadCount + 1);
    }

    /// <summary>HTTP fallback for the badge. Called on connect and on
    /// every mark-read so the count stays honest even if a SignalR frame
    /// dropped on the way.</summary>
    public async Task RefreshUnreadCountAsync()
    {
        try
        {
            var count = await _api.NotificationsUnreadCountAsync();
            if (count.HasValue) SetUnread(count.Value);
        }
        catch { /* offline — keep the previous count */ }
    }

    /// <summary>Inbox list — called by the dropdown component on open.</summary>
    public Task<List<NotificationDto>?> GetRecentAsync(int take = 30)
        => _api.NotificationsAsync(take);

    public async Task MarkReadAsync(int id)
    {
        var ok = await _api.NotificationsMarkReadAsync(id);
        if (ok) await RefreshUnreadCountAsync();
    }

    public async Task MarkAllReadAsync()
    {
        var ok = await _api.NotificationsMarkAllReadAsync();
        if (ok) SetUnread(0);
    }

    private void SetUnread(int next)
    {
        if (_unreadCount == next) return;
        _unreadCount = next;
        UnreadCountChanged?.Invoke(next);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _auth.SignedIn  -= OnSignedIn;
        _auth.SignedOut -= OnSignedOut;
        await StopAsync();
    }
}
