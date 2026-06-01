using System.Net;
using System.Net.Http.Json;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// Role-aware integration tests for the supervisor-scoped slice of
/// <c>SyncController</c>. Each test goes through real /login → real JWT
/// → real authorize attribute → real endpoint, so any regression in the
/// auth pipeline shows up.
///
/// The seeded team graph (from <see cref="ApiTestFactory.InitializeAsync"/>):
///   SupervisorEmail  manages  OperatorEmail
///   Sup2Email        manages  Operator2Email
/// </summary>
public class SupervisorEndpointTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public SupervisorEndpointTests(ApiTestFactory f) => _factory = f;

    // ── Role gate ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SupervisorQueue_Anonymous_Returns401()
    {
        var client = _factory.CreateClient();   // no Authorization header

        var resp = await client.GetAsync("/api/sync/supervisor/queue");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SupervisorQueue_OperatorRole_Returns403()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);

        var resp = await client.GetAsync("/api/sync/supervisor/queue");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SupervisorQueue_MechanicRole_Returns403()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        var resp = await client.GetAsync("/api/sync/supervisor/queue");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SupervisorQueue_SupervisorRole_Returns200()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        var resp = await client.GetAsync("/api/sync/supervisor/queue");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SupervisorQueue_AdminRole_Returns200()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.AdminEmail);

        var resp = await client.GetAsync("/api/sync/supervisor/queue");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Defence-in-depth: team scope ────────────────────────────────────────

    [Fact]
    public async Task SupervisorQueue_SupervisorSeesOnlyOwnTeam()
    {
        // Both teams have a pending submission. Sup should only see one.
        var machineId = await _factory.SeedMachineAsync();
        var myTeamSubId   = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var otherTeamSubId = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.Operator2Email);

        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);
        var queue  = await client.GetFromJsonAsync<List<SupervisorQueueItemDto>>(
            "/api/sync/supervisor/queue");

        queue.Should().NotBeNull();
        queue!.Select(q => q.SubmissionId)
            .Should().Contain(myTeamSubId)
            .And.NotContain(otherTeamSubId);
    }

    [Fact]
    public async Task SupervisorQueue_AdminSeesAllTeams()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subA = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);
        var subB = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.Operator2Email);

        var client = await _factory.SignInAsync(ApiTestFactory.AdminEmail);
        var queue  = await client.GetFromJsonAsync<List<SupervisorQueueItemDto>>(
            "/api/sync/supervisor/queue");

        queue.Should().NotBeNull();
        queue!.Select(q => q.SubmissionId)
            .Should().Contain(new[] { subA, subB });
    }

    [Fact]
    public async Task SupervisorReview_NonAdminSupervisorOutsideOwnTeam_Returns403()
    {
        // Operator2 belongs to Sup2's team. Sup (not Sup2) tries to view
        // their submission — the defence-in-depth check should refuse,
        // even though the role gate would technically allow Supervisor.
        var machineId = await _factory.SeedMachineAsync();
        var foreignSubId = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.Operator2Email);

        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        var resp = await client.GetAsync(
            $"/api/sync/supervisor/queue/{foreignSubId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SupervisorReview_OwnTeamSubmission_Returns200WithItems()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);

        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);
        var resp   = await client.GetAsync($"/api/sync/supervisor/queue/{subId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SupervisorReviewDto>();
        body.Should().NotBeNull();
        body!.SubmissionId.Should().Be(subId);
    }

    [Fact]
    public async Task SupervisorReview_AdminCrossesTeamBoundaries_Returns200()
    {
        // Admin doesn't have a team — should see anyone's submission.
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.Operator2Email);

        var client = await _factory.SignInAsync(ApiTestFactory.AdminEmail);
        var resp   = await client.GetAsync($"/api/sync/supervisor/queue/{subId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
