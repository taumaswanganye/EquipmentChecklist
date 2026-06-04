using System.Text.Json;
using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Thin facade over <see cref="AuditQueue"/> — gives call sites a one-liner
/// instead of having to construct an <see cref="AuditEventDto"/> by hand
/// every time.
///
/// <para>Usage:</para>
/// <code>
/// await Audit.LogAsync(AuditActions.SubmissionCreated, "Submission",
///                     null, new { machineId = 42, status = "GO" });
/// </code>
///
/// <para>Errors are swallowed — audit failures must never interrupt the
/// surrounding domain action.</para>
/// </summary>
public class Audit
{
    private readonly AuditQueue   _queue;
    private readonly AuthService? _auth;

    public Audit(AuditQueue queue, AuthService? auth = null)
    {
        _queue = queue;
        _auth  = auth;

        // Wire the sign-in / sign-out auto-events here so call sites don't
        // need to remember to log them. We can't take a hard dep on AuthService
        // because the test/factory paths construct Audit without one — hence
        // the nullable param.
        if (_auth is not null)
        {
            _auth.SignedIn  += OnSignedIn;
            _auth.SignedOut += OnSignedOut;
        }
    }

    private void OnSignedIn()
    {
        // Whichever path triggered SignedIn — online password POST,
        // OfflineSignInAsync, biometric unlock — gets recorded as a generic
        // signin. Different SignedIn callers can refine by calling
        // LogAsync(UserSignedInOffline / UserBiometricUnlocked) themselves
        // before raising SignedIn if they want the more specific kind.
        _ = LogAsync(AuditActions.UserSignedIn, "User");
    }

    private void OnSignedOut()
    {
        _ = LogAsync(AuditActions.UserSignedOut, "User");
    }

    /// <summary>
    /// Queue one event. The <paramref name="payload"/> object is serialised
    /// to JSON automatically — pass an anonymous object with whatever extra
    /// detail will be useful when reading the trail later.
    /// </summary>
    public Task LogAsync(string action,
                         string? targetType = null,
                         long?   targetId   = null,
                         object? payload    = null)
    {
        string? json = null;
        if (payload is not null)
        {
            try { json = JsonSerializer.Serialize(payload); }
            catch { /* keep the event even if payload serialisation fails */ }
        }

        return _queue.EnqueueAsync(new AuditEventDto
        {
            Action           = action,
            TargetType       = targetType,
            TargetId         = targetId,
            OccurredAtClient = DateTime.UtcNow,
            PayloadJson      = json
        });
    }
}
