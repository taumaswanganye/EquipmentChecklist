using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ── Databases ────────────────────────────────────────────────────────────────
// PostgreSQL – primary cloud database
builder.Services.AddDbContext<ApplicationDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("PostgreSQL")));

// SQLite – local offline database
builder.Services.AddDbContext<LocalDbContext>(opt =>
    opt.UseSqlite(builder.Configuration.GetConnectionString("SQLite")
        ?? "Data Source=belfast_offline.db"));

builder.Services.AddSession(options =>
{
	options.IdleTimeout = TimeSpan.FromHours(8);
	options.Cookie.HttpOnly = true;
	options.Cookie.IsEssential = true;
});

builder.Services.AddScoped<ChecklistService>();
builder.Services.AddScoped<PdfService>();
builder.Services.AddScoped<EmailService>();
builder.Services.AddScoped<NotificationService>();
// KPI dashboard payload for /Admin/Reports. Scoped because it depends on
// the per-request ApplicationDbContext.
builder.Services.AddScoped<ReportsService>();

// Append-only audit log. Needs IHttpContextAccessor to resolve the current
// actor from the ambient ClaimsPrincipal — registered here as well in case
// nothing else in the pipeline did it.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AuditService>();

// Site-specific labels (mine name, tagline, compliance text) — bound from
// the "Mine" section of appsettings.json. Defaults in MineSettings cover
// freshly cloned repos.
builder.Services.Configure<MineSettings>(builder.Configuration.GetSection("Mine"));

// SignalR — backs the /hubs/notifications real-time stream.
builder.Services.AddSignalR();

// ── Biometric / passwordless (WebAuthn + offline voucher) ───────────────────
// OfflineVoucherService holds the long-lived RSA signing key and must be
// singleton so we don't regenerate the key on every request.
builder.Services.AddSingleton<EquipmentChecklist.Services.OfflineVoucherService>();

builder.Services.AddFido2(options =>
{
    options.ServerDomain      = builder.Configuration["Fido2:ServerDomain"]      ?? "localhost";
    options.ServerName        = builder.Configuration["Fido2:ServerName"]        ?? "Equipment Checklist";
    options.Origins           = new HashSet<string>(
        (builder.Configuration.GetSection("Fido2:Origins").Get<string[]>() ?? new[] { "https://localhost:5001" }));
    options.TimestampDriftTolerance = 300_000;
});
// ── Identity ─────────────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(opt =>
{
    opt.Password.RequireDigit = true;
    opt.Password.RequiredLength = 8;
    opt.Password.RequireUppercase = false;
    opt.Lockout.MaxFailedAccessAttempts = 5;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

// ── JWT (for the mobile/API clients) ─────────────────────────────────────────
var jwtKey = builder.Configuration["Jwt:Key"]
    ?? throw new Exception("JWT key not configured – add Jwt:Key to appsettings");

builder.Services.AddAuthentication(opt =>
{
    opt.DefaultScheme = "multi";
    opt.DefaultChallengeScheme = "multi";
})
.AddPolicyScheme("multi", "multi", opt =>
{
    // Prefer cookie auth for MVC, JWT for API routes
    opt.ForwardDefaultSelector = ctx =>
        ctx.Request.Path.StartsWithSegments("/api")
            ? JwtBearerDefaults.AuthenticationScheme
            : IdentityConstants.ApplicationScheme;
})
.AddJwtBearer(opt =>
{
    opt.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateIssuer = false,
        ValidateAudience = false,
        ClockSkew = TimeSpan.Zero
    };

    // SignalR can't set an Authorization header during the WebSocket
    // upgrade — it falls back to a query-string token. Accept either
    // form so /hubs/notifications works for mobile + browser clients
    // while /api/sync/* keeps using the standard Authorization header.
    opt.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var token = context.Request.Query["access_token"];
            var path  = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(token) &&
                path.StartsWithSegments("/hubs"))
            {
                context.Token = token;
            }
            return Task.CompletedTask;
        },

        // ── Per-request deactivation check ──────────────────────────────
        // After the JWT signature is validated, look up the user and
        // refuse the request if they've been deactivated since the token
        // was issued. This is what makes "admin deactivates user → user's
        // next API call fails within seconds" work without waiting for
        // the JWT to expire on its own.
        //
        // The cost is one DB read per authenticated API call. For our
        // scale (a couple hundred operators on a mine) that's negligible
        // and the security win is large. If this ever becomes a hot path,
        // cache the IsActive flag in IMemoryCache with a 30s TTL.
        OnTokenValidated = async context =>
        {
            var userManager = context.HttpContext.RequestServices
                .GetRequiredService<UserManager<ApplicationUser>>();
            var userId = context.Principal?.FindFirst(
                    System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userId))
            {
                context.Fail("No user id in token");
                return;
            }
            var user = await userManager.FindByIdAsync(userId);
            if (user == null || !user.IsActive)
            {
                // Distinct response body so the mobile client can branch
                // between "session expired" and "you've been blocked".
                context.Response.Headers["X-Auth-Failure"] = "user_deactivated";
                context.Fail("User is deactivated");
                return;
            }

            // ── Device allowlist check ────────────────────────────────
            // Re-validate the device fingerprint on every authenticated
            // call so an admin's "deactivate device" takes effect within
            // seconds, not when the JWT expires. The header is the same
            // one we required at login.
            var fp = context.HttpContext.Request.Headers["X-Device-Fingerprint"].ToString();
            if (string.IsNullOrWhiteSpace(fp))
            {
                context.Response.Headers["X-Auth-Failure"] = "device_missing_fingerprint";
                context.Fail("Device fingerprint missing");
                return;
            }

            var db = context.HttpContext.RequestServices
                .GetRequiredService<EquipmentChecklist.Data.ApplicationDbContext>();
            var device = await db.AllowedDevices
                .Where(d => d.DeviceFingerprint == fp)
                .Select(d => new { d.IsActive, d.AssignedUserId })
                .FirstOrDefaultAsync();
            if (device == null)
            {
                context.Response.Headers["X-Auth-Failure"] = "device_not_registered";
                context.Fail("Device not registered");
                return;
            }
            if (!device.IsActive)
            {
                context.Response.Headers["X-Auth-Failure"] = "device_revoked";
                context.Fail("Device revoked");
                return;
            }
            if (!string.IsNullOrEmpty(device.AssignedUserId) &&
                !string.Equals(device.AssignedUserId, userId, StringComparison.Ordinal))
            {
                context.Response.Headers["X-Auth-Failure"] = "device_wrong_user";
                context.Fail("Device assigned to a different user");
                return;
            }
        }
    };
});

// ── Authorisation roles ───────────────────────────────────────────────────────
builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy("AdminOnly",   p => p.RequireRole("Admin"));
    opt.AddPolicy("Supervisor",  p => p.RequireRole("Admin", "Supervisor"));
    opt.AddPolicy("Mechanic",    p => p.RequireRole("Admin", "Mechanic"));
    opt.AddPolicy("Operator",    p => p.RequireRole("Admin", "Operator", "Supervisor", "Mechanic"));
});

// ── App services ──────────────────────────────────────────────────────────────
builder.Services.AddScoped<ChecklistService>();
builder.Services.AddHostedService<SyncService>();

builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// ── Middleware ────────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// HTTPS redirect is skipped in Development so a phone on the LAN can
// hit the API over plain HTTP for smoke-testing (the dev self-signed
// cert isn't trusted by Android). The MAUI app already overrides cert
// validation in DEBUG builds, so DEBUG mobile clients can use HTTPS
// just fine — this only opens the door for browser-based "is the API
// reachable?" tests from a phone over the LAN.
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRouting();
app.UseSession();          // ← must be AFTER UseRouting, BEFORE UseAuthorization
app.UseAuthentication();
app.UseAuthorization();

// ── Seed roles and admin user ──────────────────────────────────────────────────
await SeedRolesAndAdminAsync(app);

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Real-time notifications endpoint — JWT-auth, queue-and-broadcast pattern
// described in NotificationService. Mobile connects with the access_token
// query-string parameter.
app.MapHub<NotificationHub>("/hubs/notifications");

app.Run();

// ─── Seeder ────────────────────────────────────────────────────────────────────
static async Task SeedRolesAndAdminAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var cloudDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var localDb = scope.ServiceProvider.GetRequiredService<LocalDbContext>();

    // 1. Run migrations for Cloud DB.
    //
    // Was previously commented out — which is why missing-column errors like
    // "42703: column s.AudioData does not exist" survived re-deploys: a new
    // migration file would land in the assembly but the schema was never
    // pushed unless someone remembered to run `dotnet ef database update`
    // by hand. Uncommenting means every app boot now drains the pending
    // migrations list, so the schema can't drift behind the model.
    //
    // For production this is fine because all of our migrations are
    // idempotent and additive. If you ever add a destructive migration
    // (DROP COLUMN, DROP TABLE) gate it behind a feature flag or run it
    // manually from a deploy step.
    await cloudDb.Database.MigrateAsync();

    // ── Out-of-band schema bootstrap ─────────────────────────────────────
    // The MDM-lite AllowedDevices feature ships without a matching EF
    // Designer.cs + ModelSnapshot pair, so EF's migration discovery
    // skips its migration on a fresh DB even though the file exists. We
    // run the same idempotent CREATE TABLE here directly so the table
    // always exists by the time SyncController or the JWT validator
    // queries it. Each statement is IF NOT EXISTS so it's safe to re-run
    // on every boot, and broken into separate ExecuteSqlRawAsync calls
    // so we don't depend on Npgsql multi-statement handling.
    await cloudDb.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "AllowedDevices" (
            "Id"                 serial PRIMARY KEY,
            "DeviceFingerprint"  character varying(128) NOT NULL,
            "Label"              character varying(100) NULL,
            "Manufacturer"       character varying(80)  NULL,
            "Model"              character varying(80)  NULL,
            "Platform"           character varying(40)  NULL,
            "OsVersion"          character varying(40)  NULL,
            "AssignedUserId"     character varying(450) NULL,
            "IsActive"           boolean                NOT NULL DEFAULT TRUE,
            "ApprovedByAdminId"  character varying(450) NULL,
            "CreatedAt"          timestamp with time zone NOT NULL,
            "ApprovedAt"         timestamp with time zone NULL,
            "LastSeenAt"         timestamp with time zone NULL,
            "DeactivatedAt"      timestamp with time zone NULL,
            "Notes"              character varying(500) NULL,
            CONSTRAINT "FK_AllowedDevices_AspNetUsers_AssignedUserId"
                FOREIGN KEY ("AssignedUserId")
                REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL,
            CONSTRAINT "FK_AllowedDevices_AspNetUsers_ApprovedByAdminId"
                FOREIGN KEY ("ApprovedByAdminId")
                REFERENCES "AspNetUsers" ("Id") ON DELETE SET NULL
        );
        """);
    await cloudDb.Database.ExecuteSqlRawAsync(
        "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AllowedDevices_DeviceFingerprint\" " +
        "ON \"AllowedDevices\" (\"DeviceFingerprint\");");
    await cloudDb.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_AllowedDevices_AssignedUserId\" " +
        "ON \"AllowedDevices\" (\"AssignedUserId\");");
    await cloudDb.Database.ExecuteSqlRawAsync(
        "CREATE INDEX IF NOT EXISTS \"IX_AllowedDevices_ApprovedByAdminId\" " +
        "ON \"AllowedDevices\" (\"ApprovedByAdminId\");");

    // 2. Ensure Local SQLite DB is created
    await localDb.Database.EnsureCreatedAsync();

    // 3. Seed default admin user
    const string adminEmail = "admin@belfast.co.za";
    if (await userManager.FindByEmailAsync(adminEmail) == null)
    {
        var admin = new ApplicationUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            FullName = "System Administrator",
            EmployeeNumber = "ADMIN001",
            Role = UserRole.Admin,
            EmailConfirmed = true
        };
        await userManager.CreateAsync(admin, "Admin@123");
        await userManager.AddToRoleAsync(admin, "Admin");
    }

    // 4. Seed checklist templates
    if (!await cloudDb.ChecklistTemplates.AnyAsync())
    {
        await SeedChecklistTemplatesAsync(cloudDb);
    }

    // 5. Sync reference data to Local DB
    await SyncMasterDataToLocalAsync(cloudDb, localDb);
}

static async Task SyncMasterDataToLocalAsync(ApplicationDbContext cloudDb, LocalDbContext localDb)
{
    // Clear local cache
    if (!await localDb.Machines.AnyAsync())
    {
        // Copy Machines
        var machines = await cloudDb.Machines.AsNoTracking().ToListAsync();
        localDb.Machines.AddRange(machines);

        // Copy Templates
        var templates = await cloudDb.ChecklistTemplates.AsNoTracking().ToListAsync();
        localDb.ChecklistTemplates.AddRange(templates);

        // Copy Template Items
        var templateItems = await cloudDb.ChecklistTemplateItems.AsNoTracking().ToListAsync();
        localDb.ChecklistTemplateItems.AddRange(templateItems);

        await localDb.SaveChangesAsync();
    }
}

static async Task SeedChecklistTemplatesAsync(ApplicationDbContext db)
{
	var noGoKeywords = new[]
	{
		"OPERATOR LICENCE", "SEAT BELT", "SEAT BELTS", "BRAKES", "BRAKE TEST",
		"FIRE EXTINGUISHER", "KEY CONTROL", "EMERGENCY STOP"
	};

	var templates = new List<(MachineType type, string machineNumber, string machineName, string[] items)>
	{
		(MachineType.ADT, "ADT-001", "Articulated Dump Truck", new[]{
			"OPERATOR LICENCE","STOP BLOCKS","FIRE EXTINGUISHER","SEAT BELTS (IN USE)",
			"HEAD LIGHTS","ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS",
			"BRAKE LIGHTS","DOORS/HANDLES","HAND RAILS","WHEEL NUTS","PINS IN POSITION AND LOCKED",
			"LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","AUDIBLE BIN RAISE ALARM",
			"BRAKE TEST (SERVICE BRAKE)","BRAKE TEST (PARK BRAKE)",
			"BRAKE TEST RAMP WHEN ENTERING PIT","RADIO TWO-WAY","ISOLATION POINT",
			"KEY CONTROL","AIR CONDITIONER","OIL LEVEL","DASHBOARD INDICATORS/INSTRUMENTS",
			"VISIBLE LEAKS","WINDOWS","TIRE CONDITION","STEPS","SEATS","WINDSCREEN WIPER",
			"MIRRORS","COOLANT LEVEL","RADIATOR NOT CLOGGED","HYDRAULIC OIL LEVEL",
			"REFLECTIVE TAPE CONDITION","FUEL LEVEL","NEW BUMP MARKS"
		}),
		(MachineType.TLB, "TLB-001", "TLB", new[]{
			"OPERATOR LICENCE","STOP BLOCKS","FRONT LIGHTS","REAR LIGHTS","INDICATOR LIGHTS",
			"ROTATING LIGHT","ALL MIRRORS","DASH BOARD WARNING LIGHTS","HOOTER","REVERSE HOOTER",
			"STEERING/STEER CONTROL","BRAKES","KEY CONTROL / PROXY","FIRE EXTINGUISHER",
			"GAUGES","TYRES AND WHEEL NUTS","SEAT BELT","CONTROLS",
			"PARK BRAKE IN WORKING CONDITION","ISOLATION POINT",
			"AIR CONDITIONER","RADIO TWO-WAY (If intended for PIT)","DIGGING BOOM ASSEMBLY",
			"BUCKET AND CUTTING EDGES","OIL / WATER LEAKS","WINDOWS","OUT RIGGERS",
			"NEW BUMP MARKS","TOW BAR & PIN","REFLECTIVE TAPE CONDITION","ALL PIPES",
			"SCREEN WIPER","FUEL LEVEL","DOORS & HANDLES","SEATS","STEPS","Recent body damages"
		}),
		(MachineType.Excavator, "EXC-001", "Excavator", new[]{
			"OPERATOR LICENCE","FIRE EXTINGUISHER","SEAT BELTS","HEAD LIGHTS",
			"ROTATING / FLASH LIGHT","AREA LIGHTS","DASH BOARD WARNING LIGHTS","HOOTER",
			"TRAVEL ALARM","SWING BRAKES","CONTROLS","ALL PIPES","ALL MIRRORS",
			"RADIO TWO-WAY","ISOLATION POINT","KEY CONTROL","EMERGENCY STOP CONDITION",
			"AIR CONDITIONER","DOORS & HANDLES","STEPS","HAND GRIPS","WINDOWS","SEATS",
			"OIL LEAKS","TRACKS","REFLECTIVE TAPE CONDITION","BUCKET","ROLLERS","NEW BUMP MARKS"
		}),
		(MachineType.FEL, "FEL-001", "Front End Loader", new[]{
			"OPERATOR LICENCE","STOP BLOCKS","FIRE EXTINGUISHER","SEAT BELT","HEAD LIGHTS",
			"ACCESS LIGHTS","REAR LIGHTS","INDICATOR LIGHTS","HAZARD LIGHTS","ALL MIRRORS",
			"DASH BOARD WARNING LIGHTS","HOOTER","REVERSE HOOTER","STEERING/STEER CONTROL",
			"BRAKES","KEY CONTROL / PROXY","GAUGES","TYRES","CONTROLS",
			"BRAKE TEST RAMP WHEN ENTERING PIT","ROTATING LIGHT",
			"RADIO TWO-WAY","ISOLATION POINT","AIR CONDITIONER",
			"DOORS & HANDLES","HAND GRIPS","HAND RAILS","SCREEN WIPER","FUEL LEVEL",
			"ALL PIPES","SEATS","OIL / WATER LEAKS","WINDOWS","STEPS",
			"REFLECTIVE TAPE CONDITION","BUCKET","TOW BAR & PIN","NEW BUMP MARKS"
		}),
		(MachineType.Grader, "GRD-001", "Grader", new[]{
			"OPERATORS LICENCE","SEAT BELT","FIRE EXTINGUISHER","HEAD LIGHTS","ROTATING LIGHT",
			"INDICATOR LIGHTS","REVERSE LIGHTS","HAZARD LIGHTS","REAR LIGHTS","MIRRORS",
			"DASH BOARD WARNING LIGHTS","HOOTER","REVERSE HOOTER","GAUGES",
			"STEERING/STEER CONTROL","BRAKES","CONTROLS","ISOLATION POINT","KEY CONTROL",
			"RADIO TWO-WAY","AIR CONDITIONER","DOORS & HANDLES","TYRES","HAND GRIPS",
			"SCREEN WIPER","FUEL LEVEL","ALL PIPES","OIL / WATER LEAKS","WINDOWS","STEPS",
			"SEATS","REFLECTIVE TAPE CONDITION","BLADE & RIPPER","HOUR METER","NEW BUMP MARKS"
		}),
		(MachineType.RDT, "RDT-001", "773 Haul Truck / RDT", new[]{
			"OPERATOR LICENCE","STOP BLOCKS","FIRE EXTINGUISHER","ALL SEAT BELTS",
			"HEAD LIGHTS","REVERSE LIGHTS","INDICATOR LIGHTS","REAR LIGHTS",
			"KEY CONTROL / PROXY","EMERGENCY STOP CONDITION","ALL MIRRORS",
			"DASH BOARD WARNING LIGHTS","DUMP BODY UP BUZZER","HOOTER",
			"REVERSE HOOTER","DIRECTIONAL HOOTER","STEERING","BRAKES","GAUGES","TYRES",
			"CONTROLS","RADIO TWO-WAY","AIR CONDITIONER","DOORS & HANDLES",
			"HAND RAILS & KICK PLATES","ACCESS LIGHT","SCREEN WIPER","OIL / WATER LEAKS",
			"ALL PIPES","WINDOWS","SEATS","REFLECTIVE TAPE CONDITION","STEPS","HOUR METER",
			"NEW BUMP MARKS"
		}),
		(MachineType.Forklift, "FLT-001", "Forklift", new[]{
			"OPERATOR LICENCE","STOPBLOCKS","WHEEL NUTS","FORK CONDITION","LIFTING CHAIN CONDITION",
			"PINS IN POSITION AND LOCKED","SEAT BELT","EMERGENCY TRIANGLE",
			"LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS FRONT",
			"ROTATING LIGHT","INDICATOR LIGHTS","REAR LIGHTS","BRAKE LIGHTS",
			"BRAKE TEST (PARK BRAKE)","BRAKE TEST (SERVICE BRAKE)","FIRE EXTINGUISHER",
			"TYRE CONDITION","DASHBOARD INDICATORS","MIRRORS","SEATS","VISIBLE LEAKS",
			"HYDRAULIC OIL","OIL LEVEL","COOLANT LEVEL","RADIATOR NOT CLOGGED","NEW BUMP MARKS"
		}),
		(MachineType.TrackDozer, "TKD-001", "Track Dozer", new[]{
			"OPERATOR LICENCE","SEAT BELT","FIRE EXTINGUISHER","HEAD LIGHTS","ROTATING LIGHT",
			"ACCESS LIGHTS","REAR LIGHTS","MIRROR","DASH BOARD WARNING LIGHTS","HOOTER",
			"REVERSE HOOTER","STEER CONTROL","BRAKES","CONTROLS","GAUGES","ISOLATION POINT",
			"KEY CONTROL","AIR CONDITIONER","DOORS & HANDLES","GUARDS","RADIO TWO-WAY",
			"SCREEN WIPER","FUEL LEVEL","ALL PIPES","SEATS","WINDOWS","STEPS",
			"OIL / WATER LEAKS","TRACKS","HAND GRIPS","REFLECTIVE TAPE CONDITION",
			"BLADE/RIPPER","NEW BUMP MARKS"
		}),
		(MachineType.LDV, "LDV-001", "LDV / Light Vehicle", new[]{
			"OPERATOR LICENCE","WHEEL NUTS","FIRE EXTINGUISHER","EMERGENCY TRIANGLE",
			"SEAT BELTS (IN USE)","HOOTER","REVERSE HOOTER","HEAD LIGHTS","INDICATORS",
			"BRAKE LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE TEST (FOOT BRAKE)",
			"BRAKE TEST (EMERGENCY BRAKE)","VISIBLE LEAKS","TYRE CONDITION",
			"DASHBOARD INDICATORS/INSTRUMENTS","WINDOWS","MIRRORS","SEATS CONDITION",
			"SCREEN WIPER","FLAG","ROTATING LIGHT","TWO WAY RADIO",
			"REFLECTIVE TAPE CONDITION","OIL LEVEL","COOLANT LEVEL",
			"BRAKE FLUID LEVEL","SCREEN WIPER WATER LEVEL","RADIATOR NOT CLOGGED"
		}),
		(MachineType.ArticulatedWaterTruck, "AWT-001", "Articulated Water Truck", new[]{
			"OPERATOR LICENCE","PINS IN POSITION AND LOCKED","FIRE EXTINGUISHER",
			"HAND RAILS","DOORS/HANDLES","STOP BLOCKS","WHEEL NUTS","SEAT BELTS (IN USE)",
			"LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS",
			"ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE LIGHTS",
			"BRAKE TEST (SERVICE BRAKE)","BRAKE TEST (PARK BRAKE)","AIR CONDITIONER",
			"OIL LEVEL","DASHBOARD INSTRUMENTS","VISIBLE LEAKS","WINDOWS","TIRE CONDITION",
			"STEPS","SEATS","SCREEN WIPER","MIRRORS","COOLANT LEVEL",
			"RADIATOR NOT CLOGGED","HYDRAULIC OIL LEVEL","FUEL LEVEL","NEW BUMP MARKS",
			"REFLECTIVE TAPE CONDITION","FLAG","RADIO TWO-WAY"
		}),
		(MachineType.DieselBowser, "DSB-001", "Diesel Bowser", new[]{
			"OPERATOR LICENCE","WHEEL NUTS","PINS IN POSITION AND LOCKED","FIRE EXTINGUISHER",
			"STOP BLOCKS","EMERGENCY TRIANGLE","SEAT BELTS (IN USE)",
			"LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS",
			"ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE LIGHTS",
			"BRAKE TEST (SERVICE BRAKE)","PPE","ISOLATION POINT","PUMPS","GUARDS",
			"HOSE CONNECTORS","HOSE FITTINGS","PTO","NOZZLE","BRAKE TEST (PARK BRAKE)",
			"DOORS/HANDLES","AIR CONDITIONER","TWO WAY RADIO","VISIBLE LEAKS","MIRRORS",
			"WINDOWS","TIRE CONDITION","SEATS","STEPS","SCREEN WIPER","OIL LEVEL",
			"DASHBOARD INDICATORS/INSTRUMENTS","COOLANT LEVEL","RADIATOR NOT CLOGGED",
			"NEW BUMP MARKS","RADIO TWO-WAY","FLAG","REFLECTIVE TAPE","DIESEL READINGS"
		}),
		(MachineType.SRVWaterBowser, "SRV-001", "SRV / Water Bowser", new[]{
			"OPERATOR LICENCE","WHEEL NUTS","PINS IN POSITION AND LOCKED","FIRE EXTINGUISHER",
			"STOP BLOCKS","EMERGENCY TRIANGLE","SEAT BELTS (IN USE)",
			"LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS",
			"ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE LIGHTS",
			"BRAKE TEST (SERVICE BRAKE)","PUMPS","GUARDS","HOSE CONNECTORS","HOSES AND FITTINGS",
			"BRAKE TEST (PARK BRAKE)","DOORS/HANDLES","AIR CONDITIONER","OIL LEVEL",
			"VISIBLE LEAKS","MIRRORS","WINDOWS","TIRE CONDITION","SEATS","STEPS","SCREEN WIPER",
			"DASHBOARD INDICATORS/INSTRUMENTS","COOLANT LEVEL","RADIATOR NOT CLOGGED",
			"NEW BUMP MARKS","RADIO TWO-WAY","FLAG","REFLECTIVE TAPE"
		}),
		(MachineType.TruckMountedCrane, "TMC-001", "Truck Mounted Crane", new[]{
			"OPERATOR LICENCE","WHEEL NUTS","PINS IN POSITION AND LOCKED","FIRE EXTINGUISHER",
			"STOP BLOCKS","EMERGENCY TRIANGLE","SEAT BELTS (IN USE)",
			"LEVERS/JOYSTICKS/STEER CONTROL","HOOTER","REVERSE HOOTER","HEAD LIGHTS",
			"ROTATING LIGHT","INDICATOR LIGHTS","TAIL LIGHTS","REVERSE LIGHTS","BRAKE LIGHTS",
			"BRAKE TEST (SERVICE BRAKE)","HOOK SAFETY LATCH","OUTRIGGERS","LEVERS/JOYSTICKS",
			"BRAKE TEST (PARK BRAKE)","DOORS/HANDLES","TIRE CONDITION",
			"DASHBOARD INDICATORS/INSTRUMENTS","VISIBLE LEAKS","MIRRORS","WINDOWS",
			"NEW BUMP MARKS","SEATS","STEPS","SCREEN WIPER","AIR CONDITIONER","LOAD CHART",
			"OIL LEVEL","COOLANT LEVEL","RADIATOR NOT CLOGGED","HYDRAULIC OIL",
			"RADIO TWO-WAY","FLAG","REFLECTIVE TAPE"
		}),
		(MachineType.Drills, "DRL-001", "Drills", new[]{
			"OPERATOR LICENCE","FIRE EXTINGUISHER","FRONT & REAR LIGHTS","ROTATING LIGHT",
			"TRAM HOOTER","RADIO TWO-WAY","DASH BOARD WARNING LIGHTS","HOOTER",
			"DRIFTER SLIDES","DRIFTER CABLE","SEAT BELT","KEY CONTROL","CONTROLS",
			"EMERGENCY STOP CONDITION","BOOM CONDITION","ISOLATION POINT","HOSE CONNECTORS",
			"HOSES","ISOLATOR VALVES","GUARDS","AIR CONDITIONER","DOOR & HANDLES",
			"DUST COLLECTION","MIRRORS","OIL / FUEL / WATER LEAKS","SEAT","STEPS",
			"REFLECTIVE TAPE CONDITION","TRACKS & SPROCKETS","WINDOWS & WIPERS",
			"CYLINDERS","TOW BAR & PIN","CAROUSAL","NEW BUMP MARKS","FUEL LEVEL"
		})
	};

	foreach (var (type, machineNumber, machineName, items) in templates)
	{
		// 1. Save machine first — get the real Id from DB
		var machine = new Machine
		{
			MachineNumber = machineNumber,
			MachineName = machineName,
			Type = type,
			IsActive = true
		};
		db.Machines.Add(machine);
		await db.SaveChangesAsync(); // machine.Id is now populated

		// 2. Save template using real machine.Id
		var template = new ChecklistTemplate
		{
			MachineType = type,
			Name = $"{machineName} Checklist",
			MachineId = machine.Id
		};
		db.ChecklistTemplates.Add(template);
		await db.SaveChangesAsync(); // template.Id is now populated

		// 3. Save checklist items using real template.Id
		var templateItems = items.Select((itemName, index) => new ChecklistTemplateItem
		{
			TemplateId = template.Id,
			ItemName = itemName,
			Section = "General",
			SortOrder = index + 1,
			IsNoGoItem = noGoKeywords.Any(k =>
				itemName.Contains(k, StringComparison.OrdinalIgnoreCase))
		}).ToList();

		db.ChecklistTemplateItems.AddRange(templateItems);
		await db.SaveChangesAsync();
	}
}

// ───────────────────────────────────────────────────────────────────────────────
// Make Program reachable from EquipmentChecklist.Tests so WebApplicationFactory
// can spin up the full pipeline in-process. Top-level Program.cs is implicitly
// `internal partial class Program` — declaring it `public partial class Program`
// here flips the visibility without touching the script body above.
// ───────────────────────────────────────────────────────────────────────────────
public partial class Program { }