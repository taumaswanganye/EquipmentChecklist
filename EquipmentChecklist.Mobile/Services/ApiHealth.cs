namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Tracks whether the API is currently reachable.
///
/// <para>Distinct from <c>Connectivity.NetworkAccess</c> because the device can
/// be on Wi-Fi while the server is unreachable (server down, VPN dropped,
/// behind a captive portal, DNS failure). The status pill in the topbar
/// should reflect "can I actually talk to the API?" not just "is Wi-Fi on?".</para>
///
/// <para>How it works:</para>
/// <list type="bullet">
///   <item><description>If the device has no internet at all, <see cref="IsOnline"/> is false — no point pinging.</description></item>
///   <item><description>Otherwise we fire <c>ApiClient.PingAsync()</c> periodically and on connectivity events.</description></item>
///   <item><description>Listeners subscribe to <see cref="Changed"/> for UI redraws when the flag flips.</description></item>
/// </list>
///
/// <para>One singleton per app. <c>MauiProgram</c> force-resolves it after
/// <c>builder.Build()</c> so the timer starts before any page renders.</para>
/// </summary>
public class ApiHealth : IDisposable
{
    private readonly ApiClient   _api;
    private readonly AuthService _auth;
    private readonly LocalCache  _cache;
    private readonly Timer       _timer;

    /// <summary>How often we re-check the server when the device has internet.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(20);

    /// <summary>How often we ALSO pull /me to refresh the cached user's
    /// profile + competencies. Far less aggressive than the ping interval
    /// because the data changes infrequently (admin adds a cert) AND we
    /// don't want every ping to authenticate. Five minutes is a sensible
    /// balance — adds at most 5 minutes of staleness to "admin granted my
    /// competency, when does my phone notice."</summary>
    private static readonly TimeSpan MeRefreshInterval = TimeSpan.FromMinutes(5);

    private DateTime _lastMeRefresh = DateTime.MinValue;

    /// <summary>Initial state is <c>false</c> until the first ping succeeds.</summary>
    private bool _apiReachable;
    private bool _disposed;

    /// <summary>Fires when <see cref="IsOnline"/> transitions in either direction.</summary>
    public event Action? Changed;

    /// <summary>
    /// True only when BOTH the device claims to have internet AND the most
    /// recent server ping returned 2xx. This matches what the user means by
    /// "the app can reach the API".
    /// </summary>
    public bool IsOnline =>
        Connectivity.NetworkAccess == NetworkAccess.Internet && _apiReachable;

    public ApiHealth(ApiClient api, AuthService auth, LocalCache cache)
    {
        _api   = api;
        _auth  = auth;
        _cache = cache;

        // When the device flips Wi-Fi/data state, run a fresh ping immediately
        // — don't wait for the next poll window. Users notice the pill react.
        Connectivity.ConnectivityChanged += OnConnectivityChanged;

        // Initial check + recurring poll. First tick fires on startup so the
        // pill becomes accurate within a few seconds of app launch.
        _timer = new Timer(_ => _ = CheckAsync(),
                           state:    null,
                           dueTime:  TimeSpan.FromSeconds(1),
                           period:   PollInterval);
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        // If we just lost internet entirely, no need to ping — flip immediately.
        if (e.NetworkAccess != NetworkAccess.Internet)
        {
            if (_apiReachable)
            {
                _apiReachable = false;
                Changed?.Invoke();
            }
            return;
        }
        // Internet came back — ping right now to react quickly.
        _ = CheckAsync();
    }

    /// <summary>
    /// Run one ping pass. Public so pages can force a refresh (e.g. the user
    /// taps a "retry" button on a failed action).
    /// </summary>
    public async Task CheckAsync()
    {
        if (_disposed) return;

        var was = _apiReachable;

        if (Connectivity.NetworkAccess != NetworkAccess.Internet)
        {
            _apiReachable = false;
        }
        else
        {
            // ApiClient.PingAsync swallows its own exceptions and returns bool,
            // so we don't need try/catch here.
            _apiReachable = await _api.PingAsync();

            // On a successful ping, stamp "last seen the server" on the cached
            // user record. The MainLayout staleness banner reads this to
            // decide when to warn the operator that they're working off
            // increasingly old data and should reconnect.
            if (_apiReachable)
            {
                var email = _auth.CurrentUser?.Email;
                if (!string.IsNullOrEmpty(email))
                {
                    try { await _cache.BumpLastSyncedAsync(email); }
                    catch { /* cache write failures shouldn't kill the health loop */ }

                    // Throttled /me refresh — picks up competencies added or
                    // revoked by the admin since this operator's last
                    // sign-in. Without it the only way for a freshly-granted
                    // competency to reach the phone would be a sign-out +
                    // sign-in, which operators don't do mid-shift.
                    if (DateTime.UtcNow - _lastMeRefresh >= MeRefreshInterval &&
                        _auth.IsSignedIn)
                    {
                        try
                        {
                            var me = await _api.MeAsync();
                            if (me != null && !string.IsNullOrEmpty(me.Email))
                            {
                                await _cache.UpdateCompetenciesAsync(me.Email, me.Competencies);
                                _lastMeRefresh = DateTime.UtcNow;
                            }
                        }
                        catch
                        {
                            // /me can fail for many reasons (token expiry,
                            // server hiccup). Don't push _lastMeRefresh on
                            // failure — that way we retry on the very next
                            // tick instead of waiting another 5 min.
                        }
                    }
                }
            }
        }

        if (was != _apiReachable) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Connectivity.ConnectivityChanged -= OnConnectivityChanged;
        _timer.Dispose();
        GC.SuppressFinalize(this);
    }
}
