namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// HttpMessageHandler that wraps every API response and watches for the
/// server's <c>X-Auth-Failure: user_deactivated</c> header, which the
/// JwtBearer.OnTokenValidated event on the server attaches when the
/// authenticated user has been deactivated since their token was issued.
///
/// <para>When the header is seen, this handler triggers
/// <see cref="AuthService.HandleRemoteDeactivationAsync"/>, which:</para>
/// <list type="bullet">
///   <item><description>Marks the cached user as blocked so offline sign-in
///   also refuses them.</description></item>
///   <item><description>Wipes the JWT from SecureStorage.</description></item>
///   <item><description>Raises <see cref="AuthService.Deactivated"/> so the
///   layout can toast and navigate to /login.</description></item>
/// </list>
///
/// <para>Throttling: the handler keeps a <c>_seenDeactivation</c> flag so
/// when a single deactivation triggers a burst of in-flight requests we
/// only invoke the service once per response burst. The flag clears when
/// the next 2xx response comes back (i.e. the user successfully signed
/// back in OR was reactivated).</para>
/// </summary>
public class AuthFailureHandler : DelegatingHandler
{
    private readonly IServiceProvider _services;
    private bool _seenDeactivation;

    public AuthFailureHandler(IServiceProvider services)
    {
        // Service-locate AuthService at SendAsync time rather than via
        // constructor injection. AuthService → ApiClient → HttpClient
        // → AuthFailureHandler forms a graph that DI's eager validation
        // doesn't like; the lazy lookup sidesteps it cleanly.
        _services = services;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        try
        {
            if (response.IsSuccessStatusCode)
            {
                // A successful response means the user is reachable AND
                // authorised — reset the throttle so a future deactivation
                // gets caught fresh.
                _seenDeactivation = false;
            }
            else if (!_seenDeactivation
                  && response.Headers.TryGetValues("X-Auth-Failure", out var values)
                  && values.Any(v => string.Equals(v, "user_deactivated", StringComparison.OrdinalIgnoreCase)))
            {
                _seenDeactivation = true;

                // Fire-and-forget — we don't want to block the calling
                // page on the sign-out / event dispatch. The response is
                // still returned to the caller (with its 401 status), so
                // their existing "got 401, retry / show error" logic
                // continues to work.
                var auth = _services.GetService<AuthService>();
                if (auth != null)
                    _ = auth.HandleRemoteDeactivationAsync();
            }
        }
        catch
        {
            // Anything inside the throttle / dispatch block is best-effort.
            // A failure here MUST NOT mask the actual response — the caller
            // gets the response unchanged.
        }

        return response;
    }
}
