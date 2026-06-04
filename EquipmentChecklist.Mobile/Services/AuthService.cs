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

    public Task SignOutAsync()
    {
        SecureStorage.Default.RemoveAll();
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
}
