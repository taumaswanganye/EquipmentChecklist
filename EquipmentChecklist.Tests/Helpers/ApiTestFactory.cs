using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentChecklist.Tests.Helpers;

/// <summary>
/// In-process test host for the EquipmentChecklist API.
///
/// <para>What it does:</para>
/// <list type="bullet">
///   <item><description>Boots the real Program pipeline (auth, JWT, MVC, identity).</description></item>
///   <item><description>Swaps Postgres for EF Core InMemory so the tests don't need a database.</description></item>
///   <item><description>Provides a known JWT signing key so login tokens validate inside the same process.</description></item>
///   <item><description>Seeds the four standard roles + one user per role on first start.</description></item>
/// </list>
///
/// <para>Used as <c>IClassFixture&lt;ApiTestFactory&gt;</c>. xUnit will share
/// the factory across every test in the class — call <c>CreateClient()</c>
/// in each test for an isolated HttpClient.</para>
/// </summary>
public class ApiTestFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Same key every test sees so signing + verification line up.</summary>
    public const string JwtKey =
        "test-only-jwt-signing-key-please-do-not-use-in-production-9876543210";

    /// <summary>Plain-text password used for every seeded test user. Strong
    /// enough to satisfy Identity's default password policy.</summary>
    public const string Password = "P@ssword-Test-1!";

    // Known seeded users — exposed as constants so test bodies can refer to
    // them without string literals.
    public const string AdminEmail        = "admin@test.local";
    public const string SupervisorEmail   = "sup@test.local";
    public const string Sup2Email         = "sup2@test.local"; // second team's supervisor
    public const string MechanicEmail     = "mech@test.local";
    public const string OperatorEmail     = "op@test.local";
    public const string Operator2Email    = "op2@test.local";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // ── Override configuration BEFORE service registration ──
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"]                       = JwtKey,
                ["ConnectionStrings:PostgreSQL"]  = "Host=ignored;",
                ["ConnectionStrings:SQLite"]      = "Data Source=:memory:",
                // Silence the email service — tests don't need to send.
                ["Email:Username"]                = "",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Strip the production Postgres registration and replace with an
            // InMemory DB that's unique to this factory instance. Without
            // this, EF Core would try to talk to a real Postgres on startup.
            RemoveDbContext<ApplicationDbContext>(services);
            services.AddDbContext<ApplicationDbContext>(opt =>
                opt.UseInMemoryDatabase($"api-tests-{Guid.NewGuid():N}"));

            RemoveDbContext<LocalDbContext>(services);
            services.AddDbContext<LocalDbContext>(opt =>
                opt.UseInMemoryDatabase($"local-tests-{Guid.NewGuid():N}"));
        });
    }

    /// <summary>
    /// Strip both the typed options descriptor AND the matching DbContext
    /// descriptor so a follow-up AddDbContext call rebuilds the whole
    /// triplet cleanly (options, options factory, context itself).
    /// </summary>
    private static void RemoveDbContext<TContext>(IServiceCollection services)
        where TContext : DbContext
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(DbContextOptions<TContext>)
                     || d.ServiceType == typeof(TContext))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);
    }

    public async Task InitializeAsync()
    {
        using var scope = Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();

        // ── Roles ──
        foreach (var role in new[] { "Admin", "Supervisor", "Mechanic", "Operator" })
        {
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole(role));
        }

        // ── Users — one of each role, plus a second supervisor + operator
        // so we can exercise the team-scope / defence-in-depth checks. ──
        await SeedUser(users, AdminEmail,      "Admin Test",      "EMP-A1", "Admin");
        await SeedUser(users, SupervisorEmail, "Supervisor Test", "EMP-S1", "Supervisor");
        await SeedUser(users, Sup2Email,       "Supervisor Two",  "EMP-S2", "Supervisor");
        await SeedUser(users, MechanicEmail,   "Mechanic Test",   "EMP-M1", "Mechanic");
        await SeedUser(users, OperatorEmail,   "Operator Test",   "EMP-O1", "Operator");
        await SeedUser(users, Operator2Email,  "Operator Two",    "EMP-O2", "Operator");

        // ── Build the team graph: SupervisorEmail manages OperatorEmail.
        // Sup2Email manages Operator2Email. This lets us prove that a sup
        // can only see their own team's submissions. ──
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sup   = await users.FindByEmailAsync(SupervisorEmail);
        var sup2  = await users.FindByEmailAsync(Sup2Email);
        var op1   = await users.FindByEmailAsync(OperatorEmail);
        var op2   = await users.FindByEmailAsync(Operator2Email);

        db.OperatorSupervisorAssignments.Add(new OperatorSupervisorAssignment
        {
            SupervisorId = sup!.Id,
            OperatorId   = op1!.Id,
            IsActive     = true
        });
        db.OperatorSupervisorAssignments.Add(new OperatorSupervisorAssignment
        {
            SupervisorId = sup2!.Id,
            OperatorId   = op2!.Id,
            IsActive     = true
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task SeedUser(
        UserManager<ApplicationUser> users,
        string email, string fullName, string empNo, string role)
    {
        var existing = await users.FindByEmailAsync(email);
        if (existing != null) return;

        var u = new ApplicationUser
        {
            UserName       = email,
            Email          = email,
            FullName       = fullName,
            EmployeeNumber = empNo,
            IsActive       = true,
            EmailConfirmed = true
        };
        var created = await users.CreateAsync(u, Password);
        if (!created.Succeeded)
            throw new Exception("Failed to create test user " + email + ": "
                + string.Join("; ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(u, role);
    }
}
