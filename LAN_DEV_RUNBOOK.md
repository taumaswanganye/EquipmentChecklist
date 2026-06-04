# Running the app + API locally with a physical phone on the same Wi-Fi

This is the end-to-end checklist for running the EquipmentChecklist server on
your dev PC and connecting the MAUI mobile app from a physical Android phone
over Wi-Fi (no emulator, no cloud).

Three pieces have to line up:

1. The API (Kestrel) has to listen on a network interface the phone can
   reach — not just `localhost`.
2. Windows Firewall has to let the phone through on port `55025`.
3. The MAUI app's `ApiBaseUrl` has to point at your PC's LAN IP.

Once those three things are set, the phone connects exactly like any other
device.

---

## 1. Find your dev PC's LAN IP

Open a PowerShell terminal and run:

```powershell
ipconfig
```

Look for the Wi-Fi adapter section (something like **Wireless LAN adapter
Wi-Fi**) and read the `IPv4 Address` line. It will look like one of these,
depending on your router:

```
192.168.1.42
192.168.0.18
10.0.0.34
```

Write that number down — call it `<DEV_PC_LAN_IP>` for the rest of this doc.

Make sure your **phone is on the same Wi-Fi network** as your PC. Mobile
data won't work — the phone has to be on the same subnet to reach a private
`192.168.x.x` or `10.0.0.x` address.

---

## 2. Open Windows Firewall for port 55025

By default Windows blocks incoming connections on dev ports. Open an
elevated PowerShell (right-click → Run as Administrator) and run:

```powershell
New-NetFirewallRule `
    -DisplayName "EquipmentChecklist Dev API (55025-55026)" `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort 55025,55026 `
    -Action Allow `
    -Profile Private
```

`-Profile Private` restricts the rule to your home/work network (the one
Windows classifies as "private"), so you're not exposing the dev API on
coffee-shop Wi-Fi.

To remove the rule later:

```powershell
Remove-NetFirewallRule -DisplayName "EquipmentChecklist Dev API (55025-55026)"
```

---

## 3. Launch the API with the LAN profile

I added a new launch profile called **EquipmentChecklist (LAN)** to
`Properties/launchSettings.json` that binds Kestrel to `0.0.0.0` (all
interfaces) instead of `localhost`. Use it from Visual Studio's run-target
dropdown next to the green play button, or from the CLI:

```powershell
cd C:\Users\tauma\source\repos\Equip\EquipmentChecklist
dotnet run --launch-profile "EquipmentChecklist (LAN)"
```

You should see two lines in the console:

```
Now listening on: https://0.0.0.0:55025
Now listening on: http://0.0.0.0:55026
```

If Visual Studio's "EquipmentChecklist" profile (the original `localhost`
one) is still selected, the phone won't be able to reach it — switch to
**EquipmentChecklist (LAN)** specifically.

### Smoke-test from the phone's browser

Open Chrome on the phone and go to:

```
http://<DEV_PC_LAN_IP>:55026
```

(Note: plain HTTP, port `55026` — NOT https. I made the HTTPS redirect
skip in Development specifically so this works.)

You should see the EquipmentChecklist login page. If you don't:

| Symptom                       | Likely cause                                  |
|-------------------------------|-----------------------------------------------|
| `ERR_CONNECTION_REFUSED`      | Kestrel isn't bound to `0.0.0.0` — wrong launch profile |
| `ERR_CONNECTION_TIMED_OUT`    | Windows Firewall blocking — re-run section 2  |
| `ERR_ADDRESS_UNREACHABLE`     | Phone is on a different subnet (mobile data?) |
| Site loads but app can't sign in | DEV cert not trusted on phone — that's fine, see step 4 |

---

## 4. Point the MAUI app at your LAN IP

Open `EquipmentChecklist.Mobile/MauiProgram.cs` and find the constant near
the top of `CreateMauiApp`:

```csharp
const string DevHostIp = "10.0.2.2";   // ← e.g. "192.168.1.42" for physical phone
```

Replace `"10.0.2.2"` with `"<DEV_PC_LAN_IP>"` — your number from step 1.
For example:

```csharp
const string DevHostIp = "192.168.1.42";
```

That's the only line you change. The `#if ANDROID` block automatically
builds `https://192.168.1.42:55025/` as the API base URL. Windows builds
keep using `localhost`, unaffected.

### About the HTTPS cert

The ASP.NET Core dev cert is self-signed and your phone doesn't trust it.
The MAUI app already handles this in **DEBUG** builds — there's a
`ServerCertificateCustomValidationCallback` in `MauiProgram.cs` that
accepts any cert when the build is DEBUG + ANDROID. So you don't need to
install the dev cert on the phone for development.

For Release builds (App Center, Play Store) you'd need a properly trusted
cert on the API — Let's Encrypt or whatever your IT issues.

---

## 5. Deploy to the phone

### Option A: USB cable + Visual Studio

1. Phone → Settings → About phone → tap **Build number** 7 times until
   "developer mode enabled".
2. Phone → Settings → Developer options → **USB debugging** on.
3. Plug phone into PC. First time, the phone shows an "Allow USB
   debugging?" dialog — tap **Always allow from this computer** → OK.
4. In Visual Studio, the device dropdown next to the play button shows
   your phone model. Pick it.
5. Hit F5. The build takes ~2 min the first time, ~30s after that.

### Option B: Wireless ADB (Android 11+)

Once you've cabled in once, you can switch to wireless:

```powershell
# With phone still plugged in:
adb tcpip 5555
# Now unplug. Find phone's IP in Settings → About phone → Status.
adb connect <PHONE_LAN_IP>:5555
```

Visual Studio sees it as a normal device.

### Option C: Install the APK manually

```powershell
cd C:\Users\tauma\source\repos\Equip\EquipmentChecklist.Mobile
dotnet publish -f net9.0-android -c Debug
# Look in bin\Debug\net9.0-android\ for the .apk
# Copy it to the phone (USB, email, Drive). Tap to install.
# You'll need to enable "Install unknown apps" for the source app.
```

---

## 6. Sign in from the phone

Default seeded admin credentials (from `Program.cs SeedRolesAndAdminAsync`):

```
Email:    admin@belfast.co.za
Password: Admin@123
```

The first successful sign-in caches the user's PBKDF2 password hash in
SQLite, so the next time the phone is offline they can still sign in with
the same password.

---

## Troubleshooting

### "Can't reach the server" but the browser smoke-test worked

The browser test uses HTTP (`55026`); the MAUI app uses HTTPS (`55025`).
The HTTPS port is its own listener — verify Kestrel logged both URLs at
startup. If only the HTTP one shows, the LAN launch profile isn't active.

### Phone shows "Online" pill but every API call fails with 401

JWT expired. Sign out and back in. The `BumpLastSyncedAsync` and token
refresh logic only runs after a successful API call, so an expired token
can persist across app restarts.

### Each sign-in works but loses connection after a few seconds

Windows put the network adapter into power-saving / sleep. Open
**Device Manager → Network adapters → your Wi-Fi → Properties → Power
Management** and uncheck "Allow the computer to turn off this device".

### IP address changes every time the PC reboots

Your router is handing out DHCP leases that rotate. Two fixes:

- **Easiest**: set a static IP reservation in the router admin page for
  the PC's MAC address. The PC keeps the same IP forever.
- **Code change**: implement an mDNS / Bonjour discovery in the MAUI app so
  it auto-resolves a friendly name like `belfastdev.local`. Not in scope
  for this runbook.

### Multiple devs working on the same codebase

Each dev has a different LAN IP, but everyone shares the same
`MauiProgram.cs`. Two patterns:

1. **gitignore-driven local override**: create
   `EquipmentChecklist.Mobile/Properties/launchSettings.local.json` and
   add it to `.gitignore`. Read its content at startup. Slight refactor.
2. **Quick-n-dirty**: each dev keeps a local stash of their IP change —
   `git stash save "lan ip"` before pushing, `git stash pop` after pulling.

Pattern (1) is the proper fix when you have a team; (2) is fine for one
person hopping between home/office Wi-Fi.

### Phone needs to reach API from outside the LAN

Out of scope for dev. Options if you want to demo from outside:

- **ngrok** (`ngrok http https://localhost:55025`) — exposes a temporary
  public URL with a trusted cert. Free tier rotates URLs.
- **Cloudflare Tunnel** — same idea, free, persistent URL if you have a
  domain.
- **Proper deploy** to a staging server with a real cert.

---

## Files changed by this runbook

| File | Change |
|------|--------|
| `EquipmentChecklist/Properties/launchSettings.json`           | Added `EquipmentChecklist (LAN)` profile binding to `0.0.0.0` |
| `EquipmentChecklist/Program.cs`                               | `UseHttpsRedirection()` now skipped in Development            |
| `EquipmentChecklist.Mobile/MauiProgram.cs`                    | Replaced inline `10.0.2.2` with a single `DevHostIp` constant |
| `LAN_DEV_RUNBOOK.md`                                          | This file                                                     |

When this runbook is no longer needed, the only line you typically have to
revert is `DevHostIp` back to `"10.0.2.2"` so the emulator works again.
