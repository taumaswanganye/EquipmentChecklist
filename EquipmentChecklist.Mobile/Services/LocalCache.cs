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
            await _db.CreateTableAsync<CachedJsonBlob>();
            await _db.CreateTableAsync<CachedPdf>();
            await _db.CreateTableAsync<CachedLocalPdf>();
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

    /// <summary>
    /// Stamp the current UTC time on the user's <c>LastSyncedAt</c> column.
    /// Called by <see cref="ApiHealth"/> after every successful server ping
    /// so the staleness banner in MainLayout knows how long it's been since
    /// the device actually heard from the API.
    /// </summary>
    public async Task BumpLastSyncedAsync(string userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return;
        await EnsureInitAsync();
        var key = NormalizeEmail(userEmail);
        await _db.ExecuteAsync(
            "UPDATE cached_users SET LastSyncedAt = ? WHERE EmailKey = ?",
            DateTime.UtcNow, key);
    }

    /// <summary>
    /// Read the user's <c>LastSyncedAt</c>. Returns null if the user has never
    /// been cached (first-ever launch, no online login yet) — the banner
    /// treats that as "fresh", not "stale".
    /// </summary>
    public async Task<DateTime?> GetLastSyncedAtAsync(string userEmail)
    {
        if (string.IsNullOrWhiteSpace(userEmail)) return null;
        await EnsureInitAsync();
        var key = NormalizeEmail(userEmail);
        var row = await _db.Table<CachedUser>()
            .Where(u => u.EmailKey == key)
            .FirstOrDefaultAsync();
        return row?.LastSyncedAt;
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
    //              SUPERVISOR + MECHANIC QUEUE CACHES (JSON blobs)
    //
    // These are small lists scoped to the calling user. Rather than spread them
    // across one table per shape, we serialize them as JSON blobs in a single
    // shared table keyed by (UserEmailKey, BlobKind). Lets us add new caches
    // later without schema migrations.
    // ═════════════════════════════════════════════════════════════════════════

    private static class BlobKinds
    {
        public const string SupervisorQueue     = "sup-queue";
        public const string SupervisorOperators = "sup-operators";
        public const string NoGoMachines        = "nogo";          // shared by Supervisor + Mechanic
        public const string MechanicDefects     = "mech-defects";
        /// <summary>Site-level mine config (one row, not per-user).</summary>
        public const string MineConfig          = "mine";
    }

    // ── Mine config (singleton — not per-user) ──────────────────────────────
    // Re-uses the SaveBlob/GetBlob pattern with a fixed sentinel key so the
    // single global row is easy to find. Cached so first launch on a fresh
    // device with no signal still has labels to render.
    private const string MineConfigKey = "__mine__";

    public Task SaveMineConfigAsync(EquipmentChecklist.DTOs.MineDto config) =>
        SaveBlobAsync(MineConfigKey, BlobKinds.MineConfig, config);

    public Task<EquipmentChecklist.DTOs.MineDto?> GetMineConfigAsync() =>
        GetBlobAsync<EquipmentChecklist.DTOs.MineDto>(MineConfigKey, BlobKinds.MineConfig);

    public Task SaveSupervisorQueueAsync(string supervisorEmail, IEnumerable<SupervisorQueueItemDto> items) =>
        SaveBlobAsync(supervisorEmail, BlobKinds.SupervisorQueue, items);

    public Task<List<SupervisorQueueItemDto>?> GetSupervisorQueueAsync(string supervisorEmail) =>
        GetBlobAsync<List<SupervisorQueueItemDto>>(supervisorEmail, BlobKinds.SupervisorQueue);

    public Task SaveSupervisorOperatorsAsync(string supervisorEmail, IEnumerable<SupervisorOperatorDto> operators) =>
        SaveBlobAsync(supervisorEmail, BlobKinds.SupervisorOperators, operators);

    public Task<List<SupervisorOperatorDto>?> GetSupervisorOperatorsAsync(string supervisorEmail) =>
        GetBlobAsync<List<SupervisorOperatorDto>>(supervisorEmail, BlobKinds.SupervisorOperators);

    public Task SaveNoGoMachinesAsync(string userEmail, IEnumerable<NoGoMachineDto> machines) =>
        SaveBlobAsync(userEmail, BlobKinds.NoGoMachines, machines);

    public Task<List<NoGoMachineDto>?> GetNoGoMachinesAsync(string userEmail) =>
        GetBlobAsync<List<NoGoMachineDto>>(userEmail, BlobKinds.NoGoMachines);

    public Task SaveMechanicDefectsAsync(string mechanicEmail, MechanicQueueDto queue) =>
        SaveBlobAsync(mechanicEmail, BlobKinds.MechanicDefects, queue);

    public Task<MechanicQueueDto?> GetMechanicDefectsAsync(string mechanicEmail) =>
        GetBlobAsync<MechanicQueueDto>(mechanicEmail, BlobKinds.MechanicDefects);

    private async Task SaveBlobAsync<T>(string userEmail, string kind, T value)
    {
        await EnsureInitAsync();
        var key = NormalizeEmail(userEmail);

        // (UserEmailKey, Kind) is the logical key. SQLite-net's composite-key
        // support is limited so we emulate UPSERT: delete any existing row,
        // then insert. Cheap because each (user, kind) pair has at most one row.
        await _db.ExecuteAsync(
            "DELETE FROM cached_blobs WHERE UserEmailKey = ? AND Kind = ?",
            key, kind);

        await _db.InsertAsync(new CachedJsonBlob
        {
            UserEmailKey = key,
            Kind         = kind,
            Json         = JsonSerializer.Serialize(value),
            CachedAt     = DateTime.UtcNow
        });
    }

    private async Task<T?> GetBlobAsync<T>(string userEmail, string kind) where T : class
    {
        await EnsureInitAsync();
        var key = NormalizeEmail(userEmail);
        var row = await _db.Table<CachedJsonBlob>()
            .Where(r => r.UserEmailKey == key && r.Kind == kind)
            .FirstOrDefaultAsync();
        if (row == null || string.IsNullOrEmpty(row.Json)) return null;
        try   { return JsonSerializer.Deserialize<T>(row.Json); }
        catch { return null; }
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                      RENDERED-PDF BYTES (per submission)
    // Cached so a PDF the user already viewed online can be re-shown when the
    // device goes offline. Bytes are blobs; ~30–200 KB each. We cap-prune on
    // each save so this table doesn't grow forever.
    // ═════════════════════════════════════════════════════════════════════════

    private const int MAX_CACHED_PDFS = 50;

    public async Task SavePdfAsync(int submissionId, byte[] bytes)
    {
        await EnsureInitAsync();
        await _db.InsertOrReplaceAsync(new CachedPdf
        {
            SubmissionId = submissionId,
            Bytes        = bytes,
            CachedAt     = DateTime.UtcNow
        });

        // Prune oldest if we're over the cap.
        var count = await _db.Table<CachedPdf>().CountAsync();
        if (count > MAX_CACHED_PDFS)
        {
            var oldest = await _db.Table<CachedPdf>()
                .OrderBy(p => p.CachedAt)
                .Take(count - MAX_CACHED_PDFS)
                .ToListAsync();
            foreach (var p in oldest) await _db.DeleteAsync(p);
        }
    }

    public async Task<byte[]?> GetPdfAsync(int submissionId)
    {
        await EnsureInitAsync();
        var row = await _db.Table<CachedPdf>()
            .Where(p => p.SubmissionId == submissionId)
            .FirstOrDefaultAsync();
        return row?.Bytes;
    }

    // ── Local PDFs keyed by Guid (offline submissions, no server id yet) ────
    // Separate table because LocalId is a Guid string, not an int. Once the
    // submission syncs and the server hands us a real SubmissionId we can
    // optionally copy the bytes across to the int-keyed table.

    public async Task SaveLocalPdfAsync(Guid localId, byte[] bytes,
                                        int machineId, string? machineNumber)
    {
        await EnsureInitAsync();
        await _db.InsertOrReplaceAsync(new CachedLocalPdf
        {
            LocalId       = localId.ToString(),
            Bytes         = bytes,
            MachineId     = machineId,
            MachineNumber = machineNumber ?? "",
            CachedAt      = DateTime.UtcNow
        });

        // Prune oldest if we drift past the cap.
        var count = await _db.Table<CachedLocalPdf>().CountAsync();
        if (count > MAX_CACHED_PDFS)
        {
            var oldest = await _db.Table<CachedLocalPdf>()
                .OrderBy(p => p.CachedAt)
                .Take(count - MAX_CACHED_PDFS)
                .ToListAsync();
            foreach (var p in oldest) await _db.DeleteAsync(p);
        }
    }

    public async Task<byte[]?> GetLocalPdfAsync(Guid localId)
    {
        await EnsureInitAsync();
        var key = localId.ToString();
        var row = await _db.Table<CachedLocalPdf>()
            .Where(p => p.LocalId == key)
            .FirstOrDefaultAsync();
        return row?.Bytes;
    }

    /// <summary>
    /// Drop a local PDF after the corresponding submission has been
    /// confirmed by the server (the server-rendered version takes over).
    /// </summary>
    public async Task DeleteLocalPdfAsync(Guid localId)
    {
        await EnsureInitAsync();
        var key = localId.ToString();
        await _db.ExecuteAsync(
            "DELETE FROM cached_local_pdfs WHERE LocalId = ?", key);
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
        await _db.DeleteAllAsync<CachedJsonBlob>();
        await _db.DeleteAllAsync<CachedPdf>();
        await _db.DeleteAllAsync<CachedLocalPdf>();
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
        await _db.DeleteAllAsync<CachedJsonBlob>();
        await _db.DeleteAllAsync<CachedPdf>();
        await _db.DeleteAllAsync<CachedLocalPdf>();
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

    /// <summary>
    /// Generic JSON-blob cache row, scoped to a user. Holds whole DTO lists
    /// (supervisor queue, mechanic defects, etc.) — anything that's small
    /// enough to be read as a single chunk and where we don't need per-row
    /// SQL queries.
    /// </summary>
    [Table("cached_blobs")]
    public class CachedJsonBlob
    {
        /// <summary>Composite PK: (UserEmailKey, Kind). SQLite-net doesn't
        /// support composite PKs natively, so we emulate by indexing both
        /// columns and using InsertOrReplace on a synthetic id.</summary>
        [PrimaryKey, AutoIncrement] public int Id { get; set; }
        [Indexed] public string UserEmailKey { get; set; } = "";
        [Indexed] public string Kind         { get; set; } = "";
        public string  Json     { get; set; } = "";
        public DateTime CachedAt { get; set; }
    }

    /// <summary>
    /// Cached rendered PDF bytes, keyed by SubmissionId. Lets a user re-open
    /// a submission's PDF offline as long as they viewed it once while online.
    /// </summary>
    [Table("cached_pdfs")]
    public class CachedPdf
    {
        [PrimaryKey] public int SubmissionId { get; set; }
        public byte[]   Bytes    { get; set; } = Array.Empty<byte>();
        public DateTime CachedAt { get; set; }
    }

    /// <summary>
    /// PDF bytes generated on-device for submissions that haven't been seen by
    /// the server yet — keyed by the submission's <c>LocalId</c> (Guid string).
    /// The bytes get rendered by <c>PdfGenerator.buildChecklist</c> in JS.
    /// </summary>
    [Table("cached_local_pdfs")]
    public class CachedLocalPdf
    {
        [PrimaryKey] public string LocalId { get; set; } = ""; // Guid.ToString()
        public byte[]   Bytes         { get; set; } = Array.Empty<byte>();
        public int      MachineId     { get; set; }
        public string   MachineNumber { get; set; } = "";
        public DateTime CachedAt      { get; set; }
    }
}
