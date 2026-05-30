# Step 4 — pre-written files

These four files are ready to go. They live where the MAUI scaffold would
normally place them, so once you run

```powershell
dotnet new maui-blazor -n EquipmentChecklist.Mobile -o EquipmentChecklist.Mobile
```

from the **EquipmentChecklist** folder, the scaffold's defaults will sit
side-by-side with my pre-written files. Then you just:

1. **Keep my files as-is** for `Services/ApiClient.cs`, `Services/AuthService.cs`,
   `Components/Pages/Login.razor`, `MauiProgram.cs`.
2. **Overwrite the scaffold's `MauiProgram.cs`** with mine if `dotnet new` regenerates it on top.
3. **Delete the scaffold's `Components/Pages/Home.razor`** (or rename it) — `Login.razor`
   takes over the `@page "/"` route.

## File inventory

| File | What it does |
|---|---|
| `Services/ApiClient.cs` | Thin wrapper over `/api/sync/*` on the server. One method per endpoint, all token-stamped. |
| `Services/AuthService.cs` | Stores the JWT + user profile in `SecureStorage` (Keystore / Keychain / DPAPI). Exposes `RestoreAsync()`, `SignInAsync()`, `SignOutAsync()`. Auto-clears expired tokens. |
| `Components/Pages/Login.razor` | The login page. Calls `Api.LoginAsync(...)`, stashes the token, redirects to `/dashboard`. Pre-styled with the brand colors. |
| `MauiProgram.cs` | DI: wires `AuthService` (singleton) + `ApiClient` (typed `HttpClient`). Includes DEV-ONLY cert bypass for the Android emulator so `https://10.0.2.2:5001/` works against your local dev cert. |

## Things you have to change before first run

### `MauiProgram.cs` → the `ApiBase` constant

Look at the top of `MauiProgram.cs`:

```csharp
public const string ApiBase = "https://10.0.2.2:5001/";
```

- Running on **Windows desktop**? Change to `https://localhost:5001/`.
- Running on **Android emulator**? Leave as-is (`10.0.2.2` is the emulator's loopback to the host PC).
- Running on a **physical phone** over Wi-Fi? Use your laptop's LAN IP, e.g. `https://192.168.1.42:5001/`. Run `ipconfig` to find it.
- Once you deploy to the mine LAN or cloud, set it to the real URL.

### Remove the DEV cert bypass before shipping

The `#if DEBUG` block at the bottom of `MauiProgram.cs` accepts any HTTPS cert
on Android — that's so the ASP.NET dev cert and self-signed Android emulator
cert don't break local testing. **Delete that block** when you build for
production, or your app will accept man-in-the-middle certs.

## Smoke-testing the API before installing the MAUI workload

If you'd rather check the server endpoints work *first*, here's a one-liner:

```powershell
curl.exe -k -X POST https://localhost:5001/api/sync/login `
  -H "Content-Type: application/json" `
  -d '{\"email\":\"admin@belfast.co.za\",\"password\":\"YOUR_PASSWORD\"}'
```

A `200` with a JSON body containing `token` means the API is good and the
mobile project is the only thing left to scaffold.

## If `dotnet new maui-blazor` puts files in different paths

Newer scaffolds vary slightly. If yours uses `Pages/` instead of
`Components/Pages/`, move `Login.razor` to match, and update the
`@using EquipmentChecklist.Mobile.Services` line at the top — it'll auto-pick
up `Services/*` from the project root regardless.
