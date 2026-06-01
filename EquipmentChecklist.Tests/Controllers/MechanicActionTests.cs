using System.Net;
using System.Net.Http.Json;
using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// HTTP-layer tests for the mechanic write endpoints:
///   * /claim           — claim race protection
///   * /order-part      — moves the job into AwaitingParts
///   * /complete        — closes the defect; signature is mandatory
///
/// We don't have a second mechanic seeded by default. The "race" test
/// uses Admin as the second claimer because Admin is also allowed on
/// these endpoints — which gives us a different UserId without
/// needing to extend the factory.
/// </summary>
public class MechanicActionTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public MechanicActionTests(ApiTestFactory f) => _factory = f;

    // ── Claim ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Claim_UnassignedDefect_AssignsToCallerAndFlipsToInProgress()
    {
        var machineId = await _factory.SeedMachineAsync();
        var (_, orderId) = await _factory.SeedUnassignedDefectAsync(
            machineId, ApiTestFactory.OperatorEmail);

        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.PostAsync(
            $"/api/sync/mechanic/defects/{orderId}/claim", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await db.DefectOrders.FindAsync(orderId);
        order!.AssignedMechanicId.Should().NotBeNullOrWhiteSpace();
        order.RepairStatus.Should().Be(RepairStatus.InProgress);
    }

    [Fact]
    public async Task Claim_AlreadyClaimedByAnother_Returns400()
    {
        var machineId = await _factory.SeedMachineAsync();
        var (_, orderId) = await _factory.SeedUnassignedDefectAsync(
            machineId, ApiTestFactory.OperatorEmail);

        // Mechanic claims first — succeeds.
        var first  = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);
        (await first.PostAsync($"/api/sync/mechanic/defects/{orderId}/claim", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Admin tries to claim the same row — should be refused, NOT
        // silently overwritten. The endpoint protects against the race
        // by returning BadRequest with an explanation.
        var second = await _factory.SignInAsync(ApiTestFactory.AdminEmail);
        var resp   = await second.PostAsync(
            $"/api/sync/mechanic/defects/{orderId}/claim", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Claim_NonExistentOrder_Returns404()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.PostAsync(
            "/api/sync/mechanic/defects/9999/claim", null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Order-part ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OrderPart_MissingPartRequired_Returns400()
    {
        var machineId = await _factory.SeedMachineAsync();
        var (_, orderId) = await _factory.SeedUnassignedDefectAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/mechanic/defects/{orderId}/order-part",
            new OrderPartRequest { PartRequired = "", PartNumber = null });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task OrderPart_Valid_FlipsToAwaitingParts_AndAutoClaims()
    {
        var machineId = await _factory.SeedMachineAsync();
        var (_, orderId) = await _factory.SeedUnassignedDefectAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/mechanic/defects/{orderId}/order-part",
            new OrderPartRequest { PartRequired = "Brake pad", PartNumber = "BP-1234" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await db.DefectOrders.FindAsync(orderId);
        order!.PartRequired.Should().Be("Brake pad");
        order.PartNumber.Should().Be("BP-1234");
        order.RepairStatus.Should().Be(RepairStatus.AwaitingParts);
        // Auto-claim: previously unassigned, now should be the caller.
        order.AssignedMechanicId.Should().NotBeNullOrWhiteSpace();
    }

    // ── Complete ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Complete_MissingSignature_Returns400()
    {
        var machineId = await _factory.SeedMachineAsync();
        var (_, orderId) = await _factory.SeedUnassignedDefectAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/mechanic/defects/{orderId}/complete",
            new CompleteRepairRequest { Notes = "fixed", Signature = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Complete_Valid_MarksCompletedAndStoresSignature()
    {
        var machineId = await _factory.SeedMachineAsync();
        var (_, orderId) = await _factory.SeedUnassignedDefectAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.PostAsJsonAsync(
            $"/api/sync/mechanic/defects/{orderId}/complete",
            new CompleteRepairRequest
            {
                Notes     = "brakes replaced",
                Signature = "data:image/png;base64,abc"
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var order = await db.DefectOrders.FindAsync(orderId);
        order!.RepairStatus.Should().Be(RepairStatus.Completed);
        order.MechanicSignature.Should().Be("data:image/png;base64,abc");
        order.ResolutionNotes.Should().Be("brakes replaced");
        order.ResolvedAt.Should().NotBeNull();
    }
}
