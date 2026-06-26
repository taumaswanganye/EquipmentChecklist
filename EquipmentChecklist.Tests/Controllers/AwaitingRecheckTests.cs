using System.Net;
using System.Net.Http.Json;
using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// Phase 6.9b — HTTP tests for GET /api/sync/operator/awaiting-recheck.
///
/// The endpoint is the data source for the mobile dashboard tile that
/// catches operators who missed the machine-cleared push notification.
/// Three scenarios are essential to lock down:
///
/// 1. Cleared after NO-GO, no re-check yet → returns the machine.
/// 2. Operator already did a fresh checklist → list is empty.
/// 3. Machine is back down again → list is empty (don't surface it as
///    "ready for re-check" when it's actually broken).
/// </summary>
public class AwaitingRecheckTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public AwaitingRecheckTests(ApiTestFactory f) => _factory = f;

    /// <summary>Helper: seed a machine + operator NO-GO + admin clearance
    /// timestamp. Returns the machine ID for the test to point at.</summary>
    private async Task<int> SeedNoGoThenClearAsync(
        string operatorEmail,
        DateTime noGoAt,
        DateTime? clearedAt,
        bool stillImmobilised = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var op = await users.FindByEmailAsync(operatorEmail);
        op.Should().NotBeNull("seed user should exist on the test factory");

        var machine = new Machine
        {
            MachineNumber              = $"M-{Guid.NewGuid().ToString("N")[..6]}",
            MachineName                = "Recheck Loader",
            Type                       = MachineType.FEL,
            IsActive                   = true,
            IsImmobilised              = stillImmobilised,
            ClearedAt                  = clearedAt,
            AdminClearanceNotes        = clearedAt.HasValue ? "Replaced brake pads" : null
        };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        db.ChecklistSubmissions.Add(new ChecklistSubmission
        {
            MachineId   = machine.Id,
            OperatorId  = op!.Id,
            Status      = ChecklistStatus.NoGo,
            SubmittedAt = noGoAt
        });
        await db.SaveChangesAsync();

        return machine.Id;
    }

    private async Task SeedFollowUpSubmissionAsync(int machineId, string operatorEmail, DateTime at)
    {
        using var scope = _factory.Services.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var op    = await users.FindByEmailAsync(operatorEmail);

        db.ChecklistSubmissions.Add(new ChecklistSubmission
        {
            MachineId   = machineId,
            OperatorId  = op!.Id,
            Status      = ChecklistStatus.Go,
            SubmittedAt = at
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ClearedAfterNoGo_NoRecheck_ReturnsTheMachine()
    {
        var noGoAt    = DateTime.UtcNow.AddHours(-6);
        var clearedAt = DateTime.UtcNow.AddHours(-1);
        var machineId = await SeedNoGoThenClearAsync(ApiTestFactory.OperatorEmail, noGoAt, clearedAt);

        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);
        var resp   = await client.GetAsync("/api/sync/operator/awaiting-recheck");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await resp.Content.ReadFromJsonAsync<List<AwaitingRecheckDto>>();
        list.Should().NotBeNull();
        list!.Should().ContainSingle(m => m.MachineId == machineId);
        list.First(m => m.MachineId == machineId)
            .AdminClearanceNotes.Should().Be("Replaced brake pads");
    }

    [Fact]
    public async Task OperatorAlreadyRechecked_NotReturned()
    {
        var noGoAt    = DateTime.UtcNow.AddHours(-6);
        var clearedAt = DateTime.UtcNow.AddHours(-2);
        var machineId = await SeedNoGoThenClearAsync(ApiTestFactory.OperatorEmail, noGoAt, clearedAt);
        // Operator submitted a clean checklist after the clearance.
        await SeedFollowUpSubmissionAsync(machineId, ApiTestFactory.OperatorEmail, DateTime.UtcNow.AddMinutes(-30));

        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);
        var list   = await client.GetFromJsonAsync<List<AwaitingRecheckDto>>(
            "/api/sync/operator/awaiting-recheck");

        list!.Should().NotContain(m => m.MachineId == machineId);
    }

    [Fact]
    public async Task MachineStillDown_NotReturned()
    {
        // ClearedAt is set (admin tried to clear earlier) but a new NO-GO
        // came in and re-immobilised the machine. It's not actually
        // available for re-check, so the tile should hide it.
        var noGoAt    = DateTime.UtcNow.AddHours(-6);
        var clearedAt = DateTime.UtcNow.AddHours(-3);
        var machineId = await SeedNoGoThenClearAsync(
            ApiTestFactory.OperatorEmail, noGoAt, clearedAt, stillImmobilised: true);

        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);
        var list   = await client.GetFromJsonAsync<List<AwaitingRecheckDto>>(
            "/api/sync/operator/awaiting-recheck");

        list!.Should().NotContain(m => m.MachineId == machineId);
    }

    [Fact]
    public async Task NeverCleared_NotReturned()
    {
        // NO-GO was raised but admin hasn't cleared the machine yet.
        // Should not appear — the operator can't do a re-check on a
        // machine that's still locked out for repairs.
        var noGoAt    = DateTime.UtcNow.AddHours(-6);
        var machineId = await SeedNoGoThenClearAsync(
            ApiTestFactory.OperatorEmail, noGoAt, clearedAt: null);

        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);
        var list   = await client.GetFromJsonAsync<List<AwaitingRecheckDto>>(
            "/api/sync/operator/awaiting-recheck");

        list!.Should().NotContain(m => m.MachineId == machineId);
    }

    [Fact]
    public async Task ClearanceOlderThanNoGo_NotReturned()
    {
        // Edge case: machine was cleared in the past, then this operator
        // raised a NEW NO-GO. The old clearance shouldn't satisfy this
        // NO-GO's re-check requirement.
        var clearedAt = DateTime.UtcNow.AddDays(-2);
        var noGoAt    = DateTime.UtcNow.AddHours(-1);   // after the clearance
        var machineId = await SeedNoGoThenClearAsync(ApiTestFactory.OperatorEmail, noGoAt, clearedAt);

        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);
        var list   = await client.GetFromJsonAsync<List<AwaitingRecheckDto>>(
            "/api/sync/operator/awaiting-recheck");

        list!.Should().NotContain(m => m.MachineId == machineId);
    }

    [Fact]
    public async Task OtherOperatorsNoGo_NotReturnedForMe()
    {
        // Operator2 raises the NO-GO; admin clears the machine. Operator1
        // should NOT see "awaiting re-check" because they didn't raise it.
        var noGoAt    = DateTime.UtcNow.AddHours(-6);
        var clearedAt = DateTime.UtcNow.AddHours(-1);
        var machineId = await SeedNoGoThenClearAsync(ApiTestFactory.Operator2Email, noGoAt, clearedAt);

        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);
        var list   = await client.GetFromJsonAsync<List<AwaitingRecheckDto>>(
            "/api/sync/operator/awaiting-recheck");

        list!.Should().NotContain(m => m.MachineId == machineId);
    }
}
