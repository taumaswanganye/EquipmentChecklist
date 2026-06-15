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

    // ────────────────────────────────────────────────────────────────────
    //  PERSISTENT-BACKUP FEATURE — DISABLED
    //
    //  The original idea was to mirror eqcache.db to the public Documents
    //  folder so the cache survived Android's "Clear data" action. Enabling
    //  it caused the app to crash silently on launch — exact cause unknown
    //  (suspect AndroidManifest XML rule resources not being packaged, or
    //  Android.OS.Environment behaviour differing per OEM/API level).
    //
    //  Until further notice the call sites are commented out — search the
    //  file for "DISABLED — persistent backup" to find them all. To
    //  re-enable later: (1) restore PersistentBackup.cs from git history,
    //  (2) inject it in the LocalCache constructor, (3) uncomment all the
    //  call sites, (4) re-add the manifest pieces and XML rule files, and
    //  (5) AddSingleton<PersistentBackup>() + TryEager<>() in MauiProgram.
    // ────────────────────────────────────────────────────────────────────
    #pragma warning disable IDE0051 // unused private member — intentionally kept
    private void KickBackup()
    {
        // intentionally empty — feature disabled, see notice above
    }
    #pragma warning restore IDE0051

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

        // Materialise the projection BEFORE the transaction so any LINQ
        // failure can't leave us mid-transaction.
        var now  = DateTime.UtcNow;
        var rows = machines.Select(m => new CachedMachine
        {
            Id                = m.Id,
            MachineNumber     = m.MachineNumber,
            MachineName       = m.MachineName,
            TypeCode          = m.Type,
            TypeDisplay       = m.TypeDisplay,
            Description       = m.Description,
            IsImmobilised     = m.IsImmobilised,
            ImmobilisedReason = m.ImmobilisedReason,
            HasTemplate       = m.HasTemplate,
            FetchedAt         = now
        }).ToList();

        // ── Atomic replace ─────────────────────────────────────────────
        // Previously this was DeleteAllAsync(...) followed by InsertAllAsync(...)
        // without a wrapping transaction. If the app was killed (close, OOM,
        // ANR, dev redeploy) between the two calls, the cache landed empty —
        // exactly the "everything disappears when I close the app" symptom.
        // RunInTransactionAsync gives us an all-or-nothing replace: either the
        // user sees the OLD rows on next boot, or the NEW rows. Never empty.
        await _db.RunInTransactionAsync(tx =>
        {
            tx.DeleteAll<CachedMachine>();
            if (rows.Count > 0) tx.InsertAll(rows);
        });
        // KickBackup();  // ── DISABLED — persistent backup feature off, see PersistentBackup.cs
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
            Type              = r.TypeCode,
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
        // KickBackup();  // ── DISABLED — persistent backup feature off, see PersistentBackup.cs
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
            EmailKey         = emailKey,
            UserId           = user.Id,
            FullName         = user.FullName,
            EmployeeNumber   = user.EmployeeNumber,
            Email            = user.Email,
            RolesJson        = JsonSerializer.Serialize(user.Roles ?? Array.Empty<string>()),
            // Fresh competency snapshot every successful sign-in. Empty
            // list serialises as "[]" — the gate then blocks every
            // machine because no entry will match.
            CompetenciesJson = JsonSerializer.Serialize(user.Competencies ?? new List<CompetencySummaryDto>()),
            PasswordHash     = HashPassword(password),
            JwtToken         = jwt,
            JwtExpiresAt     = jwtExp,
            LastSyncedAt     = DateTime.UtcNow,
            // A successful server sign-in means the user is NOT blocked,
            // regardless of what we previously cached. This is the path
            // by which "admin reactivated me" automatically clears the
            // local block flag — no extra API call needed.
            IsBlocked        = false
        });
        // The user row is the offline-signin credential. Mirroring here means
        // even an "operator opened the app, signed in, closed it before doing
        // anything" scenario survives a later Clear data.
        // KickBackup();  // ── DISABLED — persistent backup feature off, see PersistentBackup.cs
    }

    /// <summary>
    /// Flip the IsBlocked flag on a cached user. Called by AuthService
    /// the moment the server signals deactivation — either via the login
    /// response's "user_deactivated" code OR the X-Auth-Failure header on
    /// any authenticated call. After this, offline sign-in refuses the
    /// same credentials with a "blocked by admin" error.
    /// </summary>
    public async Task MarkUserBlockedAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        await EnsureInitAsync();
        var key = NormalizeEmail(email);
        await _db.ExecuteAsync(
            "UPDATE cached_users SET IsBlocked = 1, JwtToken = NULL, JwtExpiresAt = NULL WHERE EmailKey = ?",
            key);
    }

    /// <summary>
    /// Targeted refresh of the cached user's competency list. Called by
    /// the periodic /me refresh in <c>ApiHealth</c> so an admin adding /
    /// revoking competencies on the web reaches the mobile cache without
    /// requiring the operator to sign out and back in. Touches ONLY
    /// CompetenciesJson — password hash, JWT, roles all unchanged.
    /// </summary>
    public async Task UpdateCompetenciesAsync(string? email,
        IEnumerable<CompetencySummaryDto>? competencies)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        await EnsureInitAsync();
        var key  = NormalizeEmail(email);
        var json = JsonSerializer.Serialize(competencies ?? new List<CompetencySummaryDto>());
        await _db.ExecuteAsync(
            "UPDATE cached_users SET CompetenciesJson = ?, LastSyncedAt = ? WHERE EmailKey = ?",
            json, DateTime.UtcNow, key);
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
    /// Local mirror of the server's CompetencyService.IsCompetentAsync.
    /// Reads the cached user's CompetenciesJson and looks for a non-expired
    /// entry matching the machine type. Admins bypass (we treat any role
    /// containing "Admin" as a global allow — the server does the same).
    /// </summary>
    public async Task<bool> IsCompetentAsync(string? operatorEmail, int machineTypeCode)
    {
        if (string.IsNullOrWhiteSpace(operatorEmail)) return false;
        var user = await FindUserAsync(operatorEmail);
        if (user == null) return false;

        // Admin role bypass — keeps parity with the server gate.
        try
        {
            var roles = JsonSerializer.Deserialize<string[]>(user.RolesJson) ?? Array.Empty<string>();
            if (roles.Contains("Admin")) return true;
        }
        catch { }

        try
        {
            var list = JsonSerializer.Deserialize<List<CompetencySummaryDto>>(user.CompetenciesJson)
                       ?? new List<CompetencySummaryDto>();
            var now = DateTime.UtcNow;
            return list.Any(c => c.MachineType == machineTypeCode && c.ExpiresAt > now);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the soonest-expiring competency date for a given machine
    /// type, or null if the operator isn't currently competent on it.
    /// Used by the "blocked" gate screen to tell the operator when their
    /// competency last expired.
    /// </summary>
    public async Task<DateTime?> GetCompetencyExpiryAsync(string? operatorEmail, int machineTypeCode)
    {
        if (string.IsNullOrWhiteSpace(operatorEmail)) return null;
        var user = await FindUserAsync(operatorEmail);
        if (user == null) return null;
        try
        {
            var list = JsonSerializer.Deserialize<List<CompetencySummaryDto>>(user.CompetenciesJson)
                       ?? new List<CompetencySummaryDto>();
            return list.Where(c => c.MachineType == machineTypeCode)
                       .Select(c => (DateTime?)c.ExpiresAt)
                       .Max();
        }
        catch
        {
            return null;
        }
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
        var key  = NormalizeEmail(operatorEmail);
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

        // ── SAFETY: never wipe the cache to "0 rows" ──────────────────────
        // Pre-checklists are append-only — there's no legitimate reason the
        // server would suddenly return fewer than what we already have. If
        // it does (transient empty body, JWT race, captive portal returning
        // 200 OK with []), keeping the existing cache is the safer call.
        // The operator's prior submissions remain visible offline.
        //
        // The eventual REAL refresh (when the server returns non-empty)
        // does a full replace via DELETE+INSERT, so stale rows can't
        // accumulate indefinitely.
        if (rows.Count == 0) return;

        // ── Atomic UPSERT (per row) ───────────────────────────────────────
        // Instead of DELETE all + INSERT new, do an InsertOrReplace per
        // row. This preserves any rows the server didn't include in this
        // particular response (e.g. if the API only returned the most
        // recent 30 but we already have 50 cached). The user's history
        // grows on the device rather than shrinking on every refresh.
        await _db.RunInTransactionAsync(tx =>
        {
            foreach (var r in rows) tx.InsertOrReplace(r);
        });
        // KickBackup();  // ── DISABLED — persistent backup feature off, see PersistentBackup.cs
    }

    public async Task<List<RecentSubmissionDto>> GetRecentSubmissionsAsync(string? operatorEmail)
    {
        await EnsureInitAsync();

        // ── Resolve the email key with multiple fallbacks ────────────────
        // The caller is usually <c>Auth.CurrentUser?.Email</c>. During app
        // boot, post-biometric unlock, or after a sign-out/sign-in toggle
        // CurrentUser can be transiently null — and the email-keyed query
        // would return zero rows even though SQLite has data. Falling back
        // to the most recently-signed-in CachedUser keeps the operator's
        // own history visible on a single-user device.
        //
        // On a SHARED device (rare in this workflow — operators have their
        // own phones), this could surface a different user's rows. That's
        // a fair trade for the common case where the operator just opened
        // the app and the email hasn't propagated yet.
        var key = NormalizeEmail(operatorEmail ?? "");
        if (string.IsNullOrEmpty(key))
        {
            try
            {
                var lastUser = await _db.Table<CachedUser>()
                    .OrderByDescending(u => u.LastSyncedAt)
                    .FirstOrDefaultAsync();
                if (lastUser != null) key = lastUser.EmailKey;
            }
            catch { /* fall through to empty result */ }
        }

        if (string.IsNullOrEmpty(key)) return new List<RecentSubmissionDto>();

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

        // ── Offline-everything caches ────────────────────────────────────
        //  Stats and submission details that previously fell back to "zero"
        //  or a network-fail screen now live here so an offline operator
        //  can still see the numbers they saw last time online, and tap
        //  through to any previous pre-checklist's full detail view.
        /// <summary>SyncOperatorStatsDto for the operator dashboard.</summary>
        public const string OperatorStats       = "op-stats";
        /// <summary>SyncMechanicStatsDto for the mechanic dashboard.</summary>
        public const string MechanicStats       = "mech-stats";
        /// <summary>SupervisorReviewDto for a SPECIFIC submission ID. The
        /// blob row's UserEmailKey holds "sub:{submissionId}" so we share
        /// the same table without a schema change.</summary>
        public const string SubmissionDetails   = "sub-detail";
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

    // ── Operator + mechanic stats (per-user) ────────────────────────────────
    // Caching the whole DTO so the dashboard tiles show real numbers on
    // offline boot instead of zeros. The numbers may be slightly stale (last
    // time the device was online), but a stale "12 defects" is a more
    // honest signal than "0 defects".

    public Task SaveOperatorStatsAsync(string operatorEmail, SyncOperatorStatsDto stats) =>
        SaveBlobAsync(operatorEmail, BlobKinds.OperatorStats, stats);

    public Task<SyncOperatorStatsDto?> GetOperatorStatsAsync(string operatorEmail) =>
        GetBlobAsync<SyncOperatorStatsDto>(operatorEmail, BlobKinds.OperatorStats);

    public Task SaveMechanicStatsAsync(string mechanicEmail, MechanicStatsDto stats) =>
        SaveBlobAsync(mechanicEmail, BlobKinds.MechanicStats, stats);

    public Task<MechanicStatsDto?> GetMechanicStatsAsync(string mechanicEmail) =>
        GetBlobAsync<MechanicStatsDto>(mechanicEmail, BlobKinds.MechanicStats);

    // ── Full submission detail (per submission id) ──────────────────────────
    // Stores the whole SupervisorReviewDto: items list, signatures, photos,
    // audio, remarks. After the operator views a pre-checklist online once,
    // tapping "View" on it later — even offline — replays everything from
    // SQLite. The Json blob row's UserEmailKey holds "sub:{id}" so the
    // existing blob table is reused without a schema migration.

    /// <summary>Save the full review DTO for a submission so it can be
    /// re-opened offline later. No-ops on submissionId &lt;= 0 (queued / not
    /// yet acked by the server have no canonical id).</summary>
    public Task SaveSubmissionDetailsAsync(int submissionId, SupervisorReviewDto details)
    {
        if (submissionId <= 0 || details is null) return Task.CompletedTask;
        return SaveBlobAsync($"sub:{submissionId}", BlobKinds.SubmissionDetails, details);
    }

    /// <summary>Returns the cached review DTO for a submission, or null when
    /// the operator has never viewed it online before.</summary>
    public Task<SupervisorReviewDto?> GetSubmissionDetailsAsync(int submissionId)
    {
        if (submissionId <= 0) return Task.FromResult<SupervisorReviewDto?>(null);
        return GetBlobAsync<SupervisorReviewDto>($"sub:{submissionId}", BlobKinds.SubmissionDetails);
    }

    private async Task SaveBlobAsync<T>(string userEmail, string kind, T value)
    {
        await EnsureInitAsync();
        var key  = NormalizeEmail(userEmail);
        var row  = new CachedJsonBlob
        {
            UserEmailKey = key,
            Kind         = kind,
            Json         = JsonSerializer.Serialize(value),
            CachedAt     = DateTime.UtcNow
        };

        // (UserEmailKey, Kind) is the logical key. SQLite-net's composite-key
        // support is limited so we emulate UPSERT: delete the existing row
        // then insert the new one — wrapped in a single transaction so an
        // app close mid-write can't leave the slot empty. Cheap because each
        // (user, kind) pair has at most one row.
        await _db.RunInTransactionAsync(tx =>
        {
            tx.Execute(
                "DELETE FROM cached_blobs WHERE UserEmailKey = ? AND Kind = ?",
                key, kind);
            tx.Insert(row);
        });
        // KickBackup();  // ── DISABLED — persistent backup feature off, see PersistentBackup.cs
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
        // KickBackup();  // ── DISABLED — persistent backup feature off, see PersistentBackup.cs
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
        // KickBackup();  // ── DISABLED — persistent backup feature off, see PersistentBackup.cs
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
    /// offline.
    ///
    /// <para>IMPORTANT: this is NOT called automatically on sign-out
    /// (an earlier comment claimed it was — it was wrong). The cache is
    /// preserved across app close, sign-out, and sign-in by design so an
    /// offline operator can keep working with the data they already have.
    /// Only call this method explicitly when you want to nuke the cache
    /// (e.g. a "Clear local data" debug button).</para></summary>
    public async Task ClearAsync()
    {
        await EnsureInitAsync();
        await _db.RunInTransactionAsync(tx =>
        {
            tx.DeleteAll<CachedMachine>();
            tx.DeleteAll<CachedTemplate>();
            tx.DeleteAll<CachedSubmission>();
            tx.DeleteAll<CachedJsonBlob>();
            tx.DeleteAll<CachedPdf>();
            tx.DeleteAll<CachedLocalPdf>();
            // Intentionally NOT clearing CachedUser
        });
    }

    /// <summary>Full wipe, including remembered users. Use only when you want
    /// to forget every operator who's ever signed in on this device.</summary>
    public async Task ForgetEverythingAsync()
    {
        await EnsureInitAsync();
        await _db.RunInTransactionAsync(tx =>
        {
            tx.DeleteAll<CachedMachine>();
            tx.DeleteAll<CachedTemplate>();
            tx.DeleteAll<CachedSubmission>();
            tx.DeleteAll<CachedJsonBlob>();
            tx.DeleteAll<CachedPdf>();
            tx.DeleteAll<CachedLocalPdf>();
            tx.DeleteAll<CachedUser>();
        });
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                      6-MONTH RETENTION + HEALTH
    //
    // Cache is designed to live for ≥6 months (180 days) on the device. Rows
    // are never age-expired by the LocalCache itself — they're only replaced
    // when a newer copy arrives from the server. The optional
    // PruneOldCacheAsync below exists as an explicit lever the UI can invoke
    // (e.g. via a debug page) when an admin wants to reclaim space.
    //
    // CacheHealthAsync gives the UI a snapshot of "what's in here right now,
    // and when was each section last refreshed?" — useful for diagnosing
    // user reports of "cache disappeared".
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>How long the cache is guaranteed to remain even without a
    /// network refresh. Set to 6 months — long enough to cover seasonal
    /// rotations, extended leave, and contractor stints without losing the
    /// device's offline view. Don't prune anything younger than this.</summary>
    public static readonly TimeSpan MIN_CACHE_RETENTION = TimeSpan.FromDays(180);

    /// <summary>
    /// Returns a snapshot of the cache state — row counts per table plus
    /// when each section was most recently refreshed. The UI can show this
    /// on a debug page so an operator complaining "my data disappeared"
    /// can confirm it's actually still there.
    /// </summary>
    public async Task<CacheHealth> CacheHealthAsync()
    {
        await EnsureInitAsync();

        async Task<DateTime?> NewestAsync<T>(System.Linq.Expressions.Expression<Func<T, DateTime>> selector)
            where T : new()
        {
            try
            {
                var row = await _db.Table<T>()
                    .OrderByDescending(selector)
                    .FirstOrDefaultAsync();
                return row is null ? null : selector.Compile()(row);
            }
            catch { return null; }
        }

        return new CacheHealth(
            MachineCount:        await _db.Table<CachedMachine>()    .CountAsync(),
            TemplateCount:       await _db.Table<CachedTemplate>()   .CountAsync(),
            SubmissionCount:     await _db.Table<CachedSubmission>() .CountAsync(),
            BlobCount:           await _db.Table<CachedJsonBlob>()   .CountAsync(),
            PdfCount:            await _db.Table<CachedPdf>()        .CountAsync(),
            LocalPdfCount:       await _db.Table<CachedLocalPdf>()   .CountAsync(),
            UserCount:           await _db.Table<CachedUser>()       .CountAsync(),
            MachinesRefreshedAt: await NewestAsync<CachedMachine>(r => r.FetchedAt),
            SubmissionsCachedAt: await NewestAsync<CachedSubmission>(r => r.CachedAt),
            BlobsCachedAt:       await NewestAsync<CachedJsonBlob>(r => r.CachedAt));
    }

    /// <summary>
    /// Prune rows older than <paramref name="maxAge"/>. Default is the
    /// 60-day minimum retention from <see cref="MIN_CACHE_RETENTION"/>;
    /// callers MUST NOT pass a value smaller than that — we silently clamp
    /// to protect operators who've been offline for stretches longer than
    /// the wall-clock retention.
    ///
    /// <para>Returns total rows removed across all tables. The cache will
    /// repopulate from the server on next refresh, so a pruned device that
    /// regains signal recovers automatically.</para>
    /// </summary>
    public async Task<int> PruneOldCacheAsync(TimeSpan? maxAge = null)
    {
        await EnsureInitAsync();
        var effective = (maxAge ?? MIN_CACHE_RETENTION);
        if (effective < MIN_CACHE_RETENTION) effective = MIN_CACHE_RETENTION;
        var cutoff = DateTime.UtcNow - effective;

        int total = 0;
        await _db.RunInTransactionAsync(tx =>
        {
            total += tx.Execute("DELETE FROM cached_machines     WHERE FetchedAt < ?", cutoff);
            total += tx.Execute("DELETE FROM cached_templates    WHERE FetchedAt < ?", cutoff);
            total += tx.Execute("DELETE FROM cached_submissions  WHERE CachedAt  < ?", cutoff);
            total += tx.Execute("DELETE FROM cached_blobs        WHERE CachedAt  < ?", cutoff);
            total += tx.Execute("DELETE FROM cached_pdfs         WHERE CachedAt  < ?", cutoff);
            total += tx.Execute("DELETE FROM cached_local_pdfs   WHERE CachedAt  < ?", cutoff);
            // CachedUser rows are kept forever — they're the offline-signin
            // credentials, not refreshable data.
        });
        return total;
    }

    /// <summary>
    /// Snapshot of cache state surfaced by <see cref="CacheHealthAsync"/>.
    /// Lets diagnostic UIs render "you have N machines cached, last refreshed
    /// X days ago" without poking the SQLite tables directly.
    /// </summary>
    public record CacheHealth(
        int       MachineCount,
        int       TemplateCount,
        int       SubmissionCount,
        int       BlobCount,
        int       PdfCount,
        int       LocalPdfCount,
        int       UserCount,
        DateTime? MachinesRefreshedAt,
        DateTime? SubmissionsCachedAt,
        DateTime? BlobsCachedAt);

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
        /// <summary>MachineType enum value as int. Used by the
        /// competency pre-block on machine tap — matches against the
        /// operator's cached Competencies entries.</summary>
        public int     TypeCode          { get; set; }
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
        /// <summary>
        /// JSON-serialised List&lt;CompetencySummaryDto&gt; — the operator's
        /// current competencies with their expiry dates. Re-fetched on
        /// every successful login + every /me poll. Empty list = operator
        /// has no current competencies; the mobile gate blocks them on
        /// every machine.
        /// </summary>
        public string  CompetenciesJson        { get; set; } = "[]";
        /// <summary>
        /// True when the server has told us this user is deactivated
        /// (response code "user_deactivated" on login OR X-Auth-Failure
        /// header on any authenticated call). When true, AuthService
        /// refuses both online and offline sign-in. Cleared when a fresh
        /// online sign-in succeeds (because the server wouldn't issue a
        /// token if the user were still blocked).
        /// </summary>
        public bool    IsBlocked              { get; set; }
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
