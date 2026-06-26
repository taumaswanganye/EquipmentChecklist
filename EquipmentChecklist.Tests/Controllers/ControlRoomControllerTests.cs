using System.Security.Claims;
using EquipmentChecklist.Controllers;
using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// Direct-instantiation tests for <see cref="ControlRoomController"/>.
///
/// Covers the Phase 3 + 4 dispatch surface:
///   * Dispatch requires the defect to be Planner-captured first.
///   * Double-dispatch is refused.
///   * Reassign requires a different artisan + the defect to be open.
///   * Escalate stamps notes + leaves the defect in the queue.
///
/// NotificationService is faked via a mock <see cref="IHubContext"/> —
/// we don't assert on SignalR pushes here, just that the dispatch
/// completes despite the notify call.
/// </summary>
public class ControlRoomControllerTests
{
    // ── Fixture plumbing ─────────────────────────────────────────────────────

    private static (UserManager<ApplicationUser> users, AuditService audit, NotificationService notif)
        BuildDependencies(ApplicationDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ApplicationDbContext>(db);
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>();

        // SignalR hub context — a fake that swallows every call. The
        // controller calls _notifications.PushAsync inside a try/catch
        // anyway, so a no-op hub is fine for these tests.
        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock
            .Setup(c => c.SendCoreAsync(
                It.IsAny<string>(),
                It.IsAny<object?[]>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var clientsMock = new Mock<IHubClients>();
        clientsMock.Setup(c => c.User(It.IsAny<string>())).Returns(clientProxyMock.Object);
        clientsMock.Setup(c => c.All).Returns(clientProxyMock.Object);

        var hubMock = new Mock<IHubContext<NotificationHub>>();
        hubMock.Setup(h => h.Clients).Returns(clientsMock.Object);

        var sp    = services.BuildServiceProvider();
        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var audit = new AuditService(
            db,
            sp.GetRequiredService<IHttpContextAccessor>(),
            users,
            NullLogger<AuditService>.Instance);
        var notif = new NotificationService(db, hubMock.Object, NullLogger<NotificationService>.Instance);

        return (users, audit, notif);
    }

    private static ControlRoomController NewController(ApplicationDbContext db, string controlRoomUserId)
    {
        var (users, audit, notif) = BuildDependencies(db);
        var ctrl = new ControlRoomController(db, users, notif, audit);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, controlRoomUserId),
                    new Claim(ClaimTypes.Name,           "controlroom@test.local")
                }, "TestAuth"))
            }
        };
        return ctrl;
    }

    /// <summary>Seeds: 1 machine, 1 NO-GO submission, 1 Planner-captured
    /// DefectOrder ready for dispatch, 2 active Mechanic-role artisans
    /// (one with no fleet, one with a fleet). Returns the IDs the tests
    /// need to assert against.</summary>
    private static async Task<(int defectId, string controlUserId,
                               string artisanAId, string artisanBId)>
        SeedReadyToDispatchAsync(ApplicationDbContext db, int? machineFleetId = null)
    {
        var op       = new ApplicationUser { UserName = "op",      Email = "op@x",    FullName = "Op",  EmployeeNumber = "E1", IsActive = true };
        var control  = new ApplicationUser { UserName = "control", Email = "c@x",     FullName = "C",   EmployeeNumber = "E2", IsActive = true };
        var artisanA = new ApplicationUser { UserName = "artA",    Email = "a@x",     FullName = "A",   EmployeeNumber = "E3", IsActive = true };
        var artisanB = new ApplicationUser { UserName = "artB",    Email = "b@x",     FullName = "B",   EmployeeNumber = "E4", IsActive = true };
        db.Users.AddRange(op, control, artisanA, artisanB);

        // Mechanic role + assign both artisans to it so
        // UserManager.GetUsersInRoleAsync returns them.
        var role = new IdentityRole("Mechanic") { NormalizedName = "MECHANIC" };
        db.Roles.Add(role);
        await db.SaveChangesAsync();
        db.UserRoles.AddRange(
            new IdentityUserRole<string> { UserId = artisanA.Id, RoleId = role.Id },
            new IdentityUserRole<string> { UserId = artisanB.Id, RoleId = role.Id });
        await db.SaveChangesAsync();

        var machine = new Machine
        {
            MachineNumber = "M-001",
            MachineName   = "Test Loader",
            Type          = MachineType.FEL,
            IsActive      = true,
            FleetId       = machineFleetId
        };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        var sub = new ChecklistSubmission
        {
            MachineId   = machine.Id,
            OperatorId  = op.Id,
            Status      = ChecklistStatus.NoGo,
            SubmittedAt = DateTime.UtcNow.AddHours(-2)
        };
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();

        var defect = new DefectOrder
        {
            SubmissionId       = sub.Id,
            DefectDescription  = "Hydraulics failed",
            CreatedAt          = DateTime.UtcNow.AddHours(-2),
            PlannerCapturedAt  = DateTime.UtcNow.AddHours(-1),
            JobCardNumber      = "WO-9001",
            RepairStatus       = RepairStatus.Pending
        };
        db.DefectOrders.Add(defect);
        await db.SaveChangesAsync();

        return (defect.Id, control.Id, artisanA.Id, artisanB.Id);
    }

    // ── Dispatch ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispatch_StampsArtisanAndDispatchedAt()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, artisanAId, _) = await SeedReadyToDispatchAsync(db);

        var ctrl = NewController(db, controlId);
        var result = await ctrl.Dispatch(defectId, artisanAId);

        result.Should().BeOfType<RedirectToActionResult>();

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.AssignedMechanicId.Should().Be(artisanAId);
        defect.DispatchedAt.Should().NotBeNull();
        defect.DispatchedById.Should().Be(controlId);
        defect.RepairStatus.Should().Be(RepairStatus.InProgress);
    }

    [Fact]
    public async Task Dispatch_EmptyArtisanId_DoesNothing()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, _, _) = await SeedReadyToDispatchAsync(db);

        var ctrl = NewController(db, controlId);
        await ctrl.Dispatch(defectId, "");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.AssignedMechanicId.Should().BeNull();
        defect.DispatchedAt.Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_BeforePlannerCapture_Refuses()
    {
        // A defect that hasn't been Planner-captured yet shouldn't be
        // dispatchable — the Control Room can only see things that have
        // made it through the Planner queue.
        using var db = TestDb.Create();
        var (defectId, controlId, artisanAId, _) = await SeedReadyToDispatchAsync(db);
        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.PlannerCapturedAt = null;
        await db.SaveChangesAsync();

        var ctrl = NewController(db, controlId);
        await ctrl.Dispatch(defectId, artisanAId);

        var refreshed = await db.DefectOrders.FindAsync(defectId);
        refreshed!.AssignedMechanicId.Should().BeNull();
    }

    [Fact]
    public async Task Dispatch_AlreadyDispatched_Refuses()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, artisanAId, artisanBId) = await SeedReadyToDispatchAsync(db);
        var ctrl = NewController(db, controlId);
        await ctrl.Dispatch(defectId, artisanAId);

        await ctrl.Dispatch(defectId, artisanBId);

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.AssignedMechanicId.Should().Be(artisanAId);   // unchanged
    }

    [Fact]
    public async Task Dispatch_InactiveArtisan_Refuses()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, artisanAId, _) = await SeedReadyToDispatchAsync(db);
        var artisan = await db.Users.FindAsync(artisanAId);
        artisan!.IsActive = false;
        await db.SaveChangesAsync();

        var ctrl = NewController(db, controlId);
        await ctrl.Dispatch(defectId, artisanAId);

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.AssignedMechanicId.Should().BeNull();
    }

    // ── Reassign ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reassign_SwapsArtisan()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, artisanAId, artisanBId) = await SeedReadyToDispatchAsync(db);
        var ctrl = NewController(db, controlId);
        await ctrl.Dispatch(defectId, artisanAId);

        await ctrl.Reassign(defectId, artisanBId, reason: "A went off-shift");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.AssignedMechanicId.Should().Be(artisanBId);
    }

    [Fact]
    public async Task Reassign_SameArtisan_Refuses()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, artisanAId, _) = await SeedReadyToDispatchAsync(db);
        var ctrl = NewController(db, controlId);
        await ctrl.Dispatch(defectId, artisanAId);

        await ctrl.Reassign(defectId, artisanAId, reason: null);

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.AssignedMechanicId.Should().Be(artisanAId);
    }

    [Fact]
    public async Task Reassign_NotYetDispatched_Refuses()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, _, artisanBId) = await SeedReadyToDispatchAsync(db);

        var ctrl = NewController(db, controlId);
        await ctrl.Reassign(defectId, artisanBId, reason: null);

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.AssignedMechanicId.Should().BeNull();
    }

    [Fact]
    public async Task Reassign_AfterCompletion_Refuses()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, artisanAId, artisanBId) = await SeedReadyToDispatchAsync(db);
        var ctrl = NewController(db, controlId);
        await ctrl.Dispatch(defectId, artisanAId);

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.RepairStatus = RepairStatus.Completed;
        defect.ResolvedAt    = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await ctrl.Reassign(defectId, artisanBId, reason: null);

        var refreshed = await db.DefectOrders.FindAsync(defectId);
        refreshed!.AssignedMechanicId.Should().Be(artisanAId);
    }

    // ── Escalate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Escalate_StampsNotesAndLeavesInQueue()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, _, _) = await SeedReadyToDispatchAsync(db);

        var ctrl = NewController(db, controlId);
        await ctrl.Escalate(defectId, reason: "No Mota-Engil artisan on shift");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.RepairStatus.Should().NotBe(RepairStatus.Completed);
        defect.ResolutionNotes.Should().Contain("ESCALATED");
        defect.ResolutionNotes.Should().Contain("No Mota-Engil artisan on shift");
        defect.AssignedMechanicId.Should().BeNull();
    }

    [Fact]
    public async Task Escalate_TwiceAppends_Not_Replaces()
    {
        using var db = TestDb.Create();
        var (defectId, controlId, _, _) = await SeedReadyToDispatchAsync(db);

        var ctrl = NewController(db, controlId);
        await ctrl.Escalate(defectId, reason: "first reason");
        await ctrl.Escalate(defectId, reason: "second reason");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.ResolutionNotes.Should().Contain("first reason");
        defect.ResolutionNotes.Should().Contain("second reason");
    }
}
