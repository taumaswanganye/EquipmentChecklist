using Fido2NetLib;
using Microsoft.Extensions.Options;

namespace EquipmentChecklist.Services;

/// <summary>
/// Layers DB-backed values on top of whatever <c>AddFido2</c> set from
/// <c>appsettings.json</c>. Runs the first time <c>Fido2Configuration</c>
/// is resolved by the DI container — after build, so the
/// <see cref="ConfigurationService"/> is alive and can read from the DB.
///
/// <para>What gets overridden:</para>
/// <list type="bullet">
///   <item><description><c>ServerDomain</c> — the relying-party domain that
///   WebAuthn assertions are validated against.</description></item>
///   <item><description><c>ServerName</c> — the friendly RP name shown in
///   the operator's biometric-enrolment dialog.</description></item>
///   <item><description><c>Origins</c> — the allowed origins for WebAuthn
///   assertions. Stored as a semicolon-separated string in the DB so it
///   serialises into the AppSetting.Value column cleanly.</description></item>
/// </list>
///
/// <para>If any DB read returns null/empty the existing value (set at boot
/// from <c>appsettings.json</c>) is preserved as the fallback.</para>
/// </summary>
public class DbFido2PostConfigure : IPostConfigureOptions<Fido2Configuration>
{
    private readonly ConfigurationService _config;

    public DbFido2PostConfigure(ConfigurationService config)
    {
        _config = config;
    }

    public void PostConfigure(string? name, Fido2Configuration options)
    {
        // PostConfigure can't be async; we call .Result on the lookup.
        // This is acceptable because ConfigurationService caches all
        // reads in memory after the first DB hit, so subsequent options
        // resolutions are sync-fast and don't block a request thread.
        var serverDomain = _config.GetAsync("Fido2.ServerDomain").GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(serverDomain))
            options.ServerDomain = serverDomain;

        var serverName = _config.GetAsync("Fido2.ServerName").GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(serverName))
            options.ServerName = serverName;

        var origins = _config.GetAsync("Fido2.Origins").GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(origins))
        {
            // Semicolon-separated in the DB so it round-trips through an
            // <input type="text"> cleanly. Split + trim + drop empties.
            options.Origins = new HashSet<string>(
                origins.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
    }
}
