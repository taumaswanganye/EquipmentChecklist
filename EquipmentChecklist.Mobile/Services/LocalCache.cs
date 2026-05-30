using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EquipmentChecklist.DTOs;
using SQLite;

namespace EquipmentChecklist.Mobile.Services;

/// <summary>
/// Read-through SQLite cache for everything the operator needs offline:
///   - Machines assigned to them
///   - Checklist templates per machine
///   - Recent submissions list (so My Submissions works offline)
///   - User profile + PBKDF2 password hash (so they can sign in offline
///     once they've been online once)
///
/// All rows live in <c>eqcache.db</c> under <see cref="FileSystem.AppDataDirectory"/>.
/// </summary>
public class LocalCache
{
    private readonly SQLiteAsyncConnection _db;
    private readonly SemaphoreSlim         _initLock = new(1, 1);
    private bool                           _initialized;

    public LocalCache()
    {
        var path = Path.Combine(FileSystem.AppDataDirectory, "eqcache.db");
        _db = new SQLiteAsyncConnection(path,
            SQLiteOpenFlags.Create | SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.SharedCache);
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await _db.CreateTableAsync<CachedMachine>();
            await _db.CreateTableAsync<CachedTemplate>();
            await _db.CreateTableAsync<CachedUser>();
            await _db.CreateTableAsync<CachedSubmission>();
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                                MACHINES
    // ═════════════════════════════════════════════════════════════════════════

    public async Task SaveMachinesAsync(IEnumerable<SyncMachineSummaryDto> machines)
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<CachedMachine>();
        var now = DateTime.UtcNow;
        var rows = machines.Select(m => new CachedMachine
        {
            Id                = m.Id,
            MachineNumber     = m.MachineNumber,
            MachineName       = m.MachineName,
            TypeDisplay       = m.TypeDisplay,
            Description       = m.Description,
            IsImmobilised     = m.IsImmobilised,
            ImmobilisedReason = m.ImmobilisedReason,
            HasTemplate       = m.HasTemplate,
            FetchedAt         = now
        }).ToList();
        if (rows.Count > 0) await _db.InsertAllAsync(rows);
    }

    public async Task<List<SyncMachineSummaryDto>> GetMachinesAsync()
    {
        await EnsureInitAsync();
        var rows = await _db.Table<CachedMachine>().ToListAsync();
        return rows.Select(r => new SyncMachineSummaryDto
        {
            Id                = r.Id,
            MachineNumber     = r.MachineNumber,
            MachineName       = r.MachineName,
            TypeDisplay       = r.TypeDisplay,
            Description       = r.Description,
            IsImmobilised     = r.IsImmobilised,
            ImmobilisedReason = r.ImmobilisedReason,
            HasTemplate       = r.HasTemplate
        }).ToList();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                               TEMPLATES
    // ═════════════════════════════════════════════════════════════════════════

    public async Task SaveTemplateAsync(SyncTemplateDto t)
    {
        await EnsureInitAsync();
        await _db.InsertOrReplaceAsync(new CachedTemplate
        {
            MachineId  = t.MachineId,
            TemplateId = t.TemplateId,
            Name       = t.Name,
            ItemsJson  = JsonSerializer.Serialize(t.Items),
            FetchedAt  = DateTime.UtcNow
        });
    }

    public async Task<SyncTemplateDto?> GetTemplateAsync(int machineId)
    {
        await EnsureInitAsync();
        var row = await _db.Table<CachedTemplate>()
            .Where(r => r.MachineId == machineId)
            .FirstOrDefaultAsync();
        if (row is null) return null;

        var items = string.IsNullOrEmpty(row.ItemsJson)
            ? new List<SyncTemplateItemDto>()
            : JsonSerializer.Deserialize<List<SyncTemplateItemDto>>(row.ItemsJson)
              ?? new List<SyncTemplateItemDto>();

        return new SyncTemplateDto
        {
            MachineId  = row.MachineId,
            TemplateId = row.TemplateId,
            Name       = row.Name,
            Items      = items
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                          USER PROFILES (offline auth)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Save a user's profile + a PBKDF2 hash of their password so
    /// they can sign in later without internet.</summary>
    public async Task SaveUserAsync(SyncUserDto user, string password, string? jwt, DateTime? jwtExp)
    {
        await EnsureInitAsync();
        var emailKey = NormalizeEmail(user.Email);
        await _db.InsertOrReplaceAsync(new CachedUser
        {
            EmailKey       = emailKey,
            UserId         = user.Id,
            FullName       = user.FullName,
            EmployeeNumber = user.EmployeeNumber,
            Email          = user.Email,
            RolesJson      = JsonSerializer.Serialize(user.Roles ?? Array.Empty<string>()),
            PasswordHash   = HashPassword(password),
            JwtToken       = jwt,
            JwtExpiresAt   = jwtExp,
            LastSyncedAt   = DateTime.UtcNow
        });
    }

    public async Task<CachedUser?> FindUserAsync(string email)
    {
        await EnsureInitAsync();
        var key = NormalizeEmail(email);
        return await _db.Table<CachedUser>()
            .Where(u => u.EmailKey == key)
            .FirstOrDefaultAsync();
    }

    public static bool VerifyPassword(string password, string hashStr)
    {
        try
        {
            var parts = (hashStr ?? "").Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
            var iters    = int.Parse(parts[1]);
            var salt     = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);

            using var derive = new Rfc2898DeriveBytes(password, salt, iters, HashAlgorithmName.SHA256);
            var actual = derive.GetBytes(expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                       RECENT SUBMISSIONS (per user)
    // ═════════════════════════════════════════════════════════════════════════

    public async Task SaveRecentSubmissionsAsync(string operatorEmail, IEnumerable<RecentSubmissionDto> subs)
    {
        await EnsureInitAsync();
        var key = NormalizeEmail(operatorEmail);

        // Replace this user's slice
        await _db.ExecuteAsync(
            "DELETE FROM cached_submissions WHERE OperatorEmailKey = ?", key);

        var now  = DateTime.UtcNow;
        var rows = subs.Select(s => new CachedSubmission
        {
            Id                = s.SubmissionId,
            OperatorEmailKey  = key,
            MachineNumber     = s.MachineNumber,
            MachineName       = s.MachineName,
            OperatorName      = s.OperatorName,
            Status            = (int)s.Status,
            SubmittedAt       = s.SubmittedAt,
            ItemCount         = s.ItemCount,
            DefectCount       = s.DefectCount,
            Shift             = (int)s.Shift,
            KmOrHourMeter     = s.KmOrHourMeter,
            CachedAt          = now
        }).ToList();
        if (rows.Count > 0) await _db.InsertAllAsync(rows);
    }

    public async Task<List<RecentSubmissionDto>> GetRecentSubmissionsAsync(string operatorEmail)
    {
        await EnsureInitAsync();
        var key = NormalizeEmail(operatorEmail);
        var rows = await _db.Table<CachedSubmission>()
            .Where(r => r.OperatorEmailKey == key)
            .OrderByDescending(r => r.SubmittedAt)
            .ToListAsync();
        return rows.Select(r => new RecentSubmissionDto
        {
            SubmissionId  = r.Id,
            MachineNumber = r.MachineNumber,
            MachineName   = r.MachineName,
            OperatorName  = r.OperatorName,
            Status        = (EquipmentChecklist.Models.ChecklistStatus)r.Status,
            SubmittedAt   = r.SubmittedAt,
            ItemCount     = r.ItemCount,
            DefectCount   = r.DefectCount,
            Shift         = (EquipmentChecklist.Models.Shift)r.Shift,
            KmOrHourMeter = r.KmOrHourMeter
        }).ToList();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                                  CLEAR
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Drops machine + template + recent caches but keeps the
    /// CachedUser table so the operator can come back tomorrow and sign in
    /// offline. Called on every sign-out.</summary>
    public async Task ClearAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<CachedMachine>();
        await _db.DeleteAllAsync<CachedTemplate>();
        await _db.DeleteAllAsync<CachedSubmission>();
        // Intentionally NOT clearing CachedUser
    }

    /// <summary>Full wipe, including remembered users. Use only when you want
    /// to forget every operator who's ever signed in on this device.</summary>
    public async Task ForgetEverythingAsync()
    {
        await EnsureInitAsync();
        await _db.DeleteAllAsync<CachedMachine>();
        await _db.DeleteAllAsync<CachedTemplate>();
        await _db.DeleteAllAsync<CachedSubmission>();
        await _db.DeleteAllAsync<CachedUser>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static string NormalizeEmail(string email)
        => (email ?? "").Trim().ToLowerInvariant();

    public static string HashPassword(string password)
    {
        const int iters = 100_000;
        var salt = RandomNumberGenerator.GetBytes(16);
        using var derive = new Rfc2898DeriveBytes(password, salt, iters, HashAlgorithmName.SHA256);
        var hash = derive.GetBytes(32);
        return $"pbkdf2${iters}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    // ── Tables ────────────────────────────────────────────────────────────────
    [Table("cached_machines")]
    public class CachedMachine
    {
        [PrimaryKey] public int Id { get; set; }
        public string  MachineNumber     { get; set; } = "";
        public string  MachineName       { get; set; } = "";
        public string  TypeDisplay       { get; set; } = "";
        public string? Description       { get; set; }
        public bool    IsImmobilised     { get; set; }
        public string? ImmobilisedReason { get; set; }
        public bool    HasTemplate       { get; set; }
        public DateTime FetchedAt        { get; set; }
    }

    [Table("cached_templates")]
    public class CachedTemplate
    {
        [PrimaryKey] public int MachineId { get; set; }
        public int      TemplateId        { get; set; }
        public string   Name              { get; set; } = "";
        public string   ItemsJson         { get; set; } = "";
        public DateTime FetchedAt         { get; set; }
    }

    [Table("cached_users")]
    public class CachedUser
    {
        [PrimaryKey] public string EmailKey   { get; set; } = ""; // lower-cased
        public string  UserId                  { get; set; } = "";
        public string  Email                   { get; set; } = ""; // original casing
        public string  FullName                { get; set; } = "";
        public string  EmployeeNumber          { get; set; } = "";
        public string  RolesJson               { get; set; } = "";
        public string  PasswordHash            { get; set; } = ""; // pbkdf2$iters$salt$hash
        public string? JwtToken                { get; set; }
        public DateTime? JwtExpiresAt          { get; set; }
        public DateTime LastSyncedAt           { get; set; }
    }

    [Table("cached_submissions")]
    public class CachedSubmission
    {
        [PrimaryKey] public int Id { get; set; }
        [Indexed]
        public string  OperatorEmailKey { get; set; } = ""; // FK to CachedUser.EmailKey
        public string  MachineNumber    { get; set; } = "";
        public string  MachineName      { get; set; } = "";
        public string  OperatorName     { get; set; } = "";
        public int     Status           { get; set; } // ChecklistStatus enum int
        public DateTime SubmittedAt     { get; set; }
        public int     ItemCount        { get; set; }
        public int     DefectCount      { get; set; }
        public int     Shift            { get; set; } // Shift enum int
        public int?    KmOrHourMeter    { get; set; }
        public DateTime CachedAt        { get; set; }
    }
}
