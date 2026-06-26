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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// Direct-instantiation tests for <see cref="PlannerController"/>.
///
/// We don't go through HTTP because:
///   - These are MVC controllers (return RedirectToAction), not API.
///   - We care about DB side effects + workflow state transitions, not
///     about Razor rendering.
///
/// Each test builds its own in-memory DbContext, real UserManager, real
/// AuditService, and a controller wired with the standard
/// "I'm the Planner" claims principal. Side effects are verified by
/// reading the DB back through a second context scope.
/// </summary>
public class PlannerControllerTests
{
    // ── Fixture plumbing ─────────────────────────────────────────────────────

    /// <summary>Build a UserManager + AuditService backed by the same
    /// in-memory DB the controller uses. Returned together because
    /// AuditService takes the UserManager as a constructor dep.</summary>
    private static (UserManager<ApplicationUser> users, AuditService audit)
        BuildUserManagerAndAudit(ApplicationDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ApplicationDbContext>(db);
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddIdentityCore<ApplicationUser>()
                .AddEntityFrameworkStores<ApplicationDbContext>();
        var sp = services.BuildServiceProvider();

        var users = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var audit = new AuditService(
            db,
            sp.GetRequiredService<IHttpContextAccessor>(),
            users,
            NullLogger<AuditService>.Instance);
        return (users, audit);
    }

    private static PlannerController NewController(
        ApplicationDbContext db,
        string plannerUserId)
    {
        var (users, audit) = BuildUserManagerAndAudit(db);
        var ctrl           = new PlannerController(db, users, audit);
        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.NameIdentifier, plannerUserId),
                    new Claim(ClaimTypes.Name,           "planner@test.local")
                }, "TestAuth"))
            }
        };
        return ctrl;
    }

    private static async Task<(int machineId, int submissionId, int defectId, string plannerId)>
        SeedDefectWaitingForPlannerAsync(ApplicationDbContext db)
    {
        // Minimal users — operator + planner. UserManager isn't required
        // for these inserts; we just need rows with the right shape.
        var op      = new ApplicationUser { UserName = "op",      Email = "op@x",      FullName = "Op",      EmployeeNumber = "E1", IsActive = true };
        var planner = new ApplicationUser { UserName = "planner", Email = "planner@x", FullName = "Planner", EmployeeNumber = "E2", IsActive = true };
        db.Users.AddRange(op, planner);

        var machine = new Machine
        {
            MachineNumber = "M-001",
            MachineName   = "Test Grader",
            Type          = MachineType.Grader,
            IsActive      = true
        };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        var template = new ChecklistTemplate
        {
            Name = "T1", MachineType = MachineType.Grader, MachineId = machine.Id
        };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        var tItem = new ChecklistTemplateItem
        {
            TemplateId = template.Id, ItemName = "Brakes", SortOrder = 0, IsNoGoItem = true
        };
        db.ChecklistTemplateItems.Add(tItem);
        await db.SaveChangesAsync();

        var submission = new ChecklistSubmission
        {
            MachineId   = machine.Id,
            OperatorId  = op.Id,
            Status      = ChecklistStatus.NoGo,
            SubmittedAt = DateTime.UtcNow.AddHours(-2)
        };
        db.ChecklistSubmissions.Add(submission);
        await db.SaveChangesAsync();

        var sItem = new SubmissionItem
        {
            SubmissionId   = submission.Id,
            TemplateItemId = tItem.Id,
            Status         = ItemStatus.Defect
        };
        db.SubmissionItems.Add(sItem);
        await db.SaveChangesAsync();

        var defect = new DefectOrder
        {
            SubmissionId      = submission.Id,
            SubmissionItemId  = sItem.Id,
            DefectDescription = "Brakes failed",
            CreatedAt         = DateTime.UtcNow.AddHours(-2),
            RepairStatus      = RepairStatus.Pending
        };
        db.DefectOrders.Add(defect);
        await db.SaveChangesAsync();

        return (machine.Id, submission.Id, defect.Id, planner.Id);
    }

    // ── Capture (defect) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Capture_StampsPlannerAndJobcard()
    {
        using var db = TestDb.Create();
        var (_, _, defectId, plannerId) = await SeedDefectWaitingForPlannerAsync(db);

        var ctrl = NewController(db, plannerId);
        var result = await ctrl.Capture(defectId, "WO-12345");

        result.Should().BeOfType<RedirectToActionResult>()
              .Which.ActionName.Should().Be("Index");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.PlannerCapturedAt.Should().NotBeNull();
        defect.PlannerCapturedById.Should().Be(plannerId);
        defect.JobCardNumber.Should().Be("WO-12345");
    }

    [Fact]
    public async Task Capture_NoJobcardSupplied_StoresNullNotEmpty()
    {
        // Empty / whitespace jobcard should normalise to null so the
        // Control Room dispatch view doesn't display "  ".
        using var db = TestDb.Create();
        var (_, _, defectId, plannerId) = await SeedDefectWaitingForPlannerAsync(db);

        var ctrl = NewController(db, plannerId);
        await ctrl.Capture(defectId, "   ");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.JobCardNumber.Should().BeNull();
        defect.PlannerCapturedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Capture_AlreadyCaptured_Refuses()
    {
        // Idempotency: re-capturing must not silently double-stamp.
        using var db = TestDb.Create();
        var (_, _, defectId, plannerId) = await SeedDefectWaitingForPlannerAsync(db);
        var ctrl = NewController(db, plannerId);
        await ctrl.Capture(defectId, "WO-1");
        var firstAt = (await db.DefectOrders.FindAsync(defectId))!.PlannerCapturedAt;

        await ctrl.Capture(defectId, "WO-2");
        var defect = await db.DefectOrders.FindAsync(defectId);

        defect!.PlannerCapturedAt.Should().Be(firstAt);
        defect.JobCardNumber.Should().Be("WO-1");
    }

    // ── Reject ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_RequiresReason()
    {
        using var db = TestDb.Create();
        var (_, _, defectId, plannerId) = await SeedDefectWaitingForPlannerAsync(db);
        var ctrl = NewController(db, plannerId);

        await ctrl.Reject(defectId, "   ");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.RepairStatus.Should().NotBe(RepairStatus.Completed);
    }

    [Fact]
    public async Task Reject_ValidReason_ClosesDefectWithStampedNotes()
    {
        using var db = TestDb.Create();
        var (_, _, defectId, plannerId) = await SeedDefectWaitingForPlannerAsync(db);
        var ctrl = NewController(db, plannerId);

        await ctrl.Reject(defectId, "duplicate of WO-7720");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.RepairStatus.Should().Be(RepairStatus.Completed);
        defect.ResolvedAt.Should().NotBeNull();
        defect.ResolutionNotes.Should().Contain("REJECTED BY PLANNER");
        defect.ResolutionNotes.Should().Contain("duplicate of WO-7720");
    }

    [Fact]
    public async Task Reject_AfterCapture_Refuses()
    {
        // Once a defect's been pushed to Control Room it's too late for
        // the Planner to reject — they'd have to cancel the jobcard.
        using var db = TestDb.Create();
        var (_, _, defectId, plannerId) = await SeedDefectWaitingForPlannerAsync(db);
        var ctrl = NewController(db, plannerId);
        await ctrl.Capture(defectId, "WO-1");

        await ctrl.Reject(defectId, "changed my mind");

        var defect = await db.DefectOrders.FindAsync(defectId);
        defect!.RepairStatus.Should().NotBe(RepairStatus.Completed);
    }

    // ── CaptureSubmission (Phase 4.B) ────────────────────────────────────────

    [Fact]
    public async Task CaptureSubmission_StampsCapturedAtAndPlannerId()
    {
        using var db = TestDb.Create();
        var (machineId, subId, _, plannerId) = await SeedDefectWaitingForPlannerAsync(db);
        // Convert to a clean GO submission for this test
        var sub = await db.ChecklistSubmissions.FindAsync(subId);
        sub!.Status = ChecklistStatus.Go;
        await db.SaveChangesAsync();

        var ctrl = NewController(db, plannerId);
        await ctrl.CaptureSubmission(subId);

        var refreshed = await db.ChecklistSubmissions.FindAsync(subId);
        refreshed!.CapturedAt.Should().NotBeNull();
        refreshed.CapturedByPlannerId.Should().Be(plannerId);
    }

    [Fact]
    public async Task CaptureSubmission_DoubleCapture_Refuses()
    {
        using var db = TestDb.Create();
        var (_, subId, _, plannerId) = await SeedDefectWaitingForPlannerAsync(db);
        var sub = await db.ChecklistSubmissions.FindAsync(subId);
        sub!.Status = ChecklistStatus.Go;
        await db.SaveChangesAsync();

        var ctrl = NewController(db, plannerId);
        await ctrl.CaptureSubmission(subId);
        var first = (await db.ChecklistSubmissions.FindAsync(subId))!.CapturedAt;

        await ctrl.CaptureSubmission(subId);
        var refreshed = await db.ChecklistSubmissions.FindAsync(subId);

        refreshed!.CapturedAt.Should().Be(first);
    }

    // ── CaptureAllSubmissions (Phase 6.2) ────────────────────────────────────

    [Fact]
    public async Task CaptureAllSubmissions_StampsEveryEligibleRow()
    {
        using var db = TestDb.Create();
        var (machineId, _, _, plannerId) = await SeedDefectWaitingForPlannerAsync(db);
        var op = await db.Users.FirstAsync(u => u.Email == "op@x");

        // Seed three GO submissions in the 48h window.
        for (int i = 0; i < 3; i++)
        {
            db.ChecklistSubmissions.Add(new ChecklistSubmission
            {
                MachineId   = machineId,
                OperatorId  = op.Id,
                Status      = ChecklistStatus.Go,
                SubmittedAt = DateTime.UtcNow.AddHours(-i - 1)
            });
        }
        await db.SaveChangesAsync();

        var ctrl = NewController(db, plannerId);
        await ctrl.CaptureAllSubmissions();

        var captured = await db.ChecklistSubmissions
            .CountAsync(s => s.CapturedAt != null && s.Status == ChecklistStatus.Go);
        captured.Should().Be(3);
    }

    [Fact]
    public async Task CaptureAllSubmissions_IgnoresUnsignedGoBut()
    {
        // A GO-BUT 24H without supervisor sign-off should NOT be captured
        // — that's still waiting on the supervisor, the Planner shouldn't
        // sweep it into the shift log prematurely.
        using var db = TestDb.Create();
        var (machineId, _, _, plannerId) = await SeedDefectWaitingForPlannerAsync(db);
        var op = await db.Users.FirstAsync(u => u.Email == "op@x");

        db.ChecklistSubmissions.Add(new ChecklistSubmission
        {
            MachineId          = machineId,
            OperatorId         = op.Id,
            Status             = ChecklistStatus.GoButRepair24H,
            SubmittedAt        = DateTime.UtcNow.AddHours(-1),
            SupervisorSignedAt = null
        });
        await db.SaveChangesAsync();

        var ctrl = NewController(db, plannerId);
        await ctrl.CaptureAllSubmissions();

        var unsigned = await db.ChecklistSubmissions
            .FirstAsync(s => s.Status == ChecklistStatus.GoButRepair24H);
        unsigned.CapturedAt.Should().BeNull();
    }
}
