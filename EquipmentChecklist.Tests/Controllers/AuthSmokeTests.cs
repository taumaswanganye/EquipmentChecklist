using System.Net;
using System.Net.Http.Json;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace EquipmentChecklist.Tests.Controllers;

/// <summary>
/// Basic smoke tests for the auth + liveness endpoints. If these fail,
/// nothing else in the suite is meaningful — every role-aware test
/// depends on /login issuing a JWT with role claims.
/// </summary>
public class AuthSmokeTests : IClassFixture<ApiTestFactory>
{
    private readonly ApiTestFactory _factory;
    public AuthSmokeTests(ApiTestFactory f) => _factory = f;

    [Fact]
    public async Task Ping_Anonymous_Returns200()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/sync/ping");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsTokenAndRoles()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/sync/login",
            new SyncLoginRequest
            {
                Email    = ApiTestFactory.SupervisorEmail,
                Password = ApiTestFactory.Password
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncLoginResponse>();
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrWhiteSpace();
        body.User.Email.Should().Be(ApiTestFactory.SupervisorEmail);
        // Role claim must be in the response so the mobile app can build
        // the right sidebar — and so the JWT carries the claim downstream.
        body.User.Roles.Should().Contain("Supervisor");
    }

    [Fact]
    public async Task Login_BadPassword_Returns401()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/sync/login",
            new SyncLoginRequest
            {
                Email    = ApiTestFactory.SupervisorEmail,
                Password = "wrong-password"
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_WithToken_ReturnsCurrentUser()
    {
        var client = await _factory.SignInAsync(ApiTestFactory.OperatorEmail);

        var resp = await client.GetAsync("/api/sync/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncUserDto>();
        body!.Email.Should().Be(ApiTestFactory.OperatorEmail);
        body.Roles.Should().Contain("Operator");
    }

    [Fact]
    public async Task Me_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/sync/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
