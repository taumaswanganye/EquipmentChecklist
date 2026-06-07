using System.Collections.Concurrent;
using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

/// <summary>
/// DB-backed configuration store with an in-memory cache.
///
/// <para>Reads first try the cache, then the DB, then fall back to the
/// host <see cref="IConfiguration"/> (= <c>appsettings.json</c>). The
/// fallback is what keeps bootstrap-essential keys (ConnectionStrings,
/// Jwt:Key) working when the DB row doesn't exist OR when the DB hasn't
/// been seeded yet.</para>
///
/// <para>Writes go to the DB AND invalidate the in-memory cache so the
/// next read sees the change immediately. Other server instances will
/// pick the change up on the next cache miss for that key — for a
/// single-instance mine deployment this is fine, for a multi-node
/// deployment add a periodic refresh or a pub/sub invalidation.</para>
///
/// <para>Singleton lifetime — the cache survives request boundaries. The
/// DB context is resolved per-call from <see cref="IServiceProvider"/>
/// because <see cref="ApplicationDbContext"/> is scoped.</para>
/// </summary>
public class ConfigurationService
{
    private readonly IServiceProvider _services;
    private readonly IConfiguration   _fallback;
    private readonly ILogger<ConfigurationService> _log;

    /// <summary>Thread-safe key → value cache. Null sentinel for keys that
    /// were looked up and found absent in both the DB and the fallback —
    /// avoids hammering the DB on a missing-key read loop.</summary>
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurationService(IServiceProvider services,
                                IConfiguration   fallback,
                                ILogger<ConfigurationService> log)
    {
        _services = services;
        _fallback = fallback;
        _log      = log;
    }

    /// <summary>
    /// Resolve a key. Returns the DB value when present, else the
    /// IConfiguration value (with dotted notation translated to colons —
    /// <c>Mine.Name</c> → <c>Mine:Name</c>), else null.
    /// </summary>
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out var hit))
            return hit;

        string? value = null;

        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.AppSettings
                .Where(s => s.Key == key)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(ct);
            value = row;
        }
        catch (Exception ex)
        {
            // DB read failures shouldn't break the request — fall through
            // to the host config. Most likely cause is the table not
            // existing yet on a brand-new deploy.
            _log.LogWarning(ex, "ConfigurationService DB read failed for key {Key}", key);
        }

        // Fallback: IConfiguration uses ':' as separator; expose the same
        // keys with dotted notation so consumers can use either spelling.
        if (value == null)
        {
            var cfgKey = key.Replace('.', ':');
            value = _fallback[cfgKey];
        }

        _cache[key] = value;
        return value;
    }

    /// <summary>Typed shorthand for an int (port numbers etc.).</summary>
    public async Task<int> GetIntAsync(string key, int fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);
        return int.TryParse(raw, out var n) ? n : fallback;
    }

    /// <summary>Typed shorthand for a bool. Accepts "true"/"false"/"1"/"0".</summary>
    public async Task<bool> GetBoolAsync(string key, bool fallback, CancellationToken ct = default)
    {
        var raw = await GetAsync(key, ct);
        if (string.IsNullOrEmpty(raw)) return fallback;
        if (bool.TryParse(raw, out var b)) return b;
        if (raw.Trim() == "1") return true;
        if (raw.Trim() == "0") return false;
        return fallback;
    }

    /// <summary>
    /// Update one setting. Inserts the row if it doesn't exist yet. Wipes
    /// the cache entry so the next read sees the new value.
    /// </summary>
    public async Task SetAsync(string key, string? value, string? actorUserId = null, CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTime.UtcNow;
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row == null)
        {
            row = new AppSetting
            {
                Key       = key,
                Category  = InferCategory(key),
                Value     = value,
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedById = actorUserId
            };
            db.AppSettings.Add(row);
        }
        else
        {
            row.Value       = value;
            row.UpdatedAt   = now;
            row.UpdatedById = actorUserId;
        }
        await db.SaveChangesAsync(ct);
        _cache[key] = value;
    }

    /// <summary>
    /// Seed a setting on first boot. Inserts the row with the default
    /// value AND populates DefaultValue. Skips if a row already exists —
    /// admins who've already customised the value don't get overwritten.
    /// </summary>
    public async Task SeedAsync(
        string key, string category, string? defaultValue,
        string? description = null, bool isSecret = false,
        CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.AppSettings.AnyAsync(s => s.Key == key, ct)) return;

        var now = DateTime.UtcNow;
        db.AppSettings.Add(new AppSetting
        {
            Key          = key,
            Category     = category,
            Value        = defaultValue,
            DefaultValue = defaultValue,
            Description  = description,
            IsSecret     = isSecret,
            CreatedAt    = now,
            UpdatedAt    = now
        });
        try { await db.SaveChangesAsync(ct); }
        catch (Exception ex)
        {
            // Unique-key race: another instance seeded in parallel. Safe to ignore.
            _log.LogDebug(ex, "Seed for key {Key} ignored (likely duplicate)", key);
        }
    }

    /// <summary>List all settings (DB rows only — not the IConfiguration
    /// fallback) for the admin UI. Secret values are returned masked.</summary>
    public async Task<List<AppSetting>> GetAllAsync(CancellationToken ct = default)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.AppSettings
            .OrderBy(s => s.Category).ThenBy(s => s.Key)
            .ToListAsync(ct);
        // Mask secrets before handing back to the caller.
        foreach (var r in rows)
        {
            if (r.IsSecret && !string.IsNullOrEmpty(r.Value))
                r.Value = new string('•', Math.Min(r.Value.Length, 12));
        }
        return rows;
    }

    /// <summary>Wipe the entire cache. Useful from the admin UI after a
    /// bulk edit, or as a safety hatch if values look stale.</summary>
    public void Invalidate() => _cache.Clear();

    /// <summary>Heuristic: take the chunk before the first dot as the
    /// category. Mine.Name → "Mine", Email.SmtpHost → "Email", etc.</summary>
    private static string InferCategory(string key)
    {
        var i = key.IndexOf('.');
        return i > 0 ? key[..i] : "General";
    }
}
