using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace EquipmentChecklist.Services;

/// <summary>
/// SignalR hub mobile clients connect to for real-time notifications.
///
/// <para>Auth: JWT bearer — same scheme as <c>/api/sync/*</c>. The token is
/// passed as a query-string param <c>?access_token=…</c> (SignalR's
/// negotiated transport doesn't let you set <c>Authorization</c> headers on
/// the WebSocket upgrade).</para>
///
/// <para>Routing: each connection joins a single group named <c>user:{userId}</c>
/// so <see cref="NotificationService"/> can target one user with a single
/// <c>SendAsync</c>.</para>
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = ResolveUserId(Context.User);
        if (!string.IsNullOrEmpty(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupNameFor(userId));
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = ResolveUserId(Context.User);
        if (!string.IsNullOrEmpty(userId))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupNameFor(userId));
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Stable group name format — also used by
    /// <see cref="NotificationService"/> when broadcasting.</summary>
    public static string GroupNameFor(string userId) => $"user:{userId}";

    /// <summary>
    /// JWTs are issued with the user id on the <c>sub</c> claim; ASP.NET
    /// also surfaces it as <see cref="ClaimTypes.NameIdentifier"/>. Either
    /// works in practice — check both.
    /// </summary>
    private static string? ResolveUserId(ClaimsPrincipal? principal)
        => principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
           ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
}
