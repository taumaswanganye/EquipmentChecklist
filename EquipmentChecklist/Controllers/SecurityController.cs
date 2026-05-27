using System.Security.Cryptography;
using System.Text;
using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Fido2NetLib;
using Fido2NetLib.Development;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Controllers;

/// <summary>
/// Biometric / passwordless login (WebAuthn) + offline voucher issuance.
///
/// Phase 2: real registration + assertion. Online-only sign-in works end-to-end.
/// Phase 3: the offline voucher is issued on every successful assertion so the
/// browser's Service Worker (Phase 4) can use it to authorise the user offline.
///
/// All write endpoints are scoped to the Operator role per the agreed rollout.
/// </summary>
public class SecurityController : Controller
{
    private const string AttestationOptionsKey = "fido2.attestation_options";
    private const string AssertionOptionsKey   = "fido2.assertion_options";

    private readonly ApplicationDbContext           _db;
    private readonly UserManager<ApplicationUser>   _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly IFido2                         _fido2;
    private readonly OfflineVoucherService          _vouchers;
    private readonly ILogger<SecurityController>    _log;

    public SecurityController(ApplicationDbContext db,
                              UserManager<ApplicationUser> users,
                              SignInManager<ApplicationUser> signIn,
                              IFido2 fido2,
                              OfflineVoucherService vouchers,
                              ILogger<SecurityController> log)
    {
        _db       = db;
        _users    = users;
        _signIn   = signIn;
        _fido2    = fido2;
        _vouchers = vouchers;
        _log      = log;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                        PUBLIC ENDPOINTS (no auth)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>JWK the Service Worker pins to verify offline vouchers locally.</summary>
    [HttpGet("/api/v1/auth-pubkey")]
    [AllowAnonymous]
    [ResponseCache(Duration = 86400, Location = ResponseCacheLocation.Any)]
    public IActionResult AuthPublicKey() => Json(_vouchers.GetPublicJwk());

    // ══════════════════════════════════════════════════════════════════════════
    //                       ENROLLMENT DASHBOARD (UI)
    // ══════════════════════════════════════════════════════════════════════════

    [HttpGet("/Security")]
    [Authorize(Roles = "Operator,Admin")]
    public async Task<IActionResult> Index()
    {
        var userId = _users.GetUserId(User)!;
        var creds  = await _db.UserCredentials
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync();
        return View(creds);
    }

    [HttpPost("/Security/RevokeCredential")]
    [Authorize(Roles = "Operator,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeCredential(int id)
    {
        var userId = _users.GetUserId(User)!;
        var cred = await _db.UserCredentials
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (cred == null)
        {
            TempData["Error"] = "Credential not found.";
            return RedirectToAction(nameof(Index));
        }
        cred.IsActive = false;
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Device '{cred.DeviceLabel ?? "unnamed"}' revoked.";
        return RedirectToAction(nameof(Index));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                  WEBAUTHN REGISTRATION (operator + admin)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Issues the browser a CredentialCreateOptions challenge.</summary>
    [HttpPost("/Security/RegisterStart")]
    [Authorize(Roles = "Operator,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterStart()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var fidoUser = new Fido2User
        {
            Id          = Encoding.UTF8.GetBytes(user.Id),
            Name        = user.Email ?? user.UserName ?? user.Id,
            DisplayName = user.FullName
        };

        // Exclude credentials this user has already registered so the browser
        // refuses to register the same authenticator twice.
        var existingCreds = await _db.UserCredentials
            .Where(c => c.UserId == user.Id && c.IsActive)
            .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
            .ToListAsync();

        var authenticatorSelection = new AuthenticatorSelection
        {
            RequireResidentKey = false,
            UserVerification   = UserVerificationRequirement.Required
        };

        var options = _fido2.RequestNewCredential(
            fidoUser, existingCreds, authenticatorSelection,
            AttestationConveyancePreference.None);

        HttpContext.Session.SetString(AttestationOptionsKey, options.ToJson());
        return Json(options);
    }

    public class CompleteRegistrationRequest
    {
        public AuthenticatorAttestationRawResponse AttestationResponse { get; set; } = null!;
        public string? DeviceLabel { get; set; }
    }

    /// <summary>Verifies the attestation, persists the credential.</summary>
    [HttpPost("/Security/RegisterComplete")]
    [Authorize(Roles = "Operator,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterComplete([FromBody] CompleteRegistrationRequest req)
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var optionsJson = HttpContext.Session.GetString(AttestationOptionsKey);
        if (string.IsNullOrEmpty(optionsJson))
            return BadRequest(new { error = "Registration session expired. Start again." });

        var options = CredentialCreateOptions.FromJson(optionsJson);

        // Callback Fido2 uses to ensure we're not re-registering a credential
        // already owned by a different user.
        IsCredentialIdUniqueToUserAsyncDelegate isUnique = async (args, _) =>
        {
            return !await _db.UserCredentials
                .AnyAsync(c => c.CredentialId == args.CredentialId);
        };

        try
        {
            var result = await _fido2.MakeNewCredentialAsync(
                req.AttestationResponse, options, isUnique);

            var label = string.IsNullOrWhiteSpace(req.DeviceLabel)
                ? "Unnamed device"
                : req.DeviceLabel.Trim();
            if (label.Length > 80) label = label.Substring(0, 80);

            _db.UserCredentials.Add(new UserCredential
            {
                UserId       = user.Id,
                CredentialId = result.Result.CredentialId,
                PublicKey    = result.Result.PublicKey,
                SignCount    = result.Result.Counter,
                AaGuid       = result.Result.Aaguid,
                DeviceLabel  = label,
                IsActive     = true,
                CreatedAt    = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();

            HttpContext.Session.Remove(AttestationOptionsKey);
            return Json(new { ok = true });
        }
        catch (Fido2VerificationException ex)
        {
            _log.LogWarning(ex, "Fido2 registration failed for user {UserId}", user.Id);
            return BadRequest(new { error = ex.Message });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                  WEBAUTHN ASSERTION (anonymous = login)
    // ══════════════════════════════════════════════════════════════════════════

    public class AssertStartRequest { public string? Email { get; set; } }

    /// <summary>
    /// Returns AssertionOptions for a sign-in attempt. If `email` is provided,
    /// scopes allowedCredentials to that user; otherwise issues a discoverable
    /// (usernameless) challenge.
    /// </summary>
    [HttpPost("/Security/AssertStart")]
    [AllowAnonymous]
    public async Task<IActionResult> AssertStart([FromBody] AssertStartRequest req)
    {
        var allowed = new List<PublicKeyCredentialDescriptor>();
        if (!string.IsNullOrWhiteSpace(req?.Email))
        {
            var user = await _users.FindByEmailAsync(req.Email.Trim());
            if (user != null)
            {
                allowed = await _db.UserCredentials
                    .Where(c => c.UserId == user.Id && c.IsActive)
                    .Select(c => new PublicKeyCredentialDescriptor(c.CredentialId))
                    .ToListAsync();
            }
        }

        var options = _fido2.GetAssertionOptions(
            allowed, UserVerificationRequirement.Required);

        HttpContext.Session.SetString(AssertionOptionsKey, options.ToJson());
        return Json(options);
    }

    public class CompleteAssertionRequest
    {
        public AuthenticatorAssertionRawResponse AssertionResponse { get; set; } = null!;
    }

    /// <summary>Verifies the assertion, signs in the user, hands back a voucher.</summary>
    [HttpPost("/Security/AssertComplete")]
    [AllowAnonymous]
    public async Task<IActionResult> AssertComplete([FromBody] CompleteAssertionRequest req)
    {
        var optionsJson = HttpContext.Session.GetString(AssertionOptionsKey);
        if (string.IsNullOrEmpty(optionsJson))
            return BadRequest(new { error = "Assertion session expired. Start again." });

        var options = AssertionOptions.FromJson(optionsJson);

        // Look up the credential by id.
        var credId = req.AssertionResponse.Id;
        var cred = await _db.UserCredentials
            .Include(c => c.User)
            .FirstOrDefaultAsync(c => c.CredentialId == credId && c.IsActive);
        if (cred == null)
            return BadRequest(new { error = "Unknown credential." });

        IsUserHandleOwnerOfCredentialIdAsync isOwner = async (args, _) =>
        {
            var c = await _db.UserCredentials
                .FirstOrDefaultAsync(x => x.CredentialId == args.CredentialId);
            if (c == null) return false;
            return Encoding.UTF8.GetBytes(c.UserId).SequenceEqual(args.UserHandle);
        };

        try
        {
            // Fido2NetLib 3.x signature:
            //   MakeAssertionAsync(response, options, storedPublicKey,
            //                      storedSignatureCounter, isUserHandleOwnerCallback,
            //                      requestTokenBindingId = null, CancellationToken = default)
            var result = await _fido2.MakeAssertionAsync(
                req.AssertionResponse,
                options,
                cred.PublicKey,
                cred.SignCount,
                isOwner);

            cred.SignCount  = result.Counter;
            cred.LastUsedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch (Fido2VerificationException ex)
        {
            _log.LogWarning(ex, "Fido2 assertion failed for credentialId {CredId}", credId);
            return BadRequest(new { error = ex.Message });
        }

        // Sign in via the Identity cookie pipeline.
        await _signIn.SignInAsync(cred.User, isPersistent: true);

        // Issue an offline voucher so the SW can cache it for offline use.
        var roles    = await _users.GetRolesAsync(cred.User);
        var nowUnix  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var voucher  = _vouchers.IssueVoucher(new VoucherPayload
        {
            Sub      = cred.User.Id,
            Name     = cred.User.FullName,
            Email    = cred.User.Email ?? "",
            Roles    = roles.ToArray(),
            DeviceId = cred.Id,
            PinHash  = cred.PinHash,
            Iat      = nowUnix,
            Exp      = nowUnix + (long)OfflineVoucherService.VoucherLifetime.TotalSeconds
        });

        HttpContext.Session.Remove(AssertionOptionsKey);
        return Json(new
        {
            ok          = true,
            voucher,
            expires_at  = nowUnix + (long)OfflineVoucherService.VoucherLifetime.TotalSeconds,
            redirect    = "/Checklist"
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                        OFFLINE PIN FALLBACK
    // ══════════════════════════════════════════════════════════════════════════

    public class SetPinRequest
    {
        public int CredentialId { get; set; }
        public string Pin { get; set; } = "";
    }

    [HttpPost("/Security/SetPin")]
    [Authorize(Roles = "Operator,Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPin([FromBody] SetPinRequest req)
    {
        var userId = _users.GetUserId(User)!;
        var pin    = (req.Pin ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(pin, @"^\d{4,6}$"))
            return BadRequest(new { error = "PIN must be 4 to 6 digits." });

        var cred = await _db.UserCredentials
            .FirstOrDefaultAsync(c => c.Id == req.CredentialId && c.UserId == userId);
        if (cred == null)
            return NotFound(new { error = "Credential not found." });

        cred.PinHash = HashPin(pin);
        await _db.SaveChangesAsync();
        return Json(new { ok = true });
    }

    /// <summary>
    /// PBKDF2(SHA-256, 100k iter, 16-byte salt). Stored as
    /// "pbkdf2$100000$<base64(salt)>$<base64(hash)>" so the SW can re-derive
    /// and compare offline without needing the raw PIN.
    /// </summary>
    private static string HashPin(string pin)
    {
        const int iterations = 100_000;
        var salt   = RandomNumberGenerator.GetBytes(16);
        using var derive = new Rfc2898DeriveBytes(pin, salt, iterations, HashAlgorithmName.SHA256);
        var hash   = derive.GetBytes(32);
        return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                         OFFLINE VOUCHER ISSUANCE
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Refreshes the operator's voucher. Called by the Service Worker whenever
    /// the device gets back online so the cached voucher never expires under
    /// normal use.
    /// </summary>
    [HttpGet("/api/v1/offline-voucher")]
    [Authorize(Roles = "Operator,Admin")]
    public async Task<IActionResult> IssueOfflineVoucher()
    {
        var user = await _users.GetUserAsync(User);
        if (user == null) return Unauthorized();

        var cred = await _db.UserCredentials
            .Where(c => c.UserId == user.Id && c.IsActive)
            .OrderByDescending(c => c.LastUsedAt ?? c.CreatedAt)
            .FirstOrDefaultAsync();
        if (cred == null)
            return BadRequest(new { error = "No registered device. Enroll a credential first." });

        var roles   = await _users.GetRolesAsync(user);
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var voucher = _vouchers.IssueVoucher(new VoucherPayload
        {
            Sub      = user.Id,
            Name     = user.FullName,
            Email    = user.Email ?? "",
            Roles    = roles.ToArray(),
            DeviceId = cred.Id,
            PinHash  = cred.PinHash,
            Iat      = nowUnix,
            Exp      = nowUnix + (long)OfflineVoucherService.VoucherLifetime.TotalSeconds
        });
        return Json(new
        {
            voucher,
            expires_at = nowUnix + (long)OfflineVoucherService.VoucherLifetime.TotalSeconds
        });
    }
}
