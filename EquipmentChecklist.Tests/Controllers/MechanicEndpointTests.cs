using System.Net;
using System.Net.Http.Json;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// Role-aware integration tests for the mechanic slice of
/// <c>SyncController</c>. Authorize attribute is
/// <c>[Authorize(Roles = "Mechanic,Admin")]</c> — so Operator and
/// Supervisor should both be refused.
/// </summary>
public class MechanicEndpointTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public MechanicEndpointTests(ApiTestFactory f) => _factory = f;

    [Fact]
    public async Task MechanicDefects_Anonymous_Returns401()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/sync/mechanic/defects");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MechanicDefects_OperatorRole_Returns403()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);

        var resp = await client.GetAsync("/api/sync/mechanic/defects");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MechanicDefects_SupervisorRole_Returns403()
    {
        // Supervisor is NOT included in the role list on the mechanic
        // endpoints — they manage operators, not the defect queue.
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        var resp = await client.GetAsync("/api/sync/mechanic/defects");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task MechanicDefects_MechanicRole_Returns200()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.GetAsync("/api/sync/mechanic/defects");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MechanicQueueDto>();
        body.Should().NotBeNull();
        body!.MyOrders.Should().NotBeNull();
        body.Unassigned.Should().NotBeNull();
    }

    [Fact]
    public async Task MechanicDefects_AdminRole_Returns200()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.AdminEmail);

        var resp = await client.GetAsync("/api/sync/mechanic/defects");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SupervisorNoGo_AccessibleToMechanic()
    {
        // Round 1's photo-capture task opened the supervisor NO-GO endpoint
        // to mechanics too (they need the same fleet view). Lock that in
        // with a test so a future refactor can't quietly remove the role.
        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.GetAsync("/api/sync/supervisor/nogo");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
