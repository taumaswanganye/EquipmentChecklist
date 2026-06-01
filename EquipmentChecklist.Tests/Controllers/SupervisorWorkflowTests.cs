using System.Net;
using System.Net.Http.Json;
using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// HTTP-layer tests for the supervisor sign-off and reject workflows.
///
/// Round 1 already covered the service-method logic in
/// <c>ChecklistServiceTests.SupervisorSignOff_*</c>. This file proves the
/// controller correctly:
///   * Maps request body fields into the service call.
///   * Persists database state visible to subsequent reads.
///   * Returns the documented HTTP codes on validation failures.
///   * Runs the side-effects (machine immobilisation + defect orders) on
///     reject.
/// </summary>
public class SupervisorWorkflowTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public SupervisorWorkflowTests(ApiTestFactory f) => _factory = f;

    // ── Sign-off ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignOff_MissingSignature_Returns400()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/supervisor/queue/{subId}/signoff",
            new SupervisorSignOffRequest { Resolution = 2, Signature = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SignOff_Valid24H_PersistsSupervisorAndStatus()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/supervisor/queue/{subId}/signoff",
            new SupervisorSignOffRequest
            {
                Resolution = 2,  // 24H
                Signature  = "data:image/png;base64,abc"
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Read DB state directly — quickest way to assert the side effects.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var sub = await db.ChecklistSubmissions.FindAsync(subId);
        sub!.Status.Should().Be(ChecklistStatus.GoButRepair24H);
        sub.SupervisorId.Should().NotBeNullOrWhiteSpace();
        sub.SupervisorSignedAt.Should().NotBeNull();
        sub.SupervisorSignature.Should().Be("data:image/png;base64,abc");
    }

    [Fact]
    public async Task SignOff_Resolution3_MapsToGoTillNextService()
    {
        // The controller maps req.Resolution == 3 to GoTillNextService and
        // anything else (typically 2) to GoButRepair24H. Catches the kind
        // of magic-number drift a refactor could introduce silently.
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        await client.PostAsJsonAsync(
            $"/api/sync/supervisor/queue/{subId}/signoff",
            new SupervisorSignOffRequest { Resolution = 3, Signature = "sig" });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ChecklistSubmissions.FindAsync(subId))!
            .Status.Should().Be(ChecklistStatus.GoTillNextService);
    }

    // ── Reject ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_MissingReason_Returns400()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var mechId = await _factory.UserIdAsync(ApiTestFactory.MechanicEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/supervisor/queue/{subId}/reject",
            new SupervisorRejectRequest { Reason = "", MechanicId = mechId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reject_InvalidMechanic_Returns400()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        // Using an OPERATOR's id as the "mechanic" should fail validation.
        var operatorId = await _factory.UserIdAsync(ApiTestFactory.OperatorEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/supervisor/queue/{subId}/reject",
            new SupervisorRejectRequest
            {
                Reason     = "Brakes failing",
                MechanicId = operatorId
            });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reject_ValidPath_ImmobilisesMachine_AndCreatesDefectOrders()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);

        // The reject endpoint creates DefectOrders ONLY for items the
        // operator already marked as Defect. Seed one defective item.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var tplItem = await db.ChecklistTemplateItems
                .Where(i => i.Template.MachineId == machineId)
                .FirstAsync();
            db.SubmissionItems.Add(new SubmissionItem
            {
                SubmissionId   = subId,
                TemplateItemId = tplItem.Id,
                Status         = ItemStatus.Defect,
                Notes          = "brakes failed"
            });
            await db.SaveChangesAsync();
        }

        var mechId = await _factory.UserIdAsync(ApiTestFactory.MechanicEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/supervisor/queue/{subId}/reject",
            new SupervisorRejectRequest
            {
                Reason     = "Brakes failing — machine unsafe",
                MechanicId = mechId
            });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>();

        // Status flipped to Rejected.
        var sub = await verifyDb.ChecklistSubmissions.FindAsync(subId);
        sub!.Status.Should().Be(ChecklistStatus.Rejected);
        sub.RejectionReason.Should().Contain("Brakes failing");
        sub.RejectedMechanicId.Should().Be(mechId);

        // Machine immobilised with an audit-trail reason.
        var machine = await verifyDb.Machines.FindAsync(machineId);
        machine!.IsImmobilised.Should().BeTrue();
        machine.ImmobilisedReason.Should().Contain("Brakes failing");

        // DefectOrder created and routed to the chosen mechanic.
        var orders = await verifyDb.DefectOrders
            .Where(d => d.SubmissionId == subId)
            .ToListAsync();
        orders.Should().HaveCount(1);
        orders[0].AssignedMechanicId.Should().Be(mechId);
        orders[0].RepairStatus.Should().Be(RepairStatus.InProgress);
    }

    [Fact]
    public async Task Reject_NonAdminSupervisorOutsideOwnTeam_Returns403()
    {
        // Sup tries to reject a submission belonging to Sup2's operator.
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.Operator2Email);
        var mechId = await _factory.UserIdAsync(ApiTestFactory.MechanicEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/supervisor/queue/{subId}/reject",
            new SupervisorRejectRequest { Reason = "x", MechanicId = mechId });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
