using System.Net.Http.Headers;
using System.Net.Http.Json;
using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Tests.Helpers;

/// <summary>
/// Thin shim over the real <c>POST /api/sync/login</c> endpoint — exercises
/// the same JWT-issuance code path that the mobile client uses, so any
/// regression in role-claim emission shows up here too.
/// </summary>
public static class AuthHelpers
{
    /// <summary>
    /// Drives login → returns a fresh <see cref="HttpClient"/> with the
    /// Bearer token attached so subsequent calls hit authenticated endpoints
    /// as that user.
    /// </summary>
    public static async Task<HttpClient> SignInAsync(
        this ApiTestFactory factory, string email, string? password = null)
    {
        var client = factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/sync/login",
            new SyncLoginRequest
            {
                Email    = email,
                Password = password ?? ApiTestFactory.Password
            });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<SyncLoginResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body!.Token);
        return client;
    }
}
