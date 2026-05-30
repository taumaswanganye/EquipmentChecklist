# MAUI Blazor Hybrid mobile client — runbook

Phase 1 (operators-first MVP).

## What you have already

- ✅ Server-side sync API at `/api/sync/*` (login, machines, template, submissions, recent).
- ✅ Sync DTOs in `EquipmentChecklist/DTOs/DTOs.cs`.
- ✅ JWT bearer auth already wired in `Program.cs` (the `multi` scheme routes every `/api/*` request through JWT).

## What you'll do locally (5 minutes)

These steps need the **.NET MAUI workload** which has to be installed on your Windows / Mac dev machine — it can't be set up from inside this chat.

### 1 · Install the workload (once per machine)

```powershell
dotnet workload install maui
```

### 2 · Add the mobile project to your solution

From the repo root (where `EquipmentChecklist.sln` lives):

```powershell
dotnet new maui-blazor -n EquipmentChecklist.Mobile -o EquipmentChecklist.Mobile
dotnet sln add EquipmentChecklist.Mobile/EquipmentChecklist.Mobile.csproj
```

That gives you a working Blazor Hybrid scaffold targeting Android + iOS + Windows + Mac Catalyst.

### 3 · Reference the existing project for shared DTOs

Pick **one** of the two approaches:

**A · Quick (project reference, recommended for now)**

```powershell
dotnet add EquipmentChecklist.Mobile/EquipmentChecklist.Mobile.csproj reference EquipmentChecklist/EquipmentChecklist.csproj
```

Pros: zero ceremony, both apps see the same DTO types.
Con: the mobile build pulls in EF Core + Postgres for compile-time only (no runtime hit but a slightly slower build).

**B · Cleaner (extract a Shared library)**

```powershell
dotnet new classlib -n EquipmentChecklist.Shared -o EquipmentChecklist.Shared -f net8.0
dotnet sln add EquipmentChecklist.Shared/EquipmentChecklist.Shared.csproj
# Move only the Sync* DTOs (and enums they reference: Shift, ChecklistStatus, ItemStatus)
# from EquipmentChecklist/DTOs/DTOs.cs into EquipmentChecklist.Shared/SyncDtos.cs
dotnet add EquipmentChecklist/EquipmentChecklist.csproj reference EquipmentChecklist.Shared/EquipmentChecklist.Shared.csproj
dotnet add EquipmentChecklist.Mobile/EquipmentChecklist.Mobile.csproj reference EquipmentChecklist.Shared/EquipmentChecklist.Shared.csproj
```

### 4 · Add the runtime packages the mobile client needs

```powershell
cd EquipmentChecklist.Mobile
dotnet add package Microsoft.Extensions.Http
dotnet add package CommunityToolkit.Maui
# Local SQLite cache:
dotnet add package sqlite-net-pcl
# Optional but recommended for biometric prompt + secure token storage:
dotnet add package Plugin.Maui.Biometric    # OR: use Microsoft.Maui.Authentication directly
```

### 5 · Drop in the three starter files

Replace the scaffold's defaults with the files below. They wire up:

- `ApiClient` — talks to your `/api/sync` endpoints, stamps the JWT bearer.
- `AuthService` — handles login + secure JWT storage via `SecureStorage`.
- `LoginPage.razor` — minimal UI to call the API and store the token.

#### `EquipmentChecklist.Mobile/Services/ApiClient.cs`

```csharp
using System.Net.Http.Headers;
using System.Net.Http.Json;
using EquipmentChecklist.DTOs;  // or EquipmentChecklist.Shared if you split

namespace EquipmentChecklist.Mobile.Services;

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly AuthService _auth;

    public ApiClient(HttpClient http, AuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    private async Task Authed()
    {
        var token = await _auth.GetTokenAsync();
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(token) ? null : new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<SyncLoginResponse?> LoginAsync(string email, string password)
    {
        var resp = await _http.PostAsJsonAsync("api/sync/login",
            new SyncLoginRequest { Email = email, Password = password });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SyncLoginResponse>();
    }

    public async Task<List<SyncMachineSummaryDto>?> MyMachinesAsync()
    {
        await Authed();
        return await _http.GetFromJsonAsync<List<SyncMachineSummaryDto>>("api/sync/machines");
    }

    public async Task<SyncTemplateDto?> TemplateAsync(int machineId)
    {
        await Authed();
        return await _http.GetFromJsonAsync<SyncTemplateDto>($"api/sync/machines/{machineId}/template");
    }

    public async Task<SyncSubmissionResponse?> SubmitAsync(SyncSubmissionRequest req)
    {
        await Authed();
        var resp = await _http.PostAsJsonAsync("api/sync/submissions", req);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SyncSubmissionResponse>();
    }

    public async Task<List<RecentSubmissionDto>?> RecentSubmissionsAsync()
    {
        await Authed();
        return await _http.GetFromJsonAsync<List<RecentSubmissionDto>>("api/sync/submissions/recent");
    }
}
```

#### `EquipmentChecklist.Mobile/Services/AuthService.cs`

```csharp
using EquipmentChecklist.DTOs;

namespace EquipmentChecklist.Mobile.Services;

public class AuthService
{
    private const string TOKEN_KEY = "eq_jwt";
    private const string EXP_KEY   = "eq_jwt_exp";
    private const string USER_KEY  = "eq_user_json";

    public SyncUserDto? CurrentUser { get; private set; }

    public async Task<string?> GetTokenAsync()
    {
        var token = await SecureStorage.Default.GetAsync(TOKEN_KEY);
        var expS  = await SecureStorage.Default.GetAsync(EXP_KEY);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expS)) return null;
        if (long.TryParse(expS, out var unix) && unix < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            await SignOutAsync();
            return null;
        }
        return token;
    }

    public async Task SignInAsync(SyncLoginResponse login)
    {
        await SecureStorage.Default.SetAsync(TOKEN_KEY, login.Token);
        await SecureStorage.Default.SetAsync(EXP_KEY,   new DateTimeOffset(login.ExpiresAt).ToUnixTimeSeconds().ToString());
        await SecureStorage.Default.SetAsync(USER_KEY,  System.Text.Json.JsonSerializer.Serialize(login.User));
        CurrentUser = login.User;
    }

    public async Task SignOutAsync()
    {
        SecureStorage.Default.RemoveAll();
        CurrentUser = null;
        await Task.CompletedTask;
    }
}
```

#### `EquipmentChecklist.Mobile/MauiProgram.cs` — DI tweaks

Inside `CreateMauiApp()` add:

```csharp
const string ApiBase = "https://YOUR-SERVER-OR-MINE-LAN-IP:5001/";  // ← change me

builder.Services.AddSingleton<AuthService>();
builder.Services.AddHttpClient<ApiClient>(c => c.BaseAddress = new Uri(ApiBase));
```

#### `EquipmentChecklist.Mobile/Components/Pages/Login.razor`

```razor
@page "/"
@inject ApiClient Api
@inject AuthService Auth
@inject NavigationManager Nav

<div style="padding:24px;max-width:380px;margin:0 auto">
    <h2>Equipment Checklist</h2>
    <p style="color:#888">Sign in to start your shift.</p>

    <label>Email</label>
    <input @bind="email" type="email" style="width:100%;padding:10px;margin-bottom:12px" />

    <label>Password</label>
    <input @bind="password" type="password" style="width:100%;padding:10px;margin-bottom:16px" />

    <button @onclick="DoLogin" disabled="@busy"
            style="width:100%;padding:12px;background:#1f3a93;color:#fff;border:none;border-radius:6px">
        @(busy ? "Signing in…" : "Sign In")
    </button>

    @if (!string.IsNullOrEmpty(error))
    {
        <p style="color:#c0392b;margin-top:10px">@error</p>
    }
</div>

@code {
    private string email = "";
    private string password = "";
    private bool   busy;
    private string error = "";

    protected override async Task OnInitializedAsync()
    {
        var token = await Auth.GetTokenAsync();
        if (!string.IsNullOrEmpty(token)) Nav.NavigateTo("/dashboard");
    }

    private async Task DoLogin()
    {
        busy = true; error = "";
        try
        {
            var reply = await Api.LoginAsync(email.Trim(), password);
            if (reply == null) { error = "Invalid email or password."; return; }
            await Auth.SignInAsync(reply);
            Nav.NavigateTo("/dashboard");
        }
        catch (Exception ex) { error = ex.Message; }
        finally { busy = false; }
    }
}
```

### 6 · Run it

```powershell
# Android (emulator must be running)
dotnet build EquipmentChecklist.Mobile -t:Run -f net8.0-android

# Windows
dotnet build EquipmentChecklist.Mobile -t:Run -f net8.0-windows10.0.19041.0
```

You should see the login page, be able to authenticate against your running ASP.NET server, and land on `/dashboard` (which doesn't exist yet — that's Phase 2).

## Phase 2 preview — what's next

Once the login flow works on at least one platform, the next slice is:

1. `Dashboard.razor` — calls `Api.MyMachinesAsync()`, shows assigned machines as cards.
2. `Checklist.razor` — calls `Api.TemplateAsync(machineId)`, renders the items, captures shift / KM / signature, posts to `Api.SubmitAsync(...)`.
3. Local SQLite cache (sqlite-net-pcl) for offline browsing of the last-fetched template.
4. Offline queue: store unsent `SyncSubmissionRequest` rows; flush on connectivity restored.
5. Biometric login: pop the platform's biometric prompt before reading the JWT from `SecureStorage`.

Each of those slices is a follow-up turn — the server API doesn't need any more work for the MVP.

## Backend deployment notes

- The mobile client needs **HTTPS** in production (Android blocks plain HTTP outbound by default; iOS too).
- For the mine LAN: get a real cert (Let's Encrypt with DNS challenge against a public domain pointing at the internal IP works well).
- The JWT lifetime is 30 days — tweak the `expires` in `SyncController.IssueJwt` if you want shorter.

## Why a runbook instead of generated files?

`dotnet new maui-blazor` requires the MAUI SDK workload installed on the machine running it. That workload includes platform-specific binaries (Android SDK, Xcode bridge, etc.) that can't ship from this chat. Running `dotnet new` locally gives you the freshest project template against your local SDK + workload version — much safer than me writing the .csproj from memory and hoping the IDs match your installed components.
