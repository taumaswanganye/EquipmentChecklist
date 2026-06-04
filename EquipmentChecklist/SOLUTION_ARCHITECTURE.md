# Equipment Pre-Use Checklist — Solution Architecture

**Document owner:** DevOps / IT Lead
**Target audience:** Technical (DevOps, Platform Engineering, Security)
**System:** Equipment Pre-Use Checklist (Enaleni Engineering — multi-mine deployment)
**Last reviewed:** 2026-06-04

---

## 1. Executive summary

The Equipment Pre-Use Checklist is an offline-first inspection and workflow system for mining operations. Operators run a pre-shift checklist against assigned machinery, supervisors sign off on partial passes ("GO-BUT"), and mechanics own the repair queue for failures. The system runs primarily underground where signal is intermittent, so every role works fully offline and reconciles when connectivity returns.

Three surfaces share one backend:

- **Web (ASP.NET Core MVC, dark theme):** the desk-bound surface for admins, supervisors, and mine managers — reports, dashboards, audit trail, user management.
- **Mobile (MAUI Blazor Hybrid):** the underground surface — Windows and Android targets. Same Razor markup as the web for the few shared pages.
- **API (ASP.NET Core Web API):** the sync surface — JWT-authenticated REST + SignalR, consumed by the mobile app.

All three are deployed as a single ASP.NET Core process backed by PostgreSQL.

Compliance posture: MHSA / DMR / CPS Level 8/9. Append-only audit trail, signature capture on every state transition, defect lifecycle that mirrors the regulatory paper form.

---

## 2. Technology stack

### Server (web + API)

| Layer | Choice | Version | Why |
|---|---|---|---|
| Runtime | .NET | 8.0 (LTS) | Long-term support until Nov 2026. Stable, performant, mature MVC + EF Core. |
| Web framework | ASP.NET Core MVC | 8.0 | Server-rendered Razor for management UIs. Pairs naturally with Web API for the mobile sync surface. |
| ORM | Entity Framework Core | 8.0 | Code-first migrations, LINQ-to-SQL, Npgsql provider for Postgres. |
| Auth (web) | ASP.NET Core Identity | 8.0 | Cookie auth, role-based authorisation, password hashing. |
| Auth (API) | JWT Bearer | 8.0 | Stateless tokens for the mobile app. 14-day default lifetime, refresh via re-login. |
| Auth (biometric) | Fido2.AspNet + Fido2 | 3.0.1 | WebAuthn — fingerprint / Windows Hello / Android biometric. |
| Real-time | SignalR | 8.0 | Push notifications to web and mobile. JWT auth via query-string fallback for WebSocket upgrade. |
| PDF generation | QuestPDF | 2024.3.4 | Fluent server-side PDF layout. Used for checklists, parts orders, exports. |
| PDF parsing | PdfPig | 0.1.9 | Reads operator-uploaded PDF templates during admin onboarding. |
| Excel export | ClosedXML | 0.105.0 | Reports → Excel for procurement / external audit. |

### Mobile (MAUI Blazor Hybrid)

| Layer | Choice | Version | Why |
|---|---|---|---|
| Runtime | .NET | 9.0 | MAUI on .NET 9 has the stability fixes for WebView2 and Android Blazor that 8 lacked. |
| Framework | .NET MAUI Blazor Hybrid | 9.0 | Single Razor codebase running natively on Android + Windows. WebView host with C#-side services. |
| Targets | `net9.0-android`, `net9.0-windows10.0.19041.0` | — | Android 23+ (Marshmallow), Windows 10 17763+. |
| Local DB | sqlite-net-pcl | 1.9.172 | Embedded SQLite for cache + offline queues. No EF Core on mobile — sqlite-net is lighter for the simple table shapes we use. |
| HTTP | `HttpClient` via `IHttpClientFactory` | net9 | DI'd into ApiClient; per-platform base URL (10.0.2.2 for emulator, LAN IP for physical Android). |
| SignalR | Microsoft.AspNetCore.SignalR.Client | 8.0.0 | Receives push notifications. Auto-reconnect on SignedIn event. |
| Image processing | SkiaSharp | 3.116.1 | Resize defect photos to 1280×960 @ 80% JPEG before upload. Cuts cellular bandwidth by ~10×. |
| Audio capture | Plugin.Maui.Audio | 4.0.0 | Cross-platform voice memo recorder. Outputs M4A on iOS/Android, WAV on Windows. |
| Biometric (Android) | Xamarin.AndroidX.Biometric | 1.1.0.21 | Fingerprint unlock via `BiometricPrompt`. Bridges native callback API to TaskCompletionSource. |
| On-device PDF | jsPDF + jspdf-autotable | 2.5.1 + 3.8.2 | Generates checklist PDFs in the WebView when offline. Cached locally until the server's QuestPDF render is available. |

### Shared

A small `EquipmentChecklist.Shared` class library multi-targets `netstandard2.1;net8.0;net9.0` and carries DTOs + enums consumed by both projects. Lives in its own assembly so the mobile build graph doesn't drag the Web SDK in.

### Data layer

| Storage | Where | Why |
|---|---|---|
| PostgreSQL 14+ | Server (managed or self-hosted) | Single source of truth. Submissions, defects, users, audit, notifications, machine roster. |
| SQLite (mobile) | Inside MAUI app's data directory | Per-device cache (`eqcache.db`) — submission queue, action queue, audit queue, machine roster snapshot, last-synced timestamps. |
| IndexedDB (web PWA) | Browser | Service Worker queue for offline POSTs (`eq_offline` DB). Mirrors what the MAUI app does, but for browsers. |

---

## 3. Component map

```
┌──────────────────────────────────────────────────────────────────────────┐
│                          PostgreSQL (primary)                            │
│  Submissions · SubmissionItems · DefectOrders · Machines · Users         │
│  Notifications · AuditEvents · MachineAssignments · ChecklistTemplates   │
└──────────────────────────▲───────────────────────────────────────────────┘
                           │ EF Core 8 / Npgsql
                           │
┌──────────────────────────┴───────────────────────────────────────────────┐
│              ASP.NET Core 8.0 process (single deployable)                 │
│                                                                          │
│   ┌───────────────────┐  ┌────────────────────┐  ┌────────────────────┐  │
│   │  MVC Controllers  │  │  API Controllers   │  │   SignalR Hubs     │  │
│   │  /Admin           │  │  /api/sync/*       │  │   /hubs/notif      │  │
│   │  /Supervisor      │  │  Cookie OR JWT     │  │   JWT via qstring  │  │
│   │  /Mechanic        │  │  on shared routes  │  │                    │  │
│   │  /Checklist       │  │                    │  │                    │  │
│   └─────────┬─────────┘  └─────────┬──────────┘  └─────────┬──────────┘  │
│             │                      │                       │             │
│   ┌─────────┴──────────────────────┴───────────────────────┴──────────┐  │
│   │                          Service layer                            │  │
│   │  ChecklistService · NotificationService · AuditService            │  │
│   │  ReportsService · PdfService · EmailService · MineSettings        │  │
│   │  OfflineVoucherService · FIDO2 setup                              │  │
│   └───────────────────────────────────────────────────────────────────┘  │
└──────────────────────────┬───────────────────────────────────────────────┘
                           │
                           │ HTTPS + WSS
                           │
   ┌───────────────────────┼───────────────────────┐
   │                       │                       │
┌──┴──────────────┐  ┌─────┴───────────┐  ┌────────┴─────────┐
│  Browser (web)  │  │  MAUI Mobile    │  │  MAUI Mobile     │
│  Chrome/Edge    │  │  Android 6+     │  │  Windows 10+     │
│  Service Worker │  │  WebView2 host  │  │  WebView2 host   │
│  IndexedDB      │  │  SQLite cache   │  │  SQLite cache    │
│  Fido2/WebAuth  │  │  AndroidX Bio.  │  │  Hello WebAuthn  │
└─────────────────┘  └─────────────────┘  └──────────────────┘
```

### Cross-cutting concerns

- **Auto-migrate at startup:** `cloudDb.Database.MigrateAsync()` runs in `SeedRolesAndAdminAsync` on every boot. Every migration uses idempotent SQL (`CREATE TABLE IF NOT EXISTS`, `ADD COLUMN IF NOT EXISTS`) so re-running on a populated DB is safe.
- **Multi-mine config:** the `MineSettings` block in `appsettings.json` (`Name`, `ShortName`, `Tagline`, `ComplianceText`) is injected as `IOptions<MineSettings>`. Mobile fetches the same labels from `GET /api/sync/mine` on first boot and caches them. A single binary serves multiple mines via configuration.
- **Auth scheme routing:** `Program.cs` registers a `multi` policy scheme — paths under `/api/*` are routed to JWT, everything else to the Identity cookie. Endpoints that need both (notifications) declare `AuthenticationSchemes = "Bearer,Identity.Application"` explicitly.
- **Append-only audit:** every state-changing action writes one row to `AuditEvents`. `AuditService` swallows its own exceptions AND detaches the failed entity from the change tracker so an audit-write failure can't poison subsequent `SaveChanges` calls on the same scoped DbContext.

---

## 4. Data architecture

### PostgreSQL schema (high level)

Core tables — schema names are PascalCase as EF Core generates them:

- **`AspNetUsers`** + Identity tables — users, roles, role assignments, claims.
- **`Machines`** — fleet roster. `MachineNumber`, `MachineName`, `TypeName`, `IsImmobilised`, `ImmobilisedReason`.
- **`MachineAssignments`** — operator/mechanic ↔ machine many-to-many (`OperatorId`, `MechanicId`, `MachineId`, `IsActive`).
- **`OperatorSupervisorAssignments`** — operator ↔ supervisor reporting line.
- **`ChecklistTemplates`** + **`ChecklistTemplateItems`** — per-machine inspection items. Templates can be uploaded as a structured Excel/PDF and parsed into rows.
- **`ChecklistSubmissions`** — one row per submitted checklist. Carries operator + supervisor signatures, status (Go / GoButRepair24H / GoTillNextService / NoGo / Rejected), shift, KM/hours.
- **`SubmissionItems`** — per-item state on a submission (InOrder / Defect), notes, optional `PhotoData` (bytea) + `AudioData` (bytea) for evidence.
- **`DefectOrders`** — per-defect repair tracking. Mechanic assignment, parts ordered, completion signature.
- **`Notifications`** — cross-role inbox rows. SignalR pushes them to connected clients, REST returns them to clients that just came online.
- **`AuditEvents`** — append-only event log. Actor identity snapshot, action constant, target type+id, jsonb payload, dual timestamps (client + server).
- **`UserCredentials`** — WebAuthn / FIDO2 credentials per user.
- **`IconLibraryItems`** — admin-managed icon set referenced by template items.

### Indexes worth knowing

```sql
-- Bell-badge query (notifications)
CREATE INDEX "IX_Notifications_UserId_CreatedAt"
    ON "Notifications" ("UserId", "CreatedAt" DESC);

-- Audit trail browsing
CREATE INDEX "IX_AuditEvents_ActorUserId_OccurredAtServer"
    ON "AuditEvents" ("ActorUserId", "OccurredAtServer" DESC);
CREATE INDEX "IX_AuditEvents_TargetType_TargetId"
    ON "AuditEvents" ("TargetType", "TargetId");
CREATE INDEX "IX_AuditEvents_OccurredAtServer"
    ON "AuditEvents" ("OccurredAtServer" DESC);
CREATE INDEX "IX_AuditEvents_Action_OccurredAtServer"
    ON "AuditEvents" ("Action", "OccurredAtServer" DESC);
```

### Mobile-side SQLite

Single file `eqcache.db` under the MAUI app's `FileSystem.AppDataDirectory`. Tables:

- **`pending_submissions`** — queued offline submissions, indexed by `LocalId` (Guid) for server-side idempotency.
- **`queued_actions`** — supervisor sign-offs, rejects, mechanic claims, part orders, completions. Generic action-type discriminator.
- **`pending_audit_rows`** — audit events captured before the server is reachable. Ships in batches of 100 via `POST /api/sync/audit`.
- **`cached_blobs`** — generic JSON blob cache keyed by `(user_email, key)`. Used for recent submissions, supervisor queue, mechanic defects, mine config.
- **`cached_pdfs`** + **`cached_local_pdfs`** — PDF byte cache, keyed by `SubmissionId` (server) or `LocalId` (queued).

Bounded retention: queues prune rows older than 60 days (90 for audit) once per hour via `SyncWorker.PruneStaleQueuesAsync`. A toast surfaces the drop so the operator knows their old work didn't ship.

---

## 5. Authentication & authorisation

### Identity model

- **Roles:** `Admin`, `Supervisor`, `Mechanic`, `Operator`. A user can hold multiple roles; the `Admin` role gates everything.
- **Default admin:** seeded on first boot from `appsettings.json` if no user with the seed email exists. Credentials live in `Program.cs` → `SeedRolesAndAdminAsync`.

### Scheme routing

```csharp
builder.Services.AddAuthentication(opt =>
{
    opt.DefaultScheme          = "multi";
    opt.DefaultChallengeScheme = "multi";
})
.AddPolicyScheme("multi", "multi", opt =>
{
    opt.ForwardDefaultSelector = ctx =>
        ctx.Request.Path.StartsWithSegments("/api")
            ? JwtBearerDefaults.AuthenticationScheme
            : IdentityConstants.ApplicationScheme;
});
```

- Cookie scheme on `/Admin`, `/Supervisor`, `/Mechanic`, `/Checklist`, etc.
- JWT bearer on `/api/sync/*`.
- A handful of endpoints accept both — e.g. the notifications API hit from the web's topbar bell. They declare `AuthenticationSchemes = "Bearer,Identity.Application"` directly.

### Biometric / passwordless

WebAuthn via Fido2. RP origin set in `appsettings.json` → `Fido2:ServerDomain` / `Origins`. Operators can enrol a credential on the web (`/Security`) or via the mobile login flow. Offline voucher (RSA-signed JWT) lets a previously-online operator unlock the mobile app without server contact for up to 24h.

### JWT specifics

- Signing key in `appsettings.json` → `Jwt:Key`. **Replace this for every environment.**
- Default lifetime: 14 days. Refresh happens by re-login (no refresh-token endpoint).
- SignalR WebSocket upgrade can't set an Authorization header — the JWT comes through the `access_token` query string on `/hubs/*` paths; `JwtBearerEvents.OnMessageReceived` reads it.

---

## 6. Sync and offline architecture

### Per-role offline behaviour

| Role | What works offline | What requires connectivity |
|---|---|---|
| Operator | Browse assigned machines, run checklist, sign, generate PDF receipt on-device | First-time sign-in (subsequent: biometric or cached creds) |
| Supervisor | Browse cached sign-off queue, approve / reject (queued) | First load of new operators' submissions |
| Mechanic | Browse cached defect queue, claim / order parts / complete (queued) | First load of new defects |
| Admin | None — admin is a desk role on the web | All |

### Sync triggers (`SyncWorker.DrainAsync`)

Three event sources fire the drainer:

1. `Connectivity.ConnectivityChanged` — device leaves a dead zone.
2. `ApiHealth.Changed` (positive flip) — server came back without device touching the network. ApiHealth polls `/ping` every 20 s.
3. `AuthService.SignedIn` — user signed in after the app booted.

The drain runs in priority order: **submissions → actions → audit**. Bounded retention runs first (throttled to hourly). After actions, if any row came back as 4xx (`Permanent`), the drainer calls `NotificationService.RefreshUnreadCountAsync()` so the conflict-rejected notification surfaces immediately.

### Conflict resolution

Two supervisors approve the same submission offline. The first one wins; the second's drain hits 409. `ChecklistService.ConflictException` carries the winner's name and machine number. The API writes a `ConflictRejected` notification for the loser before returning 409. The mobile drainer drops the row + refreshes the bell. The losing supervisor opens their inbox and sees "Sign-off rejected — GRD-007: This submission was already signed off by Sipho Khumalo." No silent overwrite.

### Stale-data banner

If the device hasn't seen a successful ping in 20 days, the layout shows a banner asking the operator to reconnect. After 60 days, queued rows are pruned with a warning toast.

---

## 7. Deployment architecture

### Recommended topology (production)

```
                            ┌─────────────────┐
                            │   CloudFront /  │   ← HTTPS termination,
                            │   Cloudflare    │     WAF, DDoS protection
                            └────────┬────────┘
                                     │
                            ┌────────┴────────┐
                            │  Load balancer  │   ← Health probes /ping
                            │  (single AZ ok) │
                            └────────┬────────┘
                                     │
                ┌────────────────────┴────────────────────┐
                │                                         │
        ┌───────┴────────┐                        ┌───────┴────────┐
        │  App Server 1  │                        │  App Server 2  │
        │  Kestrel :8080 │                        │  Kestrel :8080 │
        │  Linux Docker  │                        │  Linux Docker  │
        └───────┬────────┘                        └───────┬────────┘
                │                                         │
                └───────────────────┬─────────────────────┘
                                    │
                       ┌────────────┴───────────┐
                       │   PostgreSQL 14+ HA    │
                       │   Primary + replica    │
                       │   Daily backups → S3   │
                       └────────────────────────┘
```

For a single-mine deployment a single app server + one Postgres instance is sufficient. The system has been load-tested at ~200 concurrent operators on a 2 vCPU / 4 GB host.

### Deployment options compared

| Option | Best for | Trade-offs |
|---|---|---|
| **On-prem VM** (Ubuntu + Postgres + nginx) | Mines with strict data-residency rules; air-gapped facilities | Manual cert renewal, you own patching |
| **Azure App Service + Azure Database for PostgreSQL** | Lift-and-shift, SA region available, fast spin-up | Higher steady-state cost than VM; locks you in modestly |
| **AWS Elastic Beanstalk + RDS** | Mixed-platform IT teams | Beanstalk metadata can drift between deploys; needs CDK or Terraform for repeatability |
| **Docker on Hetzner / DO + managed Postgres** | Small mines, tight budget | Bring your own monitoring + log aggregation |
| **Kubernetes (AKS / EKS / k3s)** | 3+ mine federation, central IT | Operational overhead overshoots the value below ~5 mines |

For a typical single mine, **Linux Docker on a 4 vCPU / 8 GB VM with a managed Postgres** is the lowest-friction production posture.

### Mobile distribution

- **Android:** sideload the signed APK via MDM (Microsoft Intune, VMware Workspace ONE). The app's package id is `com.companyname.equipmentchecklist.mobile` — change to your real reverse-DNS before first deploy because Android treats a renamed package as a new app and loses cached data.
- **Windows:** MSIX package for laptops; or unpackaged `Pre-Checklist.exe` for kiosks. Install via SCCM, MEM, or a network share.
- **iOS:** not currently in the build matrix. Adding `net9.0-ios` to `TargetFrameworks` is a 1-day exercise; the bigger work is Apple Developer Enterprise enrolment for in-house distribution.

---

## 8. Step-by-step deployment guide (Linux Docker, single mine)

### 8.1 Prerequisites

- Ubuntu 22.04 LTS (or any Docker-capable Linux), 4 vCPU / 8 GB RAM, 50 GB disk
- Docker Engine 24+
- `docker-compose` plugin
- DNS A-record pointing at the host (e.g. `checklist.your-mine.co.za`)
- Let's Encrypt account (or your CA of choice) for TLS

### 8.2 Generate production secrets

```bash
# JWT signing key — 256 bits of randomness, base64-encoded
openssl rand -base64 32

# Postgres password — store in your vault, never commit
openssl rand -base64 24
```

### 8.3 Create the project layout

```bash
mkdir -p /srv/checklist/{data/postgres,data/wwwroot-uploads,certs,logs}
cd /srv/checklist
```

### 8.4 `docker-compose.yml`

```yaml
version: "3.9"

services:
  db:
    image: postgres:14
    restart: unless-stopped
    environment:
      POSTGRES_DB: digital_checklist
      POSTGRES_USER: checklist
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    volumes:
      - ./data/postgres:/var/lib/postgresql/data
    networks: [checklist-net]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U checklist"]
      interval: 10s
      timeout: 5s
      retries: 5

  app:
    image: enaleni/equipment-checklist:latest   # built by CI from the EquipmentChecklist project
    restart: unless-stopped
    depends_on:
      db:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__PostgreSQL: "Host=db;Database=digital_checklist;Username=checklist;Password=${POSTGRES_PASSWORD}"
      Jwt__Key: ${JWT_KEY}
      Mine__Name: "Belfast Coal Mine"
      Mine__ShortName: "BELFAST"
      Mine__Tagline: "Pre-Shift Inspection Checklist"
      Mine__ComplianceText: "MHSA / DMR / CPS Level 8/9 Compliant"
      Email__SmtpHost: "smtp.your-provider.co.za"
      Email__SmtpPort: "587"
      Email__Username: ${SMTP_USER}
      Email__Password: ${SMTP_PASS}
      Email__From: "no-reply@your-mine.co.za"
      Email__AdminEmail: "ops@your-mine.co.za"
      Email__ManagerEmail: "maintenance@your-mine.co.za"
      Fido2__ServerDomain: "checklist.your-mine.co.za"
      Fido2__Origins__0: "https://checklist.your-mine.co.za"
    volumes:
      - ./data/wwwroot-uploads:/app/wwwroot/item-images
      - ./logs:/app/logs
    networks: [checklist-net]

  caddy:
    image: caddy:2
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - ./certs:/data
    networks: [checklist-net]

networks:
  checklist-net:
```

### 8.5 `Caddyfile` (auto-TLS via Let's Encrypt)

```
checklist.your-mine.co.za {
    reverse_proxy app:8080

    # SignalR + WebSocket support
    @websockets {
        header Connection *Upgrade*
        header Upgrade websocket
    }
    reverse_proxy @websockets app:8080

    encode gzip
}
```

### 8.6 `.env` file (secrets — never commit)

```ini
POSTGRES_PASSWORD=<paste-from-openssl>
JWT_KEY=<paste-from-openssl>
SMTP_USER=apikey-or-username
SMTP_PASS=<the-smtp-secret>
```

### 8.7 First boot

```bash
docker compose pull
docker compose up -d

# Watch the migration run
docker compose logs -f app | grep -i migrat
```

You should see lines like:

```
Applying migration '20240312074136_InitialCreate'.
Applying migration '20260601000000_AddUserCredentials'.
Applying migration '20260604000000_AddAuditEvents'.
```

Then visit `https://checklist.your-mine.co.za` and sign in with the seeded admin (default email/password in `Program.cs` — **change the password immediately on first login**).

### 8.8 Post-install checklist

- [ ] Change the default admin password.
- [ ] Create role accounts for at least one supervisor, one mechanic, and a test operator.
- [ ] Upload one machine + template to confirm the inspection flow.
- [ ] On the mobile build, set `ApiBaseUrl` in `MauiProgram.cs` to `https://checklist.your-mine.co.za/` and rebuild.
- [ ] Verify the SignalR hub: open the web with DevTools → Network → WS — you should see a connection to `/hubs/notifications`.
- [ ] Take one test inspection through the full GO-BUT → supervisor sign-off → audit-trail loop. Confirm the audit row appears in `/Admin/Audit`.

---

## 9. Operations runbook

### Backups

```bash
# Nightly dump — run via cron at 02:00 local time
docker compose exec -T db pg_dump -U checklist digital_checklist | \
    gzip > /srv/checklist/backups/$(date +%Y-%m-%d).sql.gz

# Retention: keep 30 days local, 1 year off-site (S3 / Backblaze)
find /srv/checklist/backups -name '*.sql.gz' -mtime +30 -delete
aws s3 sync /srv/checklist/backups s3://your-mine-checklist-backups/
```

### Monitoring (minimum viable)

- **Application logs:** `docker compose logs -f app` or ship to Seq / Datadog via `Logging:Console` provider.
- **Liveness probe:** `GET /ping` returns 200 OK. Hook to Uptime Robot / Better Stack.
- **Disk usage:** the `wwwroot-uploads` volume grows with operator-uploaded item images. Alert at 80% capacity.
- **Postgres metrics:** if using a managed service, enable Performance Insights (RDS) or Query Performance (Azure). Self-hosted: Prometheus `postgres_exporter`.

### Scaling levers

| Symptom | First lever |
|---|---|
| Sign-off queue slow | Add an index on `ChecklistSubmissions(Status, SupervisorId, SubmittedAt)` |
| Reports page slow | Lower `Take(5000)` window in `AdminController.Audit` / `Reports`, or move to SQL-side paging |
| Notifications laggy | Confirm WebSocket upgrade works (check Caddyfile); fall back to 30s polling otherwise |
| App container CPU > 70% steady | Scale to 2 replicas behind the load balancer. Postgres handles the load. |
| Postgres CPU > 70% | First check `pg_stat_statements` for missing indexes; vertical-scale RAM before splitting reads |

### Upgrades

```bash
# Pull new image and restart — auto-migrate runs on the new container's boot
docker compose pull app
docker compose up -d app
docker compose logs -f app
```

Roll back is a `docker compose pull` of the previous tag. EF migrations are additive — older app code tolerates a DB schema ahead of it.

---

## 10. Compliance and audit posture

### Mining-specific requirements addressed

| Requirement | Implementation |
|---|---|
| MHSA Section 11 (workplace records) | Every checklist becomes a PDF receipt with operator signature, retained indefinitely in Postgres. |
| MHSA Section 17 (employee participation) | Operators have an in-app sign-off + fitness declaration before any submission. |
| Append-only event log | `AuditEvents` table is never UPDATEd or DELETEd by application code. Database role for the app has INSERT-only privileges on the table — recommend revoking UPDATE/DELETE on that table specifically. |
| Time-of-event accuracy | Dual timestamps on every audit row: `OccurredAtClient` (device clock, advisory on mobile) and `OccurredAtServer` (authoritative). |
| Digital signatures | Captured as base64 PNG data URLs at the moment of consent. Stored inline on the related row, embedded in the PDF. |
| Defect chain of custody | DefectOrder rows track every transition (creation → claim → part order → completion) with mechanic identity at each step. |

### Suggested database hardening

```sql
-- Application user has full DML on workflow tables
GRANT INSERT, UPDATE, DELETE, SELECT ON "ChecklistSubmissions" TO checklist;

-- ...but ONLY insert on the audit log
REVOKE UPDATE, DELETE ON "AuditEvents" FROM checklist;
GRANT  INSERT, SELECT ON "AuditEvents" TO checklist;

-- Separate read-only role for reporting / compliance audits
CREATE ROLE auditor LOGIN PASSWORD '<random>';
GRANT  CONNECT ON DATABASE digital_checklist TO auditor;
GRANT  USAGE   ON SCHEMA public TO auditor;
GRANT  SELECT  ON ALL TABLES IN SCHEMA public TO auditor;
```

This puts a hard guarantee at the database level that the application binary cannot rewrite history — even a compromised app process cannot UPDATE an audit row.

### Data retention defaults

- **Submissions, defects, audit:** indefinite (compliance-driven).
- **Local mobile queues:** 60 days (90 for audit), per `SyncWorker.MAX_QUEUE_AGE`. Configurable per deployment.
- **Service worker IndexedDB queue (web):** browser-managed; cleared on "Clear site data".
- **Backups:** 30-day local + 1-year off-site is the default; mining audits typically request 7 years for incident records — extend off-site retention to match your specific regulator's ask.

---

## 11. Known limitations and roadmap

| Item | State | Notes |
|---|---|---|
| iOS target | Not built | Requires Apple Developer Enterprise; ~1 dev-week. |
| Multi-language (isiZulu / Sesotho / Setswana) | Not built | `IStringLocalizer` pass + .resx files — see audit. |
| Real-time multi-mine federation | Not built | Current design is one process per mine. Cross-mine reporting would need a data warehouse layer. |
| Server-side queue inspection UI | Not built | Add an `/Admin/QueueHealth` page showing pending counts + oldest-row age. |
| Background-job scheduler | Not built | If parts ordering needs scheduled email summaries, add Hangfire. |

---

## 12. Repository layout

```
EquipmentChecklist/                 ← server (ASP.NET Core MVC + API)
├── Controllers/
│   ├── Api/SyncController.cs       ← REST API for mobile
│   ├── AdminController.cs
│   ├── SupervisorController.cs
│   ├── MechanicController.cs
│   └── ChecklistController.cs
├── Services/
│   ├── ChecklistService.cs         ← submission lifecycle
│   ├── NotificationService.cs      ← SignalR push + DB write
│   ├── AuditService.cs             ← append-only audit
│   ├── ReportsService.cs           ← KPI dashboard
│   └── PdfService.cs               ← QuestPDF rendering
├── Migrations/                     ← EF Core migrations
├── Models/                         ← entity classes + ListFilter helper
├── Views/                          ← Razor (Admin, Supervisor, Mechanic, Checklist)
├── wwwroot/                        ← static assets + PWA service worker
└── Program.cs                      ← composition root, auth scheme map

EquipmentChecklist.Mobile/          ← MAUI Blazor Hybrid (Android + Windows)
├── Components/
│   ├── Pages/                      ← Razor pages (Dashboard, Checklist, MyDefects, ...)
│   ├── Layout/MainLayout.razor
│   ├── FilterBar.razor             ← shared filter UI
│   └── PdfViewer.razor
├── Services/
│   ├── ApiClient.cs                ← HttpClient wrapper
│   ├── AuthService.cs              ← sign-in events
│   ├── SyncWorker.cs               ← drain orchestration
│   ├── SubmissionQueue.cs          ← SQLite-backed queue
│   ├── ActionQueue.cs              ← same, for supervisor/mechanic actions
│   ├── AuditQueue.cs               ← same, for telemetry
│   ├── NotificationService.cs      ← SignalR client + inbox
│   ├── LocalCache.cs               ← machine roster + last-synced
│   └── LocalPdfService.cs          ← jsPDF interop
├── wwwroot/                        ← JS shim (signature pad, jsPDF, sigpad)
└── MauiProgram.cs                  ← composition root

EquipmentChecklist.Shared/          ← DTOs + enums shared across server + mobile
└── DTOs/DTOs.cs

EquipmentChecklist.Tests/           ← xUnit + InMemory EF + WebApplicationFactory
├── Services/ChecklistServiceTests.cs
├── Controllers/SupervisorWorkflowTests.cs
├── Controllers/MechanicActionTests.cs
└── Controllers/SubmissionAccessTests.cs
```

---

## Appendix A — Required environment variables

| Variable | Required | Notes |
|---|---|---|
| `ConnectionStrings__PostgreSQL` | Yes | Standard Npgsql connection string |
| `Jwt__Key` | Yes | Minimum 256 bits, base64. Rotate annually. |
| `Mine__Name` / `Mine__ShortName` / `Mine__Tagline` / `Mine__ComplianceText` | Yes | Per-site labels |
| `Fido2__ServerDomain` | Yes | Public hostname (no scheme) |
| `Fido2__Origins__0` | Yes | Full URL including scheme |
| `Email__SmtpHost` / `Port` / `Username` / `Password` / `From` | If you want emails | Parts-order + rejection notifications go through SMTP |
| `Email__AdminEmail` / `ManagerEmail` | If you want emails | Recipients for system-level emails |
| `Auth__VoucherKeyPath` | No (defaults `App_Data/voucher_rsa.pem`) | RSA key for offline-voucher signing; auto-generated on first boot if missing |
| `ASPNETCORE_ENVIRONMENT` | Yes | `Production` for prod, `Development` for dev |

---

## Appendix B — Useful queries

```sql
-- Who signed off submission #1247?
SELECT a."ActorName", a."OccurredAtServer", a."PayloadJson"
FROM "AuditEvents" a
WHERE a."TargetType" = 'Submission'
  AND a."TargetId"   = 1247
ORDER BY a."OccurredAtServer";

-- All machines immobilised right now + how long they've been down
SELECT m."MachineNumber", m."ImmobilisedReason",
       MIN(d."CreatedAt") AS first_defect_at,
       NOW() - MIN(d."CreatedAt") AS down_for
FROM "Machines" m
JOIN "ChecklistSubmissions" s ON s."MachineId" = m."Id"
JOIN "DefectOrders" d ON d."SubmissionId" = s."Id"
WHERE m."IsImmobilised" = true
  AND d."RepairStatus" <> 4   -- 4 = Completed
GROUP BY m."Id", m."MachineNumber", m."ImmobilisedReason"
ORDER BY down_for DESC;

-- Conflict-rejected loss rate (last 7 days)
SELECT date_trunc('day', "CreatedAt") AS day,
       COUNT(*) AS conflict_count
FROM "Notifications"
WHERE "Kind" = 'conflict.rejected'
  AND "CreatedAt" >= NOW() - INTERVAL '7 days'
GROUP BY 1
ORDER BY 1;
```

---

*End of document.*
