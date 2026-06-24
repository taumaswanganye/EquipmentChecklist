using System.Text.Json;
using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Two-track auth:
///
///   ── Online (first time / re-sync) ──
///     POST /api/sync/login → JWT.
///     JWT + user profile saved to SecureStorage.
///     A PBKDF2 hash of the password is also written to <see cref="LocalCache"/>
///     so subsequent offline sign-ins on the same device can verify locally.
///
///   ── Offline (no network) ──
///     <see cref="OfflineSignInAsync"/> looks up the cached user by email and
///     verifies their password against the PBKDF2 hash. On success, the
///     in-memory CurrentUser is hydrated from cached data.
/// </summary>
public class AuthService
{
    private const string TOKEN_KEY = "eq_jwt";
    private const string EXP_KEY   = "eq_jwt_exp";          // unix seconds
    private const string USER_KEY  = "eq_user_json";
    private const string BIO_KEY   = "eq_biometric_enabled"; // "1" / absent

    private readonly BiometricUnlock _biometric;
    private readonly LocalCache      _cache;

    public SyncUserDto? CurrentUser { get; private set; }
    public bool         IsUnlocked  { get; private set; }
    public event Action? AuthChanged;

    /// <summary>Fires the moment a successful sign-in finishes (online or
    /// offline). NotificationService uses this to kick the SignalR connection.</summary>
    public event Action? SignedIn;
    /// <summary>Fires on explicit sign-out. NotificationService stops the
    /// SignalR connection so it doesn't reconnect with a stale token.</summary>
    public event Action? SignedOut;

    /// <summary>
    /// Fires once when the server confirms the current user has been
    /// deactivated by an admin. The MainLayout subscribes to this to
    /// show a "your account has been deactivated" toast and bounce the
    /// app back to /login. The cached credentials are already cleared
    /// by the time this fires, so an offline-sign-in retry will also
    /// fail with BlockedByAdmin.
    /// </summary>
    public event Action? Deactivated;

    public bool IsSignedIn => CurrentUser != null && IsUnlocked;

    public AuthService(BiometricUnlock biometric, LocalCache cache)
    {
        _biometric = biometric;
        _cache     = cache;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                                RESTORE
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<RestoreResult> RestoreAsync()
    {
        IsUnlocked = false;

        var userJson = await SecureStorage.Default.GetAsync(USER_KEY);
        if (string.IsNullOrEmpty(userJson))
        {
            CurrentUser = null;
            return RestoreResult.NoSession;
        }

        try
        {
            CurrentUser = JsonSerializer.Deserialize<SyncUserDto>(userJson);
            if (CurrentUser == null)
            {
                await SignOutAsync();
                return RestoreResult.Corrupt;
            }
        }
        catch
        {
            await SignOutAsync();
            return RestoreResult.Corrupt;
        }

        if (await IsBiometricEnabledAsync())
        {
            var name   = CurrentUser.FullName ?? "your account";
            var prompt = await _biometric.PromptAsync($"Confirm to continue as {name}");
            switch (prompt)
            {
                case BiometricResult.Success:
                    IsUnlocked = true;
                    AuthChanged?.Invoke();
                    return RestoreResult.Unlocked;

                case BiometricResult.Cancelled:
                    return RestoreResult.BiometricCancelled;

                case BiometricResult.NotAvailable:
                    await DisableBiometricsAsync();
                    IsUnlocked = true;
                    AuthChanged?.Invoke();
                    return RestoreResult.Unlocked;

                default:
                    await SignOutAsync();
                    return RestoreResult.BiometricFailed;
            }
        }

        IsUnlocked = true;
        AuthChanged?.Invoke();
        return RestoreResult.Unlocked;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                            GET / SIGN IN / OUT
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<string?> GetTokenAsync()
    {
        if (!IsUnlocked) return null;
        var token = await SecureStorage.Default.GetAsync(TOKEN_KEY);
        if (string.IsNullOrEmpty(token)) return null;
        if (await IsTokenExpiredAsync()) return null;
        return token;
    }

    /// <summary>Persist a successful ONLINE sign-in. Stashes a PBKDF2 hash of the
    /// password in LocalCache so the next offline sign-in works.</summary>
    public async Task SignInAsync(SyncLoginResponse login, string password, bool enableBiometrics = false)
    {
        await SecureStorage.Default.SetAsync(TOKEN_KEY, login.Token);
        await SecureStorage.Default.SetAsync(
            EXP_KEY,
            new DateTimeOffset(login.ExpiresAt).ToUnixTimeSeconds().ToString());
        await SecureStorage.Default.SetAsync(USER_KEY, JsonSerializer.Serialize(login.User));

        try { await _cache.SaveUserAsync(login.User, password, login.Token, login.ExpiresAt); }
        catch { /* cache failure shouldn't block sign-in */ }

        if (enableBiometrics && await _biometric.IsAvailableAsync())
            await EnableBiometricsAsync();

        CurrentUser = login.User;
        IsUnlocked  = true;
        AuthChanged?.Invoke();
        SignedIn?.Invoke();
    }

    /// <summary>Verify password against the LocalCache PBKDF2 hash; rehydrate
    /// CurrentUser from cached data. Used when the server is unreachable.</summary>
    public async Task<OfflineSignInResult> OfflineSignInAsync(string email, string password)
    {
        var user = await _cache.FindUserAsync(email);
        if (user == null) return OfflineSignInResult.UserUnknown;

        // ── Block-flag check ─────────────────────────────────────────────
        // The server set this on a previous online interaction (login
        // returned "user_deactivated" OR an authenticated call returned
        // the X-Auth-Failure: user_deactivated header). Until the user
        // reconnects with a successful sign-in (which clears the flag),
        // offline credentials are refused. We check BEFORE the password
        // verify so a deactivated user doesn't even get the satisfaction
        // of a "right password but blocked" — they get "blocked" full stop.
        if (user.IsBlocked)
            return OfflineSignInResult.BlockedByAdmin;

        if (!LocalCache.VerifyPassword(password, user.PasswordHash))
            return OfflineSignInResult.WrongPassword;

        string[] roles;
        try { roles = JsonSerializer.Deserialize<string[]>(user.RolesJson) ?? Array.Empty<string>(); }
        catch { roles = Array.Empty<string>(); }

        var profile = new SyncUserDto
        {
            Id             = user.UserId,
            Email          = user.Email,
            FullName       = user.FullName,
            EmployeeNumber = user.EmployeeNumber,
            Roles          = roles
        };

        await SecureStorage.Default.SetAsync(USER_KEY, JsonSerializer.Serialize(profile));

        if (!string.IsNullOrEmpty(user.JwtToken) && user.JwtExpiresAt.HasValue
            && user.JwtExpiresAt.Value > DateTime.UtcNow)
        {
            await SecureStorage.Default.SetAsync(TOKEN_KEY, user.JwtToken);
            await SecureStorage.Default.SetAsync(
                EXP_KEY,
                new DateTimeOffset(user.JwtExpiresAt.Value).ToUnixTimeSeconds().ToString());
        }

        CurrentUser = profile;
        IsUnlocked  = true;
        AuthChanged?.Invoke();
        SignedIn?.Invoke();
        return OfflineSignInResult.Success;
    }

    /// <summary>
    /// Called when the server tells us the current user has been
    /// deactivated (either via the login response's "user_deactivated"
    /// code, or the X-Auth-Failure: user_deactivated response header on
    /// any authenticated call — see AuthFailureHandler).
    ///
    /// <para>This does three things in order:</para>
    /// <list type="number">
    ///   <item><description>Marks the cached user as blocked so a
    ///   subsequent offline sign-in also fails.</description></item>
    ///   <item><description>Wipes the JWT from SecureStorage so no
    ///   stale-token API calls go out.</description></item>
    ///   <item><description>Raises the <see cref="Deactivated"/> event
    ///   so MainLayout can toast + navigate to /login.</description></item>
    /// </list>
    ///
    /// <para>Idempotent — calling it multiple times with the same email
    /// is harmless. ApiClient's failure handler triggers it once per
    /// detected response so a burst of in-flight requests doesn't fire
    /// the event a dozen times.</para>
    /// </summary>
    public async Task HandleRemoteDeactivationAsync(string? email = null)
    {
        // Prefer the explicit email arg (from the login response body)
        // and fall back to CurrentUser when this is triggered mid-session
        // by a 401/403 on an already-authenticated request.
        var target = !string.IsNullOrWhiteSpace(email) ? email : CurrentUser?.Email;
        if (!string.IsNullOrWhiteSpace(target))
        {
            try { await _cache.MarkUserBlockedAsync(target!); } catch { /* non-fatal */ }
        }

        await SignOutAsync();
        Deactivated?.Invoke();
    }

    public Task SignOutAsync()
    {
        // Clear session credentials but PRESERVE the biometric preference
        // flag — biometric enrollment is per-device-per-account, not per-
        // session. Wiping it via RemoveAll() means a user signed out by a
        // background flow (deactivation handler, JWT expiry, device-gate
        // rejection) silently loses their biometric opt-in and has to re-
        // tick "Use biometrics next time" on every sign-in. That was the
        // root cause of the "no prompt appears" regression.
        //
        // Anything new that lands in SecureStorage and SHOULD be wiped on
        // sign-out must be added to this explicit list. Anything that
        // should survive sign-out (per-device preferences) just gets left
        // alone — opt-in by omission.
        SecureStorage.Default.Remove(TOKEN_KEY);
        SecureStorage.Default.Remove(EXP_KEY);
        SecureStorage.Default.Remove(USER_KEY);
        CurrentUser = null;
        IsUnlocked  = false;
        AuthChanged?.Invoke();
        SignedOut?.Invoke();
        return Task.CompletedTask;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                             BIOMETRIC FLAG
    // ═════════════════════════════════════════════════════════════════════════

    public async Task<bool> IsBiometricEnabledAsync()
        => (await SecureStorage.Default.GetAsync(BIO_KEY)) == "1";

    /// <summary>
    /// True iff there's a persisted user record in SecureStorage AND the
    /// biometric opt-in flag is set — i.e. the explicit "🔐 Sign in with
    /// biometric" button on Login has something it can actually unlock.
    /// If one of these is true but not the other (e.g. an old install
    /// wiped USER_KEY but left BIO_KEY, or vice-versa), this returns
    /// false AND clears the stale BIO_KEY so the inconsistent state
    /// auto-heals on next launch.
    /// </summary>
    public async Task<bool> HasBiometricUnlockableSessionAsync()
    {
        var bioFlag = (await SecureStorage.Default.GetAsync(BIO_KEY)) == "1";
        var userJson = await SecureStorage.Default.GetAsync(USER_KEY);
        var hasUser  = !string.IsNullOrEmpty(userJson);

        if (bioFlag && !hasUser)
        {
            // Stale BIO_KEY without a session to unlock — wipe it so
            // future RefreshBiometricSessionStateAsync calls return false
            // immediately and the explicit button stops appearing in this
            // dead-end state. The user signs in with password, ticks the
            // checkbox, and the flag comes back consistent.
            SecureStorage.Default.Remove(BIO_KEY);
            return false;
        }

        return bioFlag && hasUser;
    }

    public async Task EnableBiometricsAsync()
        => await SecureStorage.Default.SetAsync(BIO_KEY, "1");

    public Task DisableBiometricsAsync()
    {
        SecureStorage.Default.Remove(BIO_KEY);
        return Task.CompletedTask;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private async Task<bool> IsTokenExpiredAsync()
    {
        var expS = await SecureStorage.Default.GetAsync(EXP_KEY);
        if (!long.TryParse(expS, out var unix)) return true;
        return unix < DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}

public enum RestoreResult
{
    NoSession,
    Corrupt,
    Expired,
    Unlocked,
    BiometricCancelled,
    BiometricFailed,
}

public enum OfflineSignInResult
{
    Success,
    /// <summary>No cached profile for this email — first sign-in must be online.</summary>
    UserUnknown,
    WrongPassword,
    /// <summary>The cached user is flagged IsBlocked — admin deactivated them
    /// while online and we haven't been online since. Offline sign-in stays
    /// refused until the user reconnects and an admin reactivates.</summary>
    BlockedByAdmin,
}
