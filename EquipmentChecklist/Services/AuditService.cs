using System.Security.Claims;
using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Identity;

namespace EquipmentChecklist.Services;

/// <summary>
/// Append-only audit logger. Every interesting action on the system goes
/// through one of the two <c>LogAsync</c> overloads and lands in the
/// <see cref="ApplicationDbContext.AuditEvents"/> table.
///
/// <para>Resolution rules for the actor:</para>
/// <list type="number">
///   <item><description>If the caller supplies an <c>ActorOverride</c>
///   (the mobile batch path does this so the JWT subject is honoured even
///   when the controller is also signed in), use it.</description></item>
///   <item><description>Otherwise resolve from
///   <see cref="IHttpContextAccessor"/> — the current ClaimsPrincipal +
///   request IP.</description></item>
///   <item><description>If neither, the event is recorded with no actor
///   (system / background-job style).</description></item>
/// </list>
///
/// <para>The actor's display name + email + primary role are denormalised
/// onto the row at log time so the audit trail stays readable even if the
/// user is later deleted or has their role changed.</para>
/// </summary>
public class AuditService
{
    private readonly ApplicationDbContext         _db;
    private readonly IHttpContextAccessor         _http;
    private readonly UserManager<ApplicationUser> _users;
    private readonly ILogger<AuditService>        _log;

    public AuditService(ApplicationDbContext db,
                        IHttpContextAccessor http,
                        UserManager<ApplicationUser> users,
                        ILogger<AuditService> log)
    {
        _db    = db;
        _http  = http;
        _users = users;
        _log   = log;
    }

    /// <summary>
    /// Snapshot of the actor used to populate audit rows. Constructed
    /// either from the current request or supplied explicitly by the
    /// mobile sync endpoint.
    /// </summary>
    public record ActorContext(
        string? UserId,
        string? Name,
        string? Email,
        string? Role,
        string  DeviceKind,
        string? IpAddress);

    /// <summary>
    /// Resolve the actor from the current HttpContext. Returns an empty
    /// (null-fields) context when there's no request — e.g. when audit
    /// is invoked from a background job.
    /// </summary>
    public async Task<ActorContext> ResolveCurrentActorAsync(string deviceKind = "web")
    {
        var ctx = _http.HttpContext;
        if (ctx?.User?.Identity?.IsAuthenticated != true)
            return new ActorContext(null, null, null, null, deviceKind, ctx?.Connection.RemoteIpAddress?.ToString());

        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        ApplicationUser? user = null;
        if (!string.IsNullOrEmpty(userId))
        {
            user = await _users.FindByIdAsync(userId);
        }

        string? role = null;
        if (user != null)
        {
            // Pick the highest-privilege role they're in — matters when an
            // Admin also happens to be assigned as an Operator on a machine.
            var roles = await _users.GetRolesAsync(user);
            role = PickPrimaryRole(roles);
        }

        return new ActorContext(
            UserId:    userId,
            Name:      user?.FullName,
            Email:     user?.Email,
            Role:      role,
            DeviceKind:deviceKind,
            IpAddress: ctx.Connection.RemoteIpAddress?.ToString());
    }

    /// <summary>
    /// Log one event using the actor implied by the current
    /// HttpContext. Returns the row Id so callers can chain.
    /// </summary>
    public Task<long> LogAsync(string action,
                               string? targetType  = null,
                               long?   targetId    = null,
                               object? payload     = null,
                               CancellationToken ct = default)
        // Every optional arg from `actor` onwards is named, so the final `ct`
        // also has to be named — once a call uses out-of-position named args,
        // any later positional ones become ambiguous to the compiler.
        => LogAsyncCore(action, targetType, targetId, payload,
                        actor: null, occurredAtClient: null, ct: ct);

    /// <summary>
    /// Log one event with an explicit actor. Used by the mobile-sync
    /// endpoint so the JWT subject (not the controller's user) is recorded
    /// as the actor, and the client's timestamp is preserved.
    /// </summary>
    public Task<long> LogAsync(ActorContext actor,
                               string  action,
                               string? targetType,
                               long?   targetId,
                               string? payloadJson,
                               DateTime occurredAtClient,
                               CancellationToken ct = default)
    {
        // Payload comes in as already-serialised JSON from the mobile DTO.
        return LogAsyncCore(action, targetType, targetId,
            payloadRaw: payloadJson,
            actor: actor,
            occurredAtClient: occurredAtClient,
            ct: ct);
    }

    private async Task<long> LogAsyncCore(string action,
                                          string? targetType,
                                          long?   targetId,
                                          object? payload         = null,
                                          string? payloadRaw      = null,
                                          ActorContext? actor     = null,
                                          DateTime? occurredAtClient = null,
                                          CancellationToken ct = default)
    {
        // Declared outside the try so the catch can detach it from the
        // change tracker if SaveChanges throws — see the comment in catch.
        AuditEvent? row = null;
        try
        {
            actor ??= await ResolveCurrentActorAsync();

            string? json = payloadRaw;
            if (json is null && payload is not null)
                json = System.Text.Json.JsonSerializer.Serialize(payload);

            var nowUtc = DateTime.UtcNow;
            row = new AuditEvent
            {
                ActorUserId      = actor.UserId,
                ActorName        = actor.Name,
                ActorEmail       = actor.Email,
                ActorRole        = actor.Role,
                Action           = action,
                TargetType       = targetType,
                TargetId         = targetId,
                PayloadJson      = json,
                OccurredAtClient = (occurredAtClient ?? nowUtc).ToUniversalTime(),
                OccurredAtServer = nowUtc,
                DeviceKind       = actor.DeviceKind,
                IpAddress        = actor.IpAddress
            };

            _db.AuditEvents.Add(row);
            await _db.SaveChangesAsync(ct);
            return row.Id;
        }
        catch (Exception ex)
        {
            // Audit MUST NEVER take down the calling request. Two parts to
            // making that safe:
            //
            //   1. Swallow + log the exception so the caller keeps going.
            //   2. DETACH the failed entity from the change tracker. Without
            //      this, the next SaveChanges on the same scoped DbContext
            //      (e.g. NotificationService.PushAsync, which runs right
            //      after SupervisorSignOffAsync calls us) tries to flush the
            //      still-Added audit row and re-throws — the user sees
            //      "An error occurred while saving the entity changes" on a
            //      completely unrelated action.
            if (row is not null)
            {
                try { _db.Entry(row).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
                catch { /* tracker may already be disposed */ }
            }
            _log.LogError(ex,
                "Failed to write audit event {Action} for target {TargetType}#{TargetId}",
                action, targetType, targetId);
            return 0;
        }
    }

    /// <summary>
    /// Convenience for batch writes from the mobile sync endpoint — uses
    /// AddRange so the audit batch is one round-trip instead of N.
    /// </summary>
    public async Task<int> LogBatchAsync(ActorContext actor,
                                         IEnumerable<AuditEventDto> events,
                                         CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var rows = new List<AuditEvent>();
        foreach (var e in events)
        {
            if (string.IsNullOrWhiteSpace(e.Action)) continue;
            rows.Add(new AuditEvent
            {
                ActorUserId      = actor.UserId,
                ActorName        = actor.Name,
                ActorEmail       = actor.Email,
                ActorRole        = actor.Role,
                Action           = e.Action,
                TargetType       = e.TargetType,
                TargetId         = e.TargetId,
                PayloadJson      = e.PayloadJson,
                OccurredAtClient = (e.OccurredAtClient == default ? nowUtc : e.OccurredAtClient).ToUniversalTime(),
                OccurredAtServer = nowUtc,
                DeviceKind       = string.IsNullOrEmpty(e.DeviceKind) ? actor.DeviceKind : e.DeviceKind,
                IpAddress        = actor.IpAddress
            });
        }

        if (rows.Count == 0) return 0;

        try
        {
            _db.AuditEvents.AddRange(rows);
            await _db.SaveChangesAsync(ct);
            return rows.Count;
        }
        catch (Exception ex)
        {
            // Same detach trick as LogAsyncCore — otherwise the failed batch
            // sits in the tracker as Added and poisons the next SaveChanges.
            foreach (var r in rows)
            {
                try { _db.Entry(r).State = Microsoft.EntityFrameworkCore.EntityState.Detached; }
                catch { }
            }
            _log.LogError(ex, "Failed to persist mobile audit batch ({Count} rows)", rows.Count);
            return 0;
        }
    }

    /// <summary>
    /// Pick the most-privileged role from the caller's role set. Used to
    /// stamp the audit row with a single, deterministic role string instead
    /// of the comma-separated list IdentityCore returns.
    /// </summary>
    private static string? PickPrimaryRole(IList<string> roles)
    {
        // Order matters — first match wins.
        string[] priority = { "Admin", "Supervisor", "Mechanic", "Operator" };
        foreach (var r in priority)
            if (roles.Contains(r)) return r;
        return roles.FirstOrDefault();
    }
}
