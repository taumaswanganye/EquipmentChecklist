using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace EquipmentChecklist.Controllers.Api;

/// <summary>
/// REST API consumed by the MAUI mobile client (Equipment Checklist mobile app).
///
/// Auth: JWT bearer. The `multi` auth policy in Program.cs already routes
/// every /api/* request to the JWT scheme — cookies aren't accepted here.
///
///  POST /api/sync/login                          → JWT for the operator
///  GET  /api/sync/me                             → current user info
///  GET  /api/sync/machines                       → machines assigned to me
///  GET  /api/sync/machines/{id}/template         → checklist template + items
///  POST /api/sync/submissions                    → submit a checklist (idempotent on LocalId)
///  GET  /api/sync/submissions/recent             → my last 30 submissions
///
/// All write endpoints honour the same business rules as the web UI
/// (delegates to ChecklistService) so the mobile + web pipelines stay consistent.
/// </summary>
[ApiController]
[Route("api/sync")]
public class SyncController : ControllerBase
{
    private readonly ApplicationDbContext           _db;
    private readonly UserManager<ApplicationUser>   _users;
    private readonly SignInManager<ApplicationUser> _signIn;
    private readonly ChecklistService               _svc;
    private readonly IConfiguration                 _cfg;
    private readonly EmailService                   _email;
    private readonly PdfService                     _pdf;
    private readonly NotificationService             _notifications;
    private readonly AuditService                    _audit;
    private readonly CompetencyService               _competency;
    private readonly ILogger<SyncController>        _log;

    public SyncController(ApplicationDbContext db,
                          UserManager<ApplicationUser> users,
                          SignInManager<ApplicationUser> signIn,
                          ChecklistService svc,
                          IConfiguration cfg,
                          EmailService email,
                          PdfService pdf,
                          NotificationService notifications,
                          AuditService audit,
                          CompetencyService competency,
                          ILogger<SyncController> log)
    {
        _db    = db;
        _users = users;
        _signIn = signIn;
        _svc   = svc;
        _audit = audit;
        _cfg   = cfg;
        _email = email;
        _pdf   = pdf;
        _notifications = notifications;
        _competency = competency;
        _log   = log;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                            LIVENESS / PING
    // Cheap, anonymous endpoint the mobile app hits every ~20s to decide
    // whether the status-bar pill should read "Online" or "Offline". Returning
    // anything other than a 2xx (404, 500, timeout) lets the client flip the
    // pill so users know the API is unreachable even if Wi-Fi is up.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("ping")]
    [AllowAnonymous]
    public IActionResult Ping() => Ok(new { ok = true, at = DateTime.UtcNow });

    // ══════════════════════════════════════════════════════════════════════════
    //                              MINE CONFIG
    // Site-specific labels for splash / headers / PDF subtitle / etc.
    // Anonymous — mobile fetches once on launch and caches in LocalCache so the
    // labels survive offline boot. Admin changes the values in appsettings.json
    // and restarts the server; clients pick up the new labels on the next sync.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("mine")]
    [AllowAnonymous]
    public async Task<ActionResult<MineDto>> MineConfig(
        [FromServices] ConfigurationService config,
        [FromServices] Microsoft.Extensions.Options.IOptions<MineSettings> mineFallback)
    {
        // DB-backed first; appsettings fallback covers a brand-new deploy
        // where SeedAppSettingsAsync hasn't committed yet, plus dev runs
        // where the DB is offline. The MineSettings IOptions hangs around
        // as the safety net.
        var fallback = mineFallback.Value;
        return Ok(new MineDto
        {
            Name           = await config.GetAsync("Mine.Name")           ?? fallback.Name,
            ShortName      = await config.GetAsync("Mine.ShortName")      ?? fallback.ShortName,
            Tagline        = await config.GetAsync("Mine.Tagline")        ?? fallback.Tagline,
            ComplianceText = await config.GetAsync("Mine.ComplianceText") ?? fallback.ComplianceText
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                                LOGIN
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<SyncLoginResponse>> Login([FromBody] SyncLoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Email) || string.IsNullOrWhiteSpace(req?.Password))
            return BadRequest(new { error = "Email and password are required." });

        var user = await _users.FindByEmailAsync(req.Email.Trim());
        if (user == null)
            return Unauthorized(new { error = "Invalid email or password." });

        // ── Check the password BEFORE we leak deactivation status ──────
        // If the password is wrong we still want a generic "invalid email
        // or password" so an attacker can't enumerate which accounts exist
        // or which are deactivated. Only AFTER a successful password
        // check do we tell the legitimate user "your account is blocked",
        // because they need to know to stop trying.
        var ok = await _signIn.CheckPasswordSignInAsync(user, req.Password, lockoutOnFailure: true);
        if (!ok.Succeeded)
            return Unauthorized(new { error = ok.IsLockedOut
                ? "Account temporarily locked. Try again in a few minutes."
                : "Invalid email or password." });

        // ── Now: the password was correct. If the account is deactivated
        // return a SPECIFIC code so the mobile client can branch:
        //   * Display "your account has been deactivated"
        //   * Clear cached credentials so offline sign-in also fails
        //   * Refuse to issue a JWT — this user is blocked.
        // Status 403 (not 401) is the conventional signal for "authenticated
        // but forbidden", which is exactly the state here. The error code
        // string is contractual — the mobile AuthService matches on it.
        if (!user.IsActive)
            return StatusCode(403, new
            {
                error = "Your account has been deactivated. Contact your administrator.",
                code  = "user_deactivated"
            });

        // ── Device allowlist check ─────────────────────────────────────
        // The mobile app sends its stable hardware fingerprint in the
        // X-Device-Fingerprint header on EVERY request. Login refuses
        // unknown / inactive devices with a distinct code so the mobile
        // can render "this device is not authorised" and show the user
        // the fingerprint to read out to their admin.
        var fingerprint = HttpContext.Request.Headers["X-Device-Fingerprint"].ToString();
        if (string.IsNullOrWhiteSpace(fingerprint))
            return StatusCode(403, new
            {
                error = "This device hasn't sent its fingerprint. Update the mobile app.",
                code  = "device_missing_fingerprint"
            });

        var device = await _db.AllowedDevices
            .FirstOrDefaultAsync(d => d.DeviceFingerprint == fingerprint);
        if (device == null)
            return StatusCode(403, new
            {
                error       = "This device is not authorised. Show your administrator the device code below.",
                code        = "device_not_registered",
                fingerprint = fingerprint
            });
        if (!device.IsActive)
            return StatusCode(403, new
            {
                error = "This device has been revoked by an administrator.",
                code  = "device_revoked"
            });
        if (!string.IsNullOrEmpty(device.AssignedUserId) &&
            !string.Equals(device.AssignedUserId, user.Id, StringComparison.Ordinal))
            return StatusCode(403, new
            {
                error = "This device is assigned to a different user.",
                code  = "device_wrong_user"
            });

        // Touch LastSeenAt so the admin's "last activity" column on the
        // Devices page is meaningful. Fire-and-forget — a save failure
        // here must not block sign-in.
        device.LastSeenAt = DateTime.UtcNow;
        try { await _db.SaveChangesAsync(); } catch { }

        var roles      = await _users.GetRolesAsync(user);
        var (token, exp) = IssueJwt(user, roles);

        // Audit row uses an EXPLICIT actor — the controller's
        // ClaimsPrincipal isn't populated for an unauthenticated POST.
        // DeviceKind is read from the User-Agent so phone vs desktop is
        // distinguishable in the trail.
        var ua = HttpContext.Request.Headers["User-Agent"].ToString() ?? "";
        var deviceKind = ua.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "android"
                       : ua.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "windows"
                       : "web";
        await _audit.LogAsync(
            actor: new AuditService.ActorContext(
                UserId:    user.Id,
                Name:      user.FullName,
                Email:     user.Email,
                Role:      roles.Contains("Admin")      ? "Admin"
                         : roles.Contains("Supervisor") ? "Supervisor"
                         : roles.Contains("Mechanic")   ? "Mechanic"
                         : roles.Contains("Operator")   ? "Operator"
                         : roles.FirstOrDefault(),
                DeviceKind:deviceKind,
                IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString()),
            action:           AuditActions.UserSignedIn,
            targetType:       "User",
            targetId:         null,
            payloadJson:      null,
            occurredAtClient: DateTime.UtcNow);

        // Resolve competencies once and reuse — they're small and the
        // mobile uses them to gate machine selection without a follow-up
        // round trip.
        var competencies = await _competency.GetCurrentSummaryAsync(user.Id);
        var competencyDtos = competencies
            .Select(c => new CompetencySummaryDto
            {
                MachineType = (int)c.MachineType,
                ExpiresAt   = c.ExpiresAt
            }).ToList();

        return Ok(new SyncLoginResponse
        {
            Token     = token,
            ExpiresAt = exp,
            User      = new SyncUserDto
            {
                Id             = user.Id,
                FullName       = user.FullName,
                Email          = user.Email ?? "",
                EmployeeNumber = user.EmployeeNumber,
                Roles          = roles.ToArray(),
                Competencies   = competencyDtos
            }
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                              CURRENT USER
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<SyncUserDto>> Me()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        var roles = await _users.GetRolesAsync(user);

        // Fresh competency snapshot — /me is called periodically by the
        // mobile so the cached competencies stay current.
        var competencies = await _competency.GetCurrentSummaryAsync(user.Id);
        var competencyDtos = competencies
            .Select(c => new CompetencySummaryDto
            {
                MachineType = (int)c.MachineType,
                ExpiresAt   = c.ExpiresAt
            }).ToList();

        return Ok(new SyncUserDto
        {
            Id             = user.Id,
            FullName       = user.FullName,
            Email          = user.Email ?? "",
            EmployeeNumber = user.EmployeeNumber,
            Roles          = roles.ToArray(),
            Competencies   = competencyDtos
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                        MACHINES ASSIGNED TO ME
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("machines")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<List<SyncMachineSummaryDto>>> Machines()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var assignments = await _db.MachineAssignments
            .Include(a => a.Machine).ThenInclude(m => m.Template)
            .Where(a => a.OperatorId == user.Id && a.IsActive)
            .ToListAsync();

        var dtos = assignments.Select(a => new SyncMachineSummaryDto
        {
            Id                = a.Machine.Id,
            MachineNumber     = a.Machine.MachineNumber,
            MachineName       = a.Machine.MachineName,
            Type              = (int)a.Machine.Type,
            TypeDisplay       = a.Machine.TypeDisplay(),
            Description       = a.Machine.Description,
            IsImmobilised     = a.Machine.IsImmobilised,
            ImmobilisedReason = a.Machine.ImmobilisedReason,
            HasTemplate       = a.Machine.Template != null
        }).ToList();

        return Ok(dtos);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       FULL TEMPLATE FOR A MACHINE
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("machines/{machineId:int}/template")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<SyncTemplateDto>> Template(int machineId)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        // Confirm the operator is actually assigned to this machine before
        // handing over the template (defence-in-depth — JWT alone is not enough).
        var assigned = await _db.MachineAssignments.AnyAsync(a =>
            a.MachineId == machineId && a.OperatorId == user.Id && a.IsActive);
        if (!assigned) return Forbid();

        var machine = await _db.Machines
            .Include(m => m.Template).ThenInclude(t => t!.Items)
            .FirstOrDefaultAsync(m => m.Id == machineId);
        if (machine?.Template == null) return NotFound();

        return Ok(new SyncTemplateDto
        {
            MachineId  = machine.Id,
            TemplateId = machine.Template.Id,
            Name       = machine.Template.Name,
            Items      = machine.Template.Items
                .OrderBy(i => i.SortOrder)
                .Select(i => new SyncTemplateItemDto
                {
                    Id               = i.Id,
                    ItemName         = i.ItemName,
                    Section          = i.Section,
                    SortOrder        = i.SortOrder,
                    IsNoGoItem       = i.IsNoGoItem,
                    StatusLabel      = i.StatusLabel,
                    Action           = i.Action,
                    InOrderCondition = i.InOrderCondition,
                    DefectCondition  = i.DefectCondition,
                    IconPath         = i.IconPath
                })
                .ToList()
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                          SUBMIT A CHECKLIST
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("submissions")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<SyncSubmissionResponse>> Submit([FromBody] SyncSubmissionRequest req)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        if (req == null || req.MachineId <= 0)
            return BadRequest(new { error = "machineId is required." });
        if (req.LocalId == Guid.Empty)
            return BadRequest(new { error = "localId is required for idempotency." });
        if (req.Shift == 0)
            return BadRequest(new { error = "shift is required." });
        if (string.IsNullOrWhiteSpace(req.OperatorSignature))
            return BadRequest(new { error = "operatorSignature is required." });

        // Idempotency: client retries with the same LocalId → return the
        // already-persisted submission instead of inserting a duplicate.
        var existing = await _db.ChecklistSubmissions
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.LocalId == req.LocalId);
        if (existing != null)
        {
            return Ok(new SyncSubmissionResponse
            {
                SubmissionId = existing.Id,
                Status       = existing.Status,
                DefectCount  = existing.Items.Count(i => i.Status == ItemStatus.Defect)
            });
        }

        // Defence-in-depth: only the operator assigned to this machine can
        // submit for it.
        var assigned = await _db.MachineAssignments.AnyAsync(a =>
            a.MachineId == req.MachineId && a.OperatorId == user.Id && a.IsActive);
        if (!assigned) return Forbid();

        // ── Competency gate (MHSA Section 22(a)) ───────────────────────
        // Look up the machine type so we can verify the operator holds a
        // current, non-revoked competency for it. Admins bypass inside
        // IsCompetentAsync.
        var machine = await _db.Machines
            .Where(m => m.Id == req.MachineId)
            .Select(m => new { m.Type, m.MachineNumber })
            .FirstOrDefaultAsync();
        if (machine == null)
            return BadRequest(new { error = "Machine not found." });

        var competent = await _competency.IsCompetentAsync(user.Id, machine.Type);
        if (!competent)
        {
            // Audit the BLOCKED event distinctly from the soft "attempted"
            // one the mobile fires — together they show whether the mobile
            // gate is doing its job (we should see few blocked events if
            // the operator's phone is up to date).
            try
            {
                await _audit.LogAsync(
                    actor:            new AuditService.ActorContext(
                        UserId:    user.Id,
                        Name:      user.FullName,
                        Email:     user.Email,
                        Role:      "Operator",
                        DeviceKind:HttpContext.Request.Headers["User-Agent"].ToString()
                                       .Contains("Android", StringComparison.OrdinalIgnoreCase) ? "android" : "web",
                        IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString()),
                    action:           AuditActions.SubmissionBlockedNoCompetency,
                    targetType:       "Machine",
                    targetId:         req.MachineId,
                    payloadJson:      System.Text.Json.JsonSerializer.Serialize(new
                    {
                        machineNumber = machine.MachineNumber,
                        machineType   = machine.Type.ToString()
                    }),
                    occurredAtClient: DateTime.UtcNow);
            }
            catch { /* audit failure must not block the response */ }

            return StatusCode(403, new
            {
                error       = "You don't hold a current competency for this machine type.",
                code        = "no_competency",
                machineType = machine.Type.ToString()
            });
        }

        var dto = new SubmitChecklistDto
        {
            MachineId                = req.MachineId,
            Shift                    = req.Shift,
            KmOrHourMeter            = req.KmOrHourMeter,
            OperatorRemarks          = req.OperatorRemarks,
            FitnessDeclarationSigned = req.FitnessDeclarationSigned,
            OperatorSignature        = req.OperatorSignature,
            Items                    = req.Items ?? new List<SubmissionItemDto>()
        };

        try
        {
            var submission = await _svc.ProcessSubmissionAsync(dto, user.Id);

            // Backfill the LocalId so future retries dedupe correctly.
            submission.LocalId = req.LocalId;
            if (req.SubmittedAt != default) submission.SubmittedAt = req.SubmittedAt;
            await _db.SaveChangesAsync();

            return Ok(new SyncSubmissionResponse
            {
                SubmissionId = submission.Id,
                Status       = submission.Status,
                DefectCount  = submission.Items.Count(i => i.Status == ItemStatus.Defect)
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                         DASHBOARD STATS (role-aware)
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("stats")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<SyncOperatorStatsDto>> Stats()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var today = DateTime.UtcNow.Date;
        var roles = await _users.GetRolesAsync(user);
        var isSupervisor = roles.Contains("Supervisor") || roles.Contains("Admin");

        if (isSupervisor)
        {
            // ── Supervisor: stats over the whole team ──
            var isAdmin = roles.Contains("Admin");

            List<string> teamOperatorIds = isAdmin
                ? await _db.Users.Select(u => u.Id).ToListAsync()
                : await _db.OperatorSupervisorAssignments
                    .Where(a => a.SupervisorId == user.Id && a.IsActive)
                    .Select(a => a.OperatorId)
                    .ToListAsync();

            // Machines visible to the supervisor: ones any of their operators are assigned to,
            // fall back to the whole fleet for Admins so the dashboard is meaningful.
            List<bool> machineFlags;
            if (isAdmin)
            {
                machineFlags = await _db.Machines.Select(m => m.IsImmobilised).ToListAsync();
            }
            else
            {
                var machineIds = await _db.MachineAssignments
                    .Where(a => teamOperatorIds.Contains(a.OperatorId) && a.IsActive)
                    .Select(a => a.MachineId)
                    .Distinct()
                    .ToListAsync();
                machineFlags = await _db.Machines
                    .Where(m => machineIds.Contains(m.Id))
                    .Select(m => m.IsImmobilised)
                    .ToListAsync();
            }

            var todaysChecks = await _db.ChecklistSubmissions
                .CountAsync(s => teamOperatorIds.Contains(s.OperatorId) && s.SubmittedAt >= today);
            var openDefects = await _db.DefectOrders
                .CountAsync(d => teamOperatorIds.Contains(d.Submission.OperatorId)
                              && d.RepairStatus != RepairStatus.Completed);

            return Ok(new SyncOperatorStatsDto
            {
                TotalMachines = machineFlags.Count,
                Operational   = machineFlags.Count(b => !b),
                Immobilised   = machineFlags.Count(b => b),
                OpenDefects   = openDefects,
                TodaysChecks  = todaysChecks
            });
        }

        // ── Operator: stats scoped to themselves ──
        var assignedMachineIds = await _db.MachineAssignments
            .Where(a => a.OperatorId == user.Id && a.IsActive)
            .Select(a => a.MachineId)
            .ToListAsync();

        var opMachineFlags = await _db.Machines
            .Where(m => assignedMachineIds.Contains(m.Id))
            .Select(m => m.IsImmobilised)
            .ToListAsync();

        var opTodaysChecks = await _db.ChecklistSubmissions
            .CountAsync(s => s.OperatorId == user.Id && s.SubmittedAt >= today);

        var opOpenDefects = await _db.DefectOrders
            .CountAsync(d => d.Submission.OperatorId == user.Id
                          && d.RepairStatus != RepairStatus.Completed);

        return Ok(new SyncOperatorStatsDto
        {
            TotalMachines = opMachineFlags.Count,
            Operational   = opMachineFlags.Count(b => !b),
            Immobilised   = opMachineFlags.Count(b => b),
            OpenDefects   = opOpenDefects,
            TodaysChecks  = opTodaysChecks
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //         Phase 6.7 — OPERATOR "AWAITING RE-CHECK" QUEUE
    // ══════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// Returns the machines this operator raised NO-GO on, that admin has
    /// since cleared back to service, and where the operator hasn't done
    /// a fresh checklist yet. Mobile dashboard renders these as a tile
    /// at the top of the home screen — the visual companion to the
    /// Phase 6.1 push notification (operator may have signed in on a
    /// different device + missed the push).
    ///
    /// "Hasn't done a fresh checklist" means: no ChecklistSubmission by
    /// this operator on this machine where SubmittedAt > ClearedAt. We
    /// scope to clearances within the last 14 days so an old clearance
    /// the operator silently ignored doesn't haunt them forever.
    /// </summary>
    [HttpGet("operator/awaiting-recheck")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<List<AwaitingRecheckDto>>> AwaitingRecheck()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var cutoff = DateTime.UtcNow.AddDays(-14);

        // Recent NO-GOs raised by this operator, with the machine in
        // its current state. We only care about machines that have a
        // ClearedAt > the operator's NO-GO submission AND no later
        // submission by this operator on the same machine.
        var myRecentNoGos = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Where(s => s.OperatorId == user.Id
                     && s.Status     == ChecklistStatus.NoGo
                     && s.SubmittedAt >= cutoff)
            .OrderByDescending(s => s.SubmittedAt)
            .ToListAsync();

        if (myRecentNoGos.Count == 0)
            return Ok(new List<AwaitingRecheckDto>());

        // For each candidate machine, find the operator's most recent
        // submission on that machine, and the machine's most recent
        // clearance timestamp. If cleared-after-NO-GO AND no submission
        // since clearance → it's awaiting re-check.
        var machineIds = myRecentNoGos.Select(s => s.Machine.Id).Distinct().ToList();

        var latestSubByMachine = await _db.ChecklistSubmissions
            .Where(s => s.OperatorId == user.Id && machineIds.Contains(s.MachineId))
            .GroupBy(s => s.MachineId)
            .Select(g => new { MachineId = g.Key, LastAt = g.Max(s => s.SubmittedAt) })
            .ToDictionaryAsync(x => x.MachineId, x => x.LastAt);

        var now = DateTime.UtcNow;
        var dto = new List<AwaitingRecheckDto>();
        // Dedupe by machine — if the operator raised three NO-GOs on the
        // same machine we still only show one tile.
        var seenMachines = new HashSet<int>();
        foreach (var s in myRecentNoGos)
        {
            if (!seenMachines.Add(s.Machine.Id))   continue;
            if (!s.Machine.ClearedAt.HasValue)     continue;       // not yet cleared
            if (s.Machine.IsImmobilised)           continue;       // back down again
            if (s.Machine.ClearedAt.Value <= s.SubmittedAt) continue; // clearance is older than the NO-GO

            var lastSubAt = latestSubByMachine.TryGetValue(s.Machine.Id, out var t) ? t : DateTime.MinValue;
            if (lastSubAt > s.Machine.ClearedAt.Value) continue;   // already re-checked

            dto.Add(new AwaitingRecheckDto
            {
                MachineId           = s.Machine.Id,
                MachineNumber       = s.Machine.MachineNumber,
                MachineName         = s.Machine.MachineName,
                TypeDisplay         = s.Machine.TypeName ?? s.Machine.Type.ToString(),
                OriginalNoGoAt      = s.SubmittedAt,
                ClearedAt           = s.Machine.ClearedAt,
                AdminClearanceNotes = s.Machine.AdminClearanceNotes,
                HoursSinceCleared   = (int)(now - s.Machine.ClearedAt.Value).TotalHours
            });
        }

        return Ok(dto);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       RECENT SUBMISSIONS (role-aware)
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("submissions/recent")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<List<RecentSubmissionDto>>> Recent(int take = 30)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        take = Math.Clamp(take, 1, 200);
        var roles        = await _users.GetRolesAsync(user);
        var isSupervisor = roles.Contains("Supervisor") || roles.Contains("Admin");
        var isAdmin      = roles.Contains("Admin");

        IQueryable<ChecklistSubmission> q = _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items);

        if (isSupervisor)
        {
            // Team view: every submission by an operator under this supervisor.
            // Admins see everything.
            if (!isAdmin)
            {
                var teamIds = await _db.OperatorSupervisorAssignments
                    .Where(a => a.SupervisorId == user.Id && a.IsActive)
                    .Select(a => a.OperatorId)
                    .ToListAsync();
                q = q.Where(s => teamIds.Contains(s.OperatorId));
            }
        }
        else
        {
            q = q.Where(s => s.OperatorId == user.Id);
        }

        var subs = await q
            .OrderByDescending(s => s.SubmittedAt)
            .Take(take)
            .Select(s => new RecentSubmissionDto
            {
                SubmissionId  = s.Id,
                MachineName   = s.Machine.MachineName,
                MachineNumber = s.Machine.MachineNumber,
                OperatorName  = s.Operator.FullName,
                Status        = s.Status,
                SubmittedAt   = s.SubmittedAt,
                ItemCount     = s.Items.Count,
                DefectCount   = s.Items.Count(i => i.Status == ItemStatus.Defect),
                Shift         = s.Shift,
                KmOrHourMeter = s.KmOrHourMeter
            })
            .ToListAsync();
        return Ok(subs);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                        SUPERVISOR · SIGN-OFF QUEUE
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("supervisor/queue")]
    [Authorize(Roles = "Supervisor,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<List<SupervisorQueueItemDto>>> SupervisorQueue()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        var roles   = await _users.GetRolesAsync(user);
        var isAdmin = roles.Contains("Admin");

        IQueryable<ChecklistSubmission> q = _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Items)
            .Where(s => s.Status == ChecklistStatus.GoButRepair24H && s.SupervisorId == null);

        if (!isAdmin)
        {
            var teamIds = await _db.OperatorSupervisorAssignments
                .Where(a => a.SupervisorId == user.Id && a.IsActive)
                .Select(a => a.OperatorId)
                .ToListAsync();
            q = q.Where(s => teamIds.Contains(s.OperatorId));
        }

        var items = await q
            .OrderBy(s => s.SubmittedAt)
            .Select(s => new SupervisorQueueItemDto
            {
                SubmissionId           = s.Id,
                MachineNumber          = s.Machine.MachineNumber,
                MachineName            = s.Machine.MachineName,
                OperatorName           = s.Operator.FullName,
                OperatorEmployeeNumber = s.Operator.EmployeeNumber,
                SubmittedAt            = s.SubmittedAt,
                Shift                  = s.Shift,
                KmOrHourMeter          = s.KmOrHourMeter,
                DefectCount            = s.Items.Count(i => i.Status == ItemStatus.Defect),
                Status                 = s.Status
            })
            .ToListAsync();
        return Ok(items);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       SUPERVISOR · OPERATOR ROSTER
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("supervisor/operators")]
    [Authorize(Roles = "Supervisor,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<List<SupervisorOperatorDto>>> SupervisorOperators()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        var roles   = await _users.GetRolesAsync(user);
        var isAdmin = roles.Contains("Admin");
        var since   = DateTime.UtcNow.Date.AddDays(-30);

        IQueryable<OperatorSupervisorAssignment> q = _db.OperatorSupervisorAssignments
            .Include(a => a.Operator)
            .Where(a => a.IsActive);
        if (!isAdmin) q = q.Where(a => a.SupervisorId == user.Id);

        var assignments = await q.ToListAsync();
        var operatorIds = assignments.Select(a => a.OperatorId).ToList();

        var perOperator = await _db.ChecklistSubmissions
            .Where(s => operatorIds.Contains(s.OperatorId) && s.SubmittedAt >= since)
            .GroupBy(s => s.OperatorId)
            .Select(g => new
            {
                OperatorId = g.Key,
                Total      = g.Count(),
                Pending    = g.Count(s => s.Status == ChecklistStatus.GoButRepair24H && s.SupervisorId == null),
                NoGo       = g.Count(s => s.Status == ChecklistStatus.NoGo),
                LastAt     = (DateTime?)g.Max(s => s.SubmittedAt)
            })
            .ToListAsync();
        var statsByOp = perOperator.ToDictionary(x => x.OperatorId);

        var dto = assignments.Select(a =>
        {
            statsByOp.TryGetValue(a.OperatorId, out var s);
            return new SupervisorOperatorDto
            {
                UserId           = a.Operator.Id,
                FullName         = a.Operator.FullName,
                EmployeeNumber   = a.Operator.EmployeeNumber,
                Email            = a.Operator.Email ?? "",
                Submissions30d   = s?.Total   ?? 0,
                PendingSignOff   = s?.Pending ?? 0,
                NoGoCount30d     = s?.NoGo    ?? 0,
                LastSubmissionAt = s?.LastAt
            };
        })
        .OrderBy(o => o.FullName)
        .ToList();
        return Ok(dto);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                  SUPERVISOR · SINGLE SUBMISSION REVIEW
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("supervisor/queue/{id:int}")]
    [Authorize(Roles = "Supervisor,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<SupervisorReviewDto>> SupervisorReview(int id)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var s = await _db.ChecklistSubmissions
            .Include(x => x.Machine)
            .Include(x => x.Operator)
            .Include(x => x.Items).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();

        // Defence-in-depth: a non-admin supervisor may only review submissions
        // belonging to their assigned operators.
        var roles = await _users.GetRolesAsync(user);
        if (!roles.Contains("Admin"))
        {
            var teamIds = await _db.OperatorSupervisorAssignments
                .Where(a => a.SupervisorId == user.Id && a.IsActive)
                .Select(a => a.OperatorId)
                .ToListAsync();
            if (!teamIds.Contains(s.OperatorId)) return Forbid();
        }

        return Ok(new SupervisorReviewDto
        {
            SubmissionId             = s.Id,
            Status                   = s.Status,
            MachineNumber            = s.Machine.MachineNumber,
            MachineName              = s.Machine.MachineName,
            MachineType              = s.Machine.TypeDisplay(),
            OperatorName             = s.Operator.FullName,
            OperatorEmployeeNumber   = s.Operator.EmployeeNumber,
            SubmittedAt              = s.SubmittedAt,
            Shift                    = s.Shift,
            KmOrHourMeter            = s.KmOrHourMeter,
            OperatorRemarks          = s.OperatorRemarks,
            OperatorSignature        = s.OperatorSignature,
            FitnessDeclarationSigned = s.FitnessDeclarationSigned,
            Items = s.Items
                .OrderBy(i => i.TemplateItem.SortOrder)
                .Select(i => new SupervisorReviewItemDto
                {
                    TemplateItemId = i.TemplateItem.Id,
                    ItemName       = i.TemplateItem.ItemName,
                    IconPath       = i.TemplateItem.IconPath,
                    IsNoGoItem     = i.TemplateItem.IsNoGoItem,
                    Status         = i.Status,
                    Notes          = i.Notes,
                    // Defect photo — base64 only if the operator attached one.
                    // The supervisor/operator UI shows it as a thumbnail next
                    // to the item; the PDF renderer embeds it inline.
                    PhotoBase64    = i.PhotoData == null || i.PhotoData.Length == 0
                                         ? null
                                         : Convert.ToBase64String(i.PhotoData),
                    PhotoMimeType  = i.PhotoMimeType,
                    // Same shape for the voice memo — base64 only if recorded.
                    // The UI renders an HTML5 <audio controls> using a
                    // data: URL built from this string.
                    AudioBase64    = i.AudioData == null || i.AudioData.Length == 0
                                         ? null
                                         : Convert.ToBase64String(i.AudioData),
                    AudioMimeType  = i.AudioMimeType
                })
                .ToList()
        });
    }

    [HttpPost("supervisor/queue/{id:int}/signoff")]
    [Authorize(Roles = "Supervisor,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult> SupervisorSignOffApi(int id, [FromBody] SupervisorSignOffRequest req)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        if (req == null || string.IsNullOrWhiteSpace(req.Signature))
            return BadRequest(new { error = "A digital signature is required to approve." });

        var resolved = req.Resolution == 3
            ? ChecklistStatus.GoTillNextService
            : ChecklistStatus.GoButRepair24H;

        try
        {
            await _svc.SupervisorSignOffAsync(id, user.Id, resolved, req.Signature);
            return Ok(new { ok = true });
        }
        catch (ChecklistService.ConflictException cx)
        {
            // Lost the race against another supervisor's earlier sign-off.
            // Write a notification for the caller so they see the loss in
            // their inbox even if the device was offline at the moment
            // the winner's sign-off landed.
            await _notifications.PushAsync(
                userId:              user.Id,
                kind:                NotificationKinds.ConflictRejected,
                title:               $"✕ Sign-off rejected — {cx.MachineNumber ?? "submission"}",
                body:                cx.Message,
                relatedSubmissionId: id);
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = cx.Message, conflict = true, winner = cx.WinnerName });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                    SUPERVISOR · AVAILABLE MECHANICS
    // Used by the Reject modal so the supervisor can pick who owns the resulting
    // defect orders. Ordered by lowest current workload (open jobs ascending).
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("supervisor/mechanics")]
    [Authorize(Roles = "Supervisor,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<List<SupervisorMechanicDto>>> SupervisorMechanics()
    {
        // GetUsersInRoleAsync hits AspNetUserRoles + AspNetUsers in one round-trip.
        var mechanics = (await _users.GetUsersInRoleAsync("Mechanic"))
            .Where(u => u.IsActive)
            .ToList();
        var mechIds = mechanics.Select(m => m.Id).ToList();

        // Load open-job counts per mechanic so the picker can sort by workload.
        var loads = await _db.DefectOrders
            .Where(d => d.AssignedMechanicId != null
                     && mechIds.Contains(d.AssignedMechanicId!)
                     && d.RepairStatus != RepairStatus.Completed)
            .GroupBy(d => d.AssignedMechanicId!)
            .Select(g => new { MechanicId = g.Key, Count = g.Count() })
            .ToListAsync();
        var loadByMech = loads.ToDictionary(x => x.MechanicId, x => x.Count);

        var dto = mechanics
            .Select(m => new SupervisorMechanicDto
            {
                Id             = m.Id,
                FullName       = m.FullName,
                EmployeeNumber = m.EmployeeNumber,
                Email          = m.Email ?? "",
                OpenJobs       = loadByMech.TryGetValue(m.Id, out var n) ? n : 0
            })
            .OrderBy(m => m.OpenJobs)
            .ThenBy(m => m.FullName)
            .ToList();

        return Ok(dto);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       SUPERVISOR · REJECT SUBMISSION
    // Mirrors the web's SupervisorController.Reject — flips the submission to
    // Rejected, immobilises the machine, and creates DefectOrders for every
    // defective item assigned to the chosen mechanic. Idempotent on defect
    // creation (skips items that already have an open order).
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("supervisor/queue/{id:int}/reject")]
    [Authorize(Roles = "Supervisor,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult> SupervisorReject(int id, [FromBody] SupervisorRejectRequest req)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        if (req == null || string.IsNullOrWhiteSpace(req.Reason))
            return BadRequest(new { error = "A rejection reason is required." });
        if (string.IsNullOrWhiteSpace(req.MechanicId))
            return BadRequest(new { error = "A mechanic must be assigned." });

        var submission = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Supervisor)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (submission == null) return NotFound();

        // Defence-in-depth: a non-admin supervisor can only reject submissions
        // belonging to their assigned operators.
        var roles = await _users.GetRolesAsync(user);
        if (!roles.Contains("Admin"))
        {
            var teamIds = await _db.OperatorSupervisorAssignments
                .Where(a => a.SupervisorId == user.Id && a.IsActive)
                .Select(a => a.OperatorId)
                .ToListAsync();
            if (!teamIds.Contains(submission.OperatorId)) return Forbid();
        }

        // ── Conflict detection ───────────────────────────────────────────
        // If another supervisor already signed-off OR already rejected this
        // submission, we DO NOT silently overwrite their decision. Notify
        // the loser and return 409.
        if (!string.IsNullOrEmpty(submission.SupervisorId) &&
            submission.SupervisorId != user.Id)
        {
            var winner = submission.Supervisor?.FullName ?? "another supervisor";
            var verdictMsg = submission.Status == ChecklistStatus.Rejected
                ? $"This submission was already rejected by {winner}."
                : $"This submission was already signed off by {winner}.";
            await _notifications.PushAsync(
                userId:              user.Id,
                kind:                NotificationKinds.ConflictRejected,
                title:               $"✕ Rejection rejected — {submission.Machine.MachineNumber}",
                body:                verdictMsg,
                relatedSubmissionId: id,
                relatedMachineId:    submission.MachineId);
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = verdictMsg, conflict = true, winner });
        }

        // Sanity-check the chosen mechanic exists and is actually a mechanic.
        var mech = await _users.FindByIdAsync(req.MechanicId);
        if (mech == null || !mech.IsActive
            || !(await _users.GetRolesAsync(mech)).Contains("Mechanic"))
            return BadRequest(new { error = "Selected mechanic is not valid." });

        // ── Apply the rejection ──
        submission.Status               = ChecklistStatus.Rejected;
        submission.SupervisorId         = user.Id;
        submission.SupervisorSignedAt   = DateTime.UtcNow;
        submission.RejectionReason      = req.Reason.Trim();
        submission.RejectedMechanicId   = req.MechanicId;

        // Machine is unfit to operate — immobilise it with a clear audit trail.
        submission.Machine.IsImmobilised     = true;
        submission.Machine.ImmobilisedReason = $"Supervisor rejected checklist on {DateTime.UtcNow:yyyy-MM-dd HH:mm}. Reason: {req.Reason.Trim()}";

        // Route every defective item to the chosen mechanic. Skip items that
        // already have an open order so retries don't double-create.
        var defects = submission.Items.Where(i => i.Status == ItemStatus.Defect).ToList();
        int created = 0;
        foreach (var item in defects)
        {
            var alreadyExists = await _db.DefectOrders.AnyAsync(d =>
                d.SubmissionItemId == item.Id &&
                d.RepairStatus != RepairStatus.Completed);
            if (alreadyExists) continue;

            var desc = item.Notes ?? item.TemplateItem.ItemName;
            if (desc.Length > 200) desc = desc.Substring(0, 200);

            _db.DefectOrders.Add(new DefectOrder
            {
                SubmissionId       = submission.Id,
                SubmissionItemId   = item.Id,
                DefectDescription  = desc,
                AssignedMechanicId = req.MechanicId,
                RepairStatus       = RepairStatus.InProgress,
                CreatedAt          = DateTime.UtcNow
            });
            created++;
        }

        await _db.SaveChangesAsync();

        // ── Audit ─────────────────────────────────────────────────────────
        // Two rows: the supervisor's reject action and the resulting machine
        // immobilisation. Investigators following a chain typically jump
        // from "what happened to submission X" → "what happened to machine Y"
        // so keeping both available makes the trail walkable.
        await _audit.LogAsync(
            action:     AuditActions.SubmissionRejected,
            targetType: "Submission",
            targetId:   submission.Id,
            payload:    new
            {
                reason       = req.Reason.Trim(),
                mechanicId   = req.MechanicId,
                mechanicName = mech.FullName,
                defectOrdersCreated = created,
                operatorId   = submission.OperatorId
            });
        await _audit.LogAsync(
            action:     AuditActions.MachineImmobilised,
            targetType: "Machine",
            targetId:   submission.Machine.Id,
            payload:    new
            {
                reason          = "supervisor-rejected checklist",
                bySubmissionId  = submission.Id,
                bySupervisorId  = user.Id
            });

        // ── Notify the assigned mechanic ──
        // The mechanic needs to know there's a NO-GO machine and N defects
        // waiting for them. Email failures must not break the API response.
        try
        {
            await _email.SendRejectionNotificationAsync(
                mechanicEmail:  mech.Email ?? "",
                mechanicName:   mech.FullName,
                operatorName:   submission.Operator.FullName,
                machineNumber:  submission.Machine.MachineNumber,
                machineName:    submission.Machine.MachineName,
                reason:         req.Reason.Trim(),
                defectCount:    created,
                supervisorName: user.FullName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rejection-notification email failed for submission {SubmissionId} " +
                                "(mobile API). Reject persisted; email pipeline was skipped.", id);
        }

        // Notify the operator in-app so they don't walk to the machine
        // next shift and find it immobilised with no context.
        try
        {
            await _notifications.PushAsync(
                userId:              submission.OperatorId,
                kind:                NotificationKinds.SubmissionRejected,
                title:               $"🛑 Rejected on {submission.Machine.MachineNumber}",
                body:                $"Reason: {req.Reason.Trim()}",
                relatedSubmissionId: submission.Id,
                relatedMachineId:    submission.MachineId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Rejection in-app notification failed for submission {SubmissionId}.", id);
        }

        return Ok(new { ok = true, defectsCreated = created });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       SUPERVISOR · NO-GO MACHINES
    // (also visible to Mechanics — they need the same fleet view)
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("supervisor/nogo")]
    [Authorize(Roles = "Supervisor,Admin,Mechanic",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<List<NoGoMachineDto>>> SupervisorNoGo()
    {
        var machines = await _db.Machines
            .Include(m => m.Assignments).ThenInclude(a => a.Operator)
            .Include(m => m.Assignments).ThenInclude(a => a.Mechanic)
            .Where(m => m.IsImmobilised)
            .OrderBy(m => m.MachineNumber)
            .ToListAsync();

        var dto = new List<NoGoMachineDto>(machines.Count);
        foreach (var m in machines)
        {
            var activeAssignment = m.Assignments.FirstOrDefault(a => a.IsActive);
            var openDefects = await _db.DefectOrders
                .CountAsync(d => d.Submission.MachineId == m.Id
                              && d.RepairStatus != RepairStatus.Completed);

            dto.Add(new NoGoMachineDto
            {
                MachineId         = m.Id,
                MachineNumber     = m.MachineNumber,
                MachineName       = m.MachineName,
                TypeDisplay       = m.TypeDisplay(),
                ImmobilisedReason = m.ImmobilisedReason,
                OpenDefects       = openDefects,
                AssignedOperator  = activeAssignment?.Operator?.FullName,
                AssignedMechanic  = activeAssignment?.Mechanic?.FullName
            });
        }
        return Ok(dto);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                          MECHANIC · DEFECT QUEUE
    // Returns "my open orders" (assigned to the caller, not yet completed) plus
    // the pool of unassigned pending orders the caller could claim.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("mechanic/defects")]
    [Authorize(Roles = "Mechanic,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<MechanicQueueDto>> MechanicDefects()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        IQueryable<DefectOrder> baseQ = _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.Submission).ThenInclude(s => s.Operator)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Include(d => d.AssignedMechanic);

        var mine = await baseQ
            .Where(d => d.AssignedMechanicId == user.Id
                     && d.RepairStatus != RepairStatus.Completed)
            // NO-GO machines first, then oldest open job
            .OrderByDescending(d => d.Submission.Machine.IsImmobilised)
            .ThenBy(d => d.CreatedAt)
            .ToListAsync();

        var unassigned = await baseQ
            .Where(d => d.AssignedMechanicId == null
                     && d.RepairStatus == RepairStatus.Pending)
            .OrderByDescending(d => d.Submission.Machine.IsImmobilised)
            .ThenBy(d => d.CreatedAt)
            .ToListAsync();

        MechanicDefectDto Map(DefectOrder d, bool assignedToMe) => new()
        {
            DefectOrderId        = d.Id,
            SubmissionId         = d.SubmissionId,
            SubmissionItemId     = d.SubmissionItemId,
            MachineId            = d.Submission.MachineId,
            MachineNumber        = d.Submission.Machine.MachineNumber,
            MachineName          = d.Submission.Machine.MachineName,
            TypeDisplay          = d.Submission.Machine.TypeDisplay(),
            MachineImmobilised   = d.Submission.Machine.IsImmobilised,
            ItemName             = d.SubmissionItem.TemplateItem.ItemName,
            IconPath             = d.SubmissionItem.TemplateItem.IconPath,
            IsCriticalItem       = d.SubmissionItem.TemplateItem.IsNoGoItem,
            DefectDescription    = d.DefectDescription,
            OperatorNotes        = d.SubmissionItem.Notes,
            OperatorName         = d.Submission.Operator.FullName,
            CreatedAt            = d.CreatedAt,
            RepairStatus         = d.RepairStatus,
            PartRequired         = d.PartRequired,
            PartNumber           = d.PartNumber,
            AssignedMechanicId   = d.AssignedMechanicId,
            AssignedMechanicName = d.AssignedMechanic?.FullName,
            IsAssignedToMe       = assignedToMe
        };

        return Ok(new MechanicQueueDto
        {
            MyOrders   = mine.Select(d => Map(d, true)).ToList(),
            Unassigned = unassigned.Select(d => Map(d, false)).ToList()
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       MECHANIC · SINGLE DEFECT DETAIL
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("mechanic/defects/{id:int}")]
    [Authorize(Roles = "Mechanic,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<MechanicDefectDto>> MechanicDefect(int id)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var d = await _db.DefectOrders
            .Include(x => x.Submission).ThenInclude(s => s.Machine)
            .Include(x => x.Submission).ThenInclude(s => s.Operator)
            .Include(x => x.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Include(x => x.AssignedMechanic)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (d == null) return NotFound();

        return Ok(new MechanicDefectDto
        {
            DefectOrderId        = d.Id,
            SubmissionId         = d.SubmissionId,
            SubmissionItemId     = d.SubmissionItemId,
            MachineId            = d.Submission.MachineId,
            MachineNumber        = d.Submission.Machine.MachineNumber,
            MachineName          = d.Submission.Machine.MachineName,
            TypeDisplay          = d.Submission.Machine.TypeDisplay(),
            MachineImmobilised   = d.Submission.Machine.IsImmobilised,
            ItemName             = d.SubmissionItem.TemplateItem.ItemName,
            IconPath             = d.SubmissionItem.TemplateItem.IconPath,
            IsCriticalItem       = d.SubmissionItem.TemplateItem.IsNoGoItem,
            DefectDescription    = d.DefectDescription,
            OperatorNotes        = d.SubmissionItem.Notes,
            OperatorName         = d.Submission.Operator.FullName,
            CreatedAt            = d.CreatedAt,
            RepairStatus         = d.RepairStatus,
            PartRequired         = d.PartRequired,
            PartNumber           = d.PartNumber,
            AssignedMechanicId   = d.AssignedMechanicId,
            AssignedMechanicName = d.AssignedMechanic?.FullName,
            IsAssignedToMe       = d.AssignedMechanicId == user.Id
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       MECHANIC · CLAIM AN UNASSIGNED DEFECT
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("mechanic/defects/{id:int}/claim")]
    [Authorize(Roles = "Mechanic,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult> MechanicClaim(int id)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var order = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.AssignedMechanic)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (order == null) return NotFound();

        // ── Conflict detection ───────────────────────────────────────────
        // The other mechanic-action endpoints don't have this race today
        // (they're protected by RepairStatus transitions) but the bare
        // Claim is the classic example — two mechanics tap "Claim" offline,
        // one wins, the other's drain has to be told they lost so the
        // bell badge surfaces the news instead of silently dropping.
        if (order.AssignedMechanicId != null && order.AssignedMechanicId != user.Id)
        {
            var winner = order.AssignedMechanic?.FullName ?? "another mechanic";
            var msg = $"This job was already claimed by {winner}.";
            await _notifications.PushAsync(
                userId:              user.Id,
                kind:                NotificationKinds.ConflictRejected,
                title:               $"✕ Claim rejected — {order.Submission.Machine.MachineNumber}",
                body:                msg,
                relatedSubmissionId: order.SubmissionId,
                relatedMachineId:    order.Submission.MachineId);
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = msg, conflict = true, winner });
        }

        order.AssignedMechanicId = user.Id;
        if (order.RepairStatus == RepairStatus.Pending)
            order.RepairStatus = RepairStatus.InProgress;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            action:     AuditActions.DefectClaimed,
            targetType: "DefectOrder",
            targetId:   order.Id,
            payload:    new { submissionId = order.SubmissionId });

        return Ok(new { ok = true });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       MECHANIC · ORDER A PART
    // Mirrors the web app's single-item OrderPart POST. Skips email for now;
    // the mobile-first iteration is just the data transition (Pending →
    // AwaitingParts). Email confirmations are a Phase-3 follow-up.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("mechanic/defects/{id:int}/order-part")]
    [Authorize(Roles = "Mechanic,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult> MechanicOrderPart(int id, [FromBody] OrderPartRequest req)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        if (req == null || string.IsNullOrWhiteSpace(req.PartRequired))
            return BadRequest(new { error = "partRequired is required." });

        // Pull the full graph so we have machine + item names available for the
        // email body / PDF without a second round-trip.
        var order = await _db.DefectOrders
            .Include(d => d.Submission).ThenInclude(s => s.Machine)
            .Include(d => d.SubmissionItem).ThenInclude(i => i.TemplateItem)
            .Include(d => d.AssignedMechanic)
            .FirstOrDefaultAsync(d => d.Id == id);
        if (order == null) return NotFound();

        // ── Conflict detection ───────────────────────────────────────────
        // If another mechanic already owns + ordered a part on this defect,
        // we don't quietly overwrite their part number. They might have
        // already kicked off the procurement workflow on their part. Refuse
        // and tell the loser.
        if (!string.IsNullOrEmpty(order.AssignedMechanicId) &&
            order.AssignedMechanicId != user.Id &&
            order.RepairStatus == RepairStatus.AwaitingParts)
        {
            var winner = order.AssignedMechanic?.FullName ?? "another mechanic";
            var msg = $"This defect already had a part ordered by {winner}.";
            await _notifications.PushAsync(
                userId:              user.Id,
                kind:                NotificationKinds.ConflictRejected,
                title:               $"✕ Part order rejected — {order.Submission.Machine.MachineNumber}",
                body:                msg,
                relatedSubmissionId: order.SubmissionId,
                relatedMachineId:    order.Submission.MachineId);
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = msg, conflict = true, winner });
        }

        // Auto-claim if currently unassigned (the web flow does the same).
        if (string.IsNullOrEmpty(order.AssignedMechanicId))
            order.AssignedMechanicId = user.Id;

        order.PartRequired = req.PartRequired.Trim();
        order.PartNumber   = string.IsNullOrWhiteSpace(req.PartNumber) ? null : req.PartNumber.Trim();
        order.RepairStatus = RepairStatus.AwaitingParts;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            action:     AuditActions.DefectPartOrdered,
            targetType: "DefectOrder",
            targetId:   order.Id,
            payload:    new
            {
                partRequired = order.PartRequired,
                partNumber   = order.PartNumber,
                machineId    = order.Submission.Machine.Id
            });

        // ── Fire confirmation to mechanic + parts-order PDF to manager ──
        // Matches the web flow in MechanicController.OrderPart so notifications
        // arrive whether the parts request was submitted from the web or mobile.
        // Email failures must NOT break the API response — log + swallow.
        try
        {
            if (!string.IsNullOrEmpty(user.Email))
            {
                var orderRef  = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                var lineItems = new List<OrderLineItem>
                {
                    new OrderLineItem
                    {
                        MachineNumber = order.Submission.Machine.MachineNumber,
                        MachineName   = order.Submission.Machine.MachineName,
                        DefectItem    = order.SubmissionItem.TemplateItem.ItemName,
                        PartRequired  = order.PartRequired ?? "",
                        PartNumber    = order.PartNumber
                    }
                };

                var partsOrderPdf = _pdf.GeneratePartsOrderPdf(user.FullName, lineItems, orderRef);
                await _email.SendOrderConfirmationAsync(
                    user.Email, user.FullName, lineItems, orderRef, partsOrderPdf);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Parts-order email failed for defect {DefectId} (mobile API). " +
                                "Order data is saved; email pipeline was skipped.", id);
        }

        return Ok(new { ok = true });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       MECHANIC · COMPLETE A REPAIR
    // Delegates to ChecklistService.ResolveDefectAsync so the same business
    // rules run (status transition, mechanic signature persistence, machine
    // re-mobilisation when all defects are cleared, etc.).
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("mechanic/defects/{id:int}/complete")]
    [Authorize(Roles = "Mechanic,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult> MechanicComplete(int id, [FromBody] CompleteRepairRequest req)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        if (req == null || string.IsNullOrWhiteSpace(req.Signature))
            return BadRequest(new { error = "A digital signature is required to close this repair." });

        try
        {
            await _svc.ResolveDefectAsync(id, user.Id, req.Notes ?? "Repair completed.", req.Signature);
            return Ok(new { ok = true });
        }
        catch (ChecklistService.ConflictException cx)
        {
            // Another mechanic already closed it. Write the conflict
            // notification so the mobile bell badge shows the news, then
            // 409 so the drainer treats it as Permanent (drops the row).
            await _notifications.PushAsync(
                userId:           user.Id,
                kind:             NotificationKinds.ConflictRejected,
                title:            $"✕ Repair close rejected — {cx.MachineNumber ?? "defect"}",
                body:             cx.Message);
            return StatusCode(StatusCodes.Status409Conflict,
                new { error = cx.Message, conflict = true, winner = cx.WinnerName });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                          MECHANIC · DASHBOARD STATS
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("mechanic/stats")]
    [Authorize(Roles = "Mechanic,Admin",
               AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<MechanicStatsDto>> MechanicStats()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var today = DateTime.UtcNow.Date;

        var openAssigned = await _db.DefectOrders.CountAsync(d =>
            d.AssignedMechanicId == user.Id &&
            d.RepairStatus != RepairStatus.Completed);

        var awaitingParts = await _db.DefectOrders.CountAsync(d =>
            d.AssignedMechanicId == user.Id &&
            d.RepairStatus == RepairStatus.AwaitingParts);

        var unassigned = await _db.DefectOrders.CountAsync(d =>
            d.AssignedMechanicId == null &&
            d.RepairStatus == RepairStatus.Pending);

        var completedToday = await _db.DefectOrders.CountAsync(d =>
            d.AssignedMechanicId == user.Id &&
            d.RepairStatus == RepairStatus.Completed &&
            d.ResolvedAt != null &&
            d.ResolvedAt >= today);

        var noGoMachines = await _db.Machines.CountAsync(m => m.IsImmobilised);

        return Ok(new MechanicStatsDto
        {
            OpenAssigned   = openAssigned,
            AwaitingParts  = awaitingParts,
            Unassigned     = unassigned,
            CompletedToday = completedToday,
            NoGoMachines   = noGoMachines
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                       AUDIT · MOBILE BATCH UPLOAD
    // Mobile clients buffer audit events locally (AuditQueue) and drain a
    // batch here when the SyncWorker fires. The endpoint enforces that the
    // actor on every event matches the JWT subject — no impersonation by
    // posting another user's id in the payload.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpPost("audit")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult> AuditBatch([FromBody] AuditEventBatchRequest req)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        if (req == null || req.Events == null || req.Events.Count == 0)
            return Ok(new { saved = 0 });   // benign no-op, drainer treats as success

        // Cap the batch so a stuck client can't DoS the server with a giant
        // dump. 500 is generous — typical drain pass is < 100 events.
        if (req.Events.Count > 500)
            return BadRequest(new { error = "Batch exceeds 500 events." });

        // Build a single actor snapshot from the JWT subject. Every row in
        // the batch is stamped with these values regardless of what the
        // client tried to put in the DTO — defence against an event whose
        // ActorUserId field claims to be someone else.
        var roles = await _users.GetRolesAsync(user);
        string? role = roles.Contains("Admin")      ? "Admin"
                     : roles.Contains("Supervisor") ? "Supervisor"
                     : roles.Contains("Mechanic")   ? "Mechanic"
                     : roles.Contains("Operator")   ? "Operator"
                     : roles.FirstOrDefault();
        var actor = new AuditService.ActorContext(
            UserId:    user.Id,
            Name:      user.FullName,
            Email:     user.Email,
            Role:      role,
            DeviceKind:"android",   // overridden per-event below
            IpAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

        var saved = await _audit.LogBatchAsync(actor, req.Events);
        return Ok(new { saved });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                FULL SUBMISSION DETAILS · role-aware view
    // Returns enough data to render a complete read-only view of the checklist
    // (items + statuses + defect notes + operator remarks + signature). Reuses
    // SupervisorReviewDto because it already has exactly the right shape.
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("submissions/{id:int}/details")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<ActionResult<SupervisorReviewDto>> SubmissionDetails(int id)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var s = await _db.ChecklistSubmissions
            .Include(x => x.Machine)
            .Include(x => x.Operator)
            .Include(x => x.Items).ThenInclude(i => i.TemplateItem)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (s == null) return NotFound();

        // Same role gate as the PDF endpoint — keeps access rules consistent.
        var roles        = await _users.GetRolesAsync(user);
        var isAdmin      = roles.Contains("Admin");
        var isSupervisor = roles.Contains("Supervisor");
        var isMechanic   = roles.Contains("Mechanic");

        bool allowed = isAdmin || s.OperatorId == user.Id;
        if (!allowed && isSupervisor)
        {
            allowed = await _db.OperatorSupervisorAssignments.AnyAsync(a =>
                a.SupervisorId == user.Id && a.IsActive && a.OperatorId == s.OperatorId);
        }
        if (!allowed && isMechanic)
        {
            allowed = await _db.MachineAssignments.AnyAsync(a =>
                a.MachineId == s.MachineId && a.IsActive && a.MechanicId == user.Id);
        }
        if (!allowed) return Forbid();

        return Ok(new SupervisorReviewDto
        {
            SubmissionId             = s.Id,
            Status                   = s.Status,
            MachineNumber            = s.Machine.MachineNumber,
            MachineName              = s.Machine.MachineName,
            MachineType              = s.Machine.TypeDisplay(),
            OperatorName             = s.Operator.FullName,
            OperatorEmployeeNumber   = s.Operator.EmployeeNumber,
            SubmittedAt              = s.SubmittedAt,
            Shift                    = s.Shift,
            KmOrHourMeter            = s.KmOrHourMeter,
            OperatorRemarks          = s.OperatorRemarks,
            OperatorSignature        = s.OperatorSignature,
            FitnessDeclarationSigned = s.FitnessDeclarationSigned,
            Items = s.Items
                .OrderBy(i => i.TemplateItem.SortOrder)
                .Select(i => new SupervisorReviewItemDto
                {
                    TemplateItemId = i.TemplateItem.Id,
                    ItemName       = i.TemplateItem.ItemName,
                    IconPath       = i.TemplateItem.IconPath,
                    IsNoGoItem     = i.TemplateItem.IsNoGoItem,
                    Status         = i.Status,
                    Notes          = i.Notes,
                    // Defect photo — base64 only if the operator attached one.
                    // The supervisor/operator UI shows it as a thumbnail next
                    // to the item; the PDF renderer embeds it inline.
                    PhotoBase64    = i.PhotoData == null || i.PhotoData.Length == 0
                                         ? null
                                         : Convert.ToBase64String(i.PhotoData),
                    PhotoMimeType  = i.PhotoMimeType,
                    // Same shape for the voice memo — base64 only if recorded.
                    // The UI renders an HTML5 <audio controls> using a
                    // data: URL built from this string.
                    AudioBase64    = i.AudioData == null || i.AudioData.Length == 0
                                         ? null
                                         : Convert.ToBase64String(i.AudioData),
                    AudioMimeType  = i.AudioMimeType
                })
                .ToList()
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                          PDF · CHECKLIST SUBMISSION
    // JWT-auth parallel to the web's /Checklist/Pdf/{id}. Returns the rendered
    // PDF bytes so the mobile app can show them in an inline popup (blob URL
    // inside an iframe) rather than launching the device's external browser.
    // Role-aware:
    //   · Operator → only their own submissions
    //   · Supervisor → submissions by operators on their team (+ Admin = all)
    //   · Mechanic → submissions for machines they're assigned to
    //   · Admin → anything
    // ══════════════════════════════════════════════════════════════════════════
    [HttpGet("submissions/{id:int}/pdf")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public async Task<IActionResult> SubmissionPdf(int id)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var sub = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Operator)
            .Include(s => s.Supervisor)
            .Include(s => s.Mechanic)
            .Include(s => s.Items).ThenInclude(i => i.TemplateItem)
            .Include(s => s.DefectOrders).ThenInclude(d => d.AssignedMechanic)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (sub == null) return NotFound();

        var roles = await _users.GetRolesAsync(user);
        var isAdmin      = roles.Contains("Admin");
        var isSupervisor = roles.Contains("Supervisor");
        var isMechanic   = roles.Contains("Mechanic");

        bool allowed = isAdmin || sub.OperatorId == user.Id;

        if (!allowed && isSupervisor)
        {
            allowed = await _db.OperatorSupervisorAssignments.AnyAsync(a =>
                a.SupervisorId == user.Id && a.IsActive && a.OperatorId == sub.OperatorId);
        }

        if (!allowed && isMechanic)
        {
            allowed = await _db.MachineAssignments.AnyAsync(a =>
                a.MachineId == sub.MachineId && a.IsActive && a.MechanicId == user.Id);
        }

        if (!allowed) return Forbid();

        try
        {
            var bytes    = _pdf.GenerateChecklistPdf(sub);
            var fileName = $"Checklist_{sub.Machine.MachineNumber}_{sub.Id}.pdf";
            return File(bytes, "application/pdf", fileName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PDF render failed for submission {SubmissionId}", id);
            return StatusCode(500, new { error = "PDF render failed." });
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                              NOTIFICATIONS · INBOX
    // GET /notifications              — recent N notifications for the caller
    // GET /notifications/unread-count — quick badge count
    // POST /notifications/{id}/read   — mark a single one as read
    // POST /notifications/read-all    — mark everything in inbox as read
    // ══════════════════════════════════════════════════════════════════════════

    // Notifications endpoints accept BOTH the JWT bearer scheme (mobile app)
    // AND the Identity cookie scheme (web browser). Combining them on the
    // attribute lets the same endpoint serve both surfaces — the web's
    // topbar bell hits these same URLs.
    //
    // Why the magic string instead of IdentityConstants.ApplicationScheme:
    // attribute arguments must be compile-time constants, and
    // IdentityConstants.ApplicationScheme is declared as `static readonly`,
    // not `const`. The value "Identity.Application" is hard-coded inside
    // IdentityConstants and has been stable since ASP.NET Core 2.x, so
    // duplicating it here is safe — adding a unit test that asserts
    // NotifAuthSchemes.EndsWith(IdentityConstants.ApplicationScheme) would
    // catch any future framework change at build time.
    private const string NotifAuthSchemes =
        JwtBearerDefaults.AuthenticationScheme + ",Identity.Application";

    [HttpGet("notifications")]
    [Authorize(AuthenticationSchemes = NotifAuthSchemes)]
    public async Task<ActionResult<List<NotificationDto>>> Notifications(int take = 30)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        take = Math.Clamp(take, 1, 200);
        var rows = await _db.Notifications
            .Where(n => n.UserId == user.Id)
            .OrderByDescending(n => n.CreatedAt)
            .Take(take)
            .Select(n => new NotificationDto
            {
                Id                  = n.Id,
                Kind                = n.Kind,
                Title               = n.Title,
                Body                = n.Body,
                RelatedSubmissionId = n.RelatedSubmissionId,
                RelatedMachineId    = n.RelatedMachineId,
                CreatedAt           = n.CreatedAt,
                ReadAt              = n.ReadAt
            })
            .ToListAsync();
        return Ok(rows);
    }

    [HttpGet("notifications/unread-count")]
    [Authorize(AuthenticationSchemes = NotifAuthSchemes)]
    public async Task<ActionResult<int>> NotificationsUnreadCount()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        var n = await _db.Notifications
            .CountAsync(x => x.UserId == user.Id && x.ReadAt == null);
        return Ok(n);
    }

    [HttpPost("notifications/{id:int}/read")]
    [Authorize(AuthenticationSchemes = NotifAuthSchemes)]
    public async Task<ActionResult> NotificationMarkRead(int id)
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();

        var n = await _db.Notifications
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        if (n == null) return NotFound();
        if (n.ReadAt == null)
        {
            n.ReadAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    [HttpPost("notifications/read-all")]
    [Authorize(AuthenticationSchemes = NotifAuthSchemes)]
    public async Task<ActionResult> NotificationsMarkAllRead()
    {
        var user = await CurrentUser();
        if (user == null) return Unauthorized();
        var now = DateTime.UtcNow;
        await _db.Notifications
            .Where(n => n.UserId == user.Id && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now));
        return Ok();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //                                HELPERS
    // ══════════════════════════════════════════════════════════════════════════
    private async Task<ApplicationUser?> CurrentUser()
    {
        var sub = User?.FindFirstValue(JwtRegisteredClaimNames.Sub)
               ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(sub)) return null;
        return await _users.FindByIdAsync(sub);
    }

    private (string token, DateTime exp) IssueJwt(ApplicationUser user, IList<string> roles)
    {
        var keyStr = _cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key missing.");
        var key    = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keyStr));
        var creds  = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.NameIdentifier,   user.Id),
            new(ClaimTypes.Name,             user.Email ?? user.UserName ?? user.Id),
            new("fullName",                  user.FullName),
            new("employeeNumber",            user.EmployeeNumber)
        };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));

        // 30-day token. Mobile app refreshes by re-logging in or, later, via a
        // refresh-token flow if we add one.
        var expires = DateTime.UtcNow.AddDays(30);
        var token   = new JwtSecurityToken(
            claims: claims,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
