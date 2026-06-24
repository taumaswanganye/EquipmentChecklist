using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

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

    // ── Hash-chain serialisation ─────────────────────────────────────────
    // Process-wide semaphore around the (read last RowHash) → (compute new
    // hash) → (insert) critical section. Without it, two simultaneous audit
    // writes could both read the same "previous" RowHash and chain to it,
    // breaking the chain at the second one. For a multi-instance deployment
    // we'd need a distributed lock; this single-process lock is correct for
    // the pilot single-API-instance topology.
    private static readonly SemaphoreSlim ChainLock = new(1, 1);

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

            // Compute the tamper-evident chain hash inside a process-wide
            // critical section so concurrent writers don't both chain to
            // the same "previous" row. The lock is held only for the brief
            // window of read-prev → compute → save.
            await ChainLock.WaitAsync(ct);
            try
            {
                row.PrevHash = await _db.AuditEvents
                    .OrderByDescending(a => a.Id)
                    .Select(a => a.RowHash)
                    .FirstOrDefaultAsync(ct);
                row.RowHash = ComputeRowHash(row);

                _db.AuditEvents.Add(row);
                await _db.SaveChangesAsync(ct);
                return row.Id;
            }
            finally
            {
                ChainLock.Release();
            }
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
            // Same chain serialisation as the single-insert path. We
            // compute each row's hash inside the lock, threading the
            // previous row's RowHash through the batch — so the chain
            // stays valid even when one POST inserts 50 rows at once.
            await ChainLock.WaitAsync(ct);
            try
            {
                var prev = await _db.AuditEvents
                    .OrderByDescending(a => a.Id)
                    .Select(a => a.RowHash)
                    .FirstOrDefaultAsync(ct);

                foreach (var r in rows)
                {
                    r.PrevHash = prev;
                    r.RowHash  = ComputeRowHash(r);
                    prev       = r.RowHash;
                }

                _db.AuditEvents.AddRange(rows);
                await _db.SaveChangesAsync(ct);
                return rows.Count;
            }
            finally
            {
                ChainLock.Release();
            }
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

    /// <summary>
    /// Compute the SHA-256 chain hash for an audit row. The serialised
    /// form is a fixed pipe-separated layout of every content field —
    /// keeping it stable matters because changing the layout in the future
    /// would invalidate every existing row's hash.
    ///
    /// <para>Order, exactly: PrevHash | Action | ActorUserId | ActorName |
    /// ActorEmail | ActorRole | TargetType | TargetId | PayloadJson |
    /// OccurredAtServer (ISO 8601) | DeviceKind | IpAddress.</para>
    /// </summary>
    private static string ComputeRowHash(AuditEvent row)
    {
        // Empty string instead of "null" so two adjacent nulls don't
        // collide with a literal "null" in any field.
        static string S(string? s) => s ?? "";

        var canonical = string.Join("|",
            S(row.PrevHash),
            S(row.Action),
            S(row.ActorUserId),
            S(row.ActorName),
            S(row.ActorEmail),
            S(row.ActorRole),
            S(row.TargetType),
            row.TargetId?.ToString() ?? "",
            S(row.PayloadJson),
            row.OccurredAtServer.ToUniversalTime().ToString("O"),
            S(row.DeviceKind),
            S(row.IpAddress));

        Span<byte> hash = stackalloc byte[32];
        var bytes = Encoding.UTF8.GetBytes(canonical);
        SHA256.HashData(bytes, hash);

        // Lowercase hex — 64 chars. Matches the PrevHash/RowHash column length.
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Walk the audit table in Id order and verify every row's RowHash
    /// matches the SHA-256 of (its PrevHash || its content fields). The
    /// first break is reported with the row Id and the recomputed-vs-stored
    /// hash pair so an admin can see exactly where (and ideally when) the
    /// chain was tampered with.
    /// </summary>
    public async Task<ChainVerificationResult> VerifyChainAsync(CancellationToken ct = default)
    {
        var rowsScanned   = 0;
        string? expectedPrev = null;

        // Pull only what we need to recompute the hash — heavy payloads
        // load on demand. The audit table is large, so we stream in pages.
        const int pageSize = 500;
        long lastId = 0;
        while (true)
        {
            var page = await _db.AuditEvents
                .AsNoTracking()
                .Where(a => a.Id > lastId)
                .OrderBy(a => a.Id)
                .Take(pageSize)
                .ToListAsync(ct);
            if (page.Count == 0) break;

            foreach (var row in page)
            {
                rowsScanned++;

                // Detect chain breaks: this row's PrevHash should equal
                // the previous row's RowHash.
                if (row.PrevHash != expectedPrev)
                {
                    return new ChainVerificationResult(
                        Valid:        false,
                        RowsScanned:  rowsScanned,
                        BreakAtRowId: row.Id,
                        Reason:       $"PrevHash mismatch: stored '{row.PrevHash}', expected '{expectedPrev}'.");
                }

                // Recompute this row's hash and compare with the stored one.
                var recomputed = ComputeRowHash(row);
                if (!string.Equals(recomputed, row.RowHash, StringComparison.OrdinalIgnoreCase))
                {
                    return new ChainVerificationResult(
                        Valid:        false,
                        RowsScanned:  rowsScanned,
                        BreakAtRowId: row.Id,
                        Reason:       $"RowHash mismatch: stored '{row.RowHash}', recomputed '{recomputed}' — row has been edited or a field's value changed.");
                }

                expectedPrev = row.RowHash;
                lastId       = row.Id;
            }
        }

        return new ChainVerificationResult(
            Valid:        true,
            RowsScanned:  rowsScanned,
            BreakAtRowId: null,
            Reason:       rowsScanned == 0 ? "Empty audit log." : "Chain intact across all rows.");
    }

    /// <summary>Outcome of <see cref="VerifyChainAsync"/>. Valid = true
    /// means every row in the table chains correctly to its predecessor
    /// and its own content hashes to its stored RowHash.</summary>
    public record ChainVerificationResult(
        bool   Valid,
        int    RowsScanned,
        long?  BreakAtRowId,
        string Reason);
}
