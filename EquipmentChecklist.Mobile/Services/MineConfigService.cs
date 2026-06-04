using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Holds the active <see cref="MineDto"/> for the whole app. Pages bind to
/// <see cref="Current"/> instead of hardcoding "Belfast Coal Mine" so a fresh
/// deployment to a different mine just needs <c>appsettings.json</c>'s
/// <c>Mine</c> section updated on the server.
///
/// <para>Lifecycle:</para>
/// <list type="number">
///   <item><description>On construction, hydrates from the persisted SQLite cache so the splash + first-page render uses real labels even before the first network call.</description></item>
///   <item><description>On <see cref="AuthService.SignedIn"/> (and at app start) calls <c>GET /api/sync/mine</c> and overwrites the cache.</description></item>
///   <item><description>Falls back to a hardcoded default that says "Mine" if both the cache and the network are empty — the app still works on a totally fresh device with no signal.</description></item>
/// </list>
/// </summary>
public class MineConfigService
{
    private readonly ApiClient   _api;
    private readonly LocalCache  _cache;
    private readonly AuthService _auth;

    public MineDto Current { get; private set; } = new()
    {
        Name           = "Pre-Checklist Mine",
        ShortName      = "MINE",
        Tagline        = "Pre-Shift Inspection Checklist",
        ComplianceText = ""
    };

    /// <summary>Raised after a successful refresh so layout / splash UI can
    /// rebind. Pages that bind directly via @inject usually don't need this
    /// — the next render picks up the new <see cref="Current"/> values.</summary>
    public event Action? Changed;

    public MineConfigService(ApiClient api, LocalCache cache, AuthService auth)
    {
        _api   = api;
        _cache = cache;
        _auth  = auth;

        _ = HydrateFromCacheAsync();
        _auth.SignedIn += OnSignedIn;

        // Don't wait for sign-in to refresh — the /mine endpoint is
        // anonymous, so we can pull it right after construction too.
        _ = RefreshAsync();
    }

    private void OnSignedIn() => _ = RefreshAsync();

    /// <summary>
    /// Pull <c>GET /api/sync/mine</c> and stash the result. Failures are
    /// swallowed — <see cref="Current"/> just keeps its previous value.
    /// </summary>
    public async Task RefreshAsync()
    {
        try
        {
            var fresh = await _api.MineConfigAsync();
            if (fresh is null) return;
            Current = fresh;
            try { await _cache.SaveMineConfigAsync(fresh); } catch { }
            Changed?.Invoke();
        }
        catch { /* offline — Current stays as cached / default */ }
    }

    private async Task HydrateFromCacheAsync()
    {
        try
        {
            var cached = await _cache.GetMineConfigAsync();
            if (cached is not null)
            {
                Current = cached;
                Changed?.Invoke();
            }
        }
        catch { /* first launch — leave the default in place */ }
    }
}
