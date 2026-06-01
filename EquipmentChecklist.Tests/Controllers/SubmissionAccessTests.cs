using System.Net;
using System.Net.Http.Json;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// The matrix of who can view a given submission's PDF + details.
///
/// <para>The endpoints are:</para>
/// <list type="bullet">
///   <item><description>GET /api/sync/submissions/{id}/pdf</description></item>
///   <item><description>GET /api/sync/submissions/{id}/details</description></item>
/// </list>
///
/// <para>Both gate identically:</para>
/// <list type="bullet">
///   <item><description>Admin — anything.</description></item>
///   <item><description>Operator — only their own submission.</description></item>
///   <item><description>Supervisor — only operators on their team.</description></item>
///   <item><description>Mechanic — only machines they're assigned to.</description></item>
/// </list>
///
/// <para>Each test exercises both endpoints to lock the parity in place —
/// if a future refactor accidentally diverges one gate, the test catches it.</para>
/// </summary>
public class SubmissionAccessTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public SubmissionAccessTests(ApiTestFactory f) => _factory = f;

    // ── Operator ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Operator_OwnSubmission_DetailsAndPdf_Returns200()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);

        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);

        (await client.GetAsync($"/api/sync/submissions/{subId}/details"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var pdfResp = await client.GetAsync($"/api/sync/submissions/{subId}/pdf");
        pdfResp.StatusCode.Should().Be(HttpStatusCode.OK);
        pdfResp.Content.Headers.ContentType?.MediaType.Should().Be("application/pdf");
    }

    [Fact]
    public async Task Operator_OtherOperatorsSubmission_DetailsAndPdf_Returns403()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.Operator2Email);

        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);

        (await client.GetAsync($"/api/sync/submissions/{subId}/details"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/sync/submissions/{subId}/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Supervisor ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Supervisor_OwnTeamSubmission_DetailsAndPdf_Returns200()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);

        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        (await client.GetAsync($"/api/sync/submissions/{subId}/details"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/sync/submissions/{subId}/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Supervisor_OtherTeamSubmission_DetailsAndPdf_Returns403()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.Operator2Email);

        var client = await _factory.SignInAsync(ApiTestFactory.SupervisorEmail);

        (await client.GetAsync($"/api/sync/submissions/{subId}/details"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/sync/submissions/{subId}/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Mechanic ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mechanic_AssignedMachineSubmission_DetailsAndPdf_Returns200()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);
        await _factory.AssignMechanicAsync(machineId, ApiTestFactory.MechanicEmail);

        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        (await client.GetAsync($"/api/sync/submissions/{subId}/details"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/sync/submissions/{subId}/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Mechanic_UnassignedMachine_DetailsAndPdf_Returns403()
    {
        // Mechanic has no MachineAssignment for this machine.
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.OperatorEmail);

        var client = await _factory.SignInAsync(ApiTestFactory.MechanicEmail);

        (await client.GetAsync($"/api/sync/submissions/{subId}/details"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/sync/submissions/{subId}/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── Admin ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Admin_AnyOperatorsSubmission_DetailsAndPdf_Returns200()
    {
        var machineId = await _factory.SeedMachineAsync();
        var subId     = await _factory.SeedPendingSubmissionAsync(
            machineId, ApiTestFactory.Operator2Email);

        var client = await _factory.SignInAsync(ApiTestFactory.AdminEmail);

        (await client.GetAsync($"/api/sync/submissions/{subId}/details"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/sync/submissions/{subId}/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 404 path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Details_NonExistentSubmission_Returns404()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.AdminEmail);

        var resp = await client.GetAsync("/api/sync/submissions/9999/details");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
