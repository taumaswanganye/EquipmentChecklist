# Project State — Equipment Pre-Use Checklist System

**Last updated:** end of Phase 5 (mobile rollout polish complete).
**Maintainer:** TauGM · Enaleni Engineering.
**Deployment site:** Belfast Coal Mine, Mpumalanga.

This is the **single navigation point** for the whole project. Every shipped deliverable — code, doc, runbook, user story, RFQ, SQL view — is listed here with one line of what it is and an absolute path to it. New developers, consultants, and inspectors all start here.

---

## 1.  Top-level repo layout

| Path | What lives here |
|---|---|
| `EquipmentChecklist/` | ASP.NET Core 8 MVC + Web API server (the main app). |
| `EquipmentChecklist.Mobile/` | MAUI Blazor Hybrid Android + Windows app for operators / supervisors / mechanics. |
| `EquipmentChecklist.Shared/` | DTOs + enums + audit-action constants used by both server and mobile. |
| `EquipmentChecklist.Tests/` | xUnit test project (controllers, services, status calc). |
| `PowerBI/` | Power BI deliverables (SQL view layer, PBIDS connection, theme, design spec). |
| `UserStories/` | Markdown + PDF user stories for the development backlog. |
| `*.docx / *.pdf` (root) | Standalone documents — RFQs, runbooks, compliance statements. |
| `*.md` (root) | Operational documentation — this file, deployment runbook, etc. |

---

## 2.  Code components (server)

### Controllers — what role each surface serves

| Controller | Route | Audience | Phase |
|---|---|---|---|
| `HomeController` | `/` | All | original |
| `AccountController` | `/Account/*` | All — sign in / sign out | original |
| `ChecklistController` | `/Checklist/*` | Operator — submit + history | original |
| `SupervisorController` | `/Supervisor/*` | Supervisor — sign-off queue, NO-GO machines | original |
| `MechanicController` | `/Mechanic/*` | Mechanic/Artisan — defect queue, complete | original |
| `AdminController` | `/Admin/*` | Admin — employees, devices, fleets, settings, audit, outbox, reports, etc. | original + Phases 1-4 |
| `MachineWizardController` | `/Admin/MachineWizard/*` | Admin — three-step machine creation | original |
| `SecurityController` | `/Security/*` | All — Fido2 WebAuthn enrolment + sign-in | Phase 1 |
| `PlannerController` | `/Planner/*` | Planner — defect capture + reject | Phase 3 |
| `ControlRoomController` | `/ControlRoom/*` | Control Room — dispatch + reassign + escalate | Phase 3+4 |
| `Api/SyncController` | `/api/sync/*` | Mobile app — JWT-auth login, machine sync, submit, mine config | Phase 1.A |
| `Api/IntegrationsController` | `/api/integrations/*` | External CMMS (SAP) — inbound webhook for work-order-closed | Phase 3 |

### Services — application logic

| Service | Purpose |
|---|---|
| `ChecklistService` | Status calc + persistence + auto-creates DefectOrders on NO-GO + writes outbox row + auto-links re-checks. |
| `ReportsService` | Builds the `/Admin/Reports` dashboard payload (KPIs, trends, top-N, competency, config audit). |
| `CompetencyService` | MHSA Section 22(a) "is this operator competent on this machine type?" gate. |
| `AuditService` | Append-only audit logger with SHA-256 hash-chain on every row. |
| `NotificationService` + `NotificationHub` | SignalR cross-role notifications. |
| `EmailService` | SMTP for parts-orders, password resets, competency expiry reminders. |
| `ConfigurationService` | DB-backed AppSettings with in-memory cache + IConfiguration fallback. |
| `OfflineVoucherService` | Phase 1 — signed offline-unlock voucher for mobile. |
| `PdfService` | PSIC-layout checklist PDF generation. |
| `CompetencyExpiryWorker` | Daily 06:00 UTC background job — emails 30 / 7 / 0 days before competency expires. |
| `Services/Integrations/IIntegrationPublisher` | Interface for external CMMS (SAP / IBM MQ / etc.). |
| `Services/Integrations/NoOpIntegrationPublisher` | Default — logs + returns. |
| `Services/Integrations/SapPmIntegrationPublisher` | Scaffold — POSTs to SAP OData Maintenance Order endpoint. |
| `Services/Integrations/OutboxPublishWorker` | BackgroundService — polls OutboxMessages every 5s, dispatches via publisher, retries with exponential backoff, dead-letters after 5 attempts. |
| `Services/DbFido2PostConfigure` | Reads Fido2.* config from DB at request time. |

### Models — domain entities (Models/Models.cs unless noted)

| Entity | Notable fields | Added in |
|---|---|---|
| `ApplicationUser` | + FleetId (Phase 4) | original |
| `Machine` | + IsImmobilised, AwaitingAdminClearance, FleetId | original / Phase 4 |
| `ChecklistTemplate`, `ChecklistTemplateItem` | | original |
| `ChecklistSubmission` | + OperatorSignature, SupervisorSignature, OriginalSubmissionId (Phase 4.6) | original / Phase 4 |
| `SubmissionItem` | + PhotoData, AudioData | original / round 2 |
| `DefectOrder` | + PlannerCapturedAt, PlannerCapturedById, JobCardNumber, DispatchedAt, DispatchedById | original / Phase 3 |
| `Notification` | | round 3 |
| `AuditEvent` (Models/AuditEvent.cs) | + PrevHash, RowHash (Phase 3) | round 3 / Phase 3 |
| `AllowedDevice` | Device-fingerprint allowlist | round 3 |
| `OperatorCompetency` | MHSA Section 22(a) tracking | round 3 |
| `AppSetting` | DB-backed runtime config | round 3 |
| `OutboxMessage` (Models/OutboxMessage.cs) | Transactional outbox row | Phase 3 |
| `Fleet` | Contractor groupings (Mota-Engil, Moolmans, etc.) | Phase 4 |
| `UserCredential` | Fido2 WebAuthn credential per user | Phase 1 |

### Mobile app components (`EquipmentChecklist.Mobile/`)

| File | Purpose |
|---|---|
| `MauiProgram.cs` | DI registration, HttpClient + DeviceFingerprintHandler + AuthFailureHandler wiring, runtime URL load from Preferences (Phase 5.2). |
| `Services/ApiClient.cs` | All HTTP calls to the server, JWT auth, error envelope handling. |
| `Services/AuthService.cs` | Online + offline sign-in, biometric flag (per-email since Phase 5.1), session restore, deactivation handling. |
| `Services/BiometricUnlock.cs` | AndroidX BiometricPrompt + Windows Hello wrapper. Class 3 + Class 2 + Device Credential. |
| `Services/DeviceFingerprint.cs` | Stable per-device fingerprint (Android ID + Windows MachineGuid). |
| `Services/LocalCache.cs` | SQLite local cache (sqlite-net-pcl). |
| `Services/SyncWorker.cs` | Drains the offline action queue when connectivity returns. |
| `Services/ApiHealth.cs` | Polls API every 20s, /me every 5min for competency cache refresh. |
| `Services/NotificationService.cs` | SignalR + offline-cached notification bell. |
| `Components/Pages/Login.razor` | Email + password + biometric button + ⚙ Server URL link + QR-coded device fingerprint. |
| `Components/Pages/ServerSetup.razor` (`/setup`) | Phase 5.2 — first-launch / one-off API URL configuration. |
| `Components/Pages/Dashboard.razor` | Operator's machine cards with competency chips. |
| `Components/Pages/Checklist.razor` | Pre-shift checklist with photo + voice memo capture. |
| `Components/Pages/MyMachines.razor` | Operator's machine list with competency expiry warnings. |
| `Components/Pages/MySubmissions.razor` | Operator's history with offline-cached PDFs. |
| `Components/NotificationBell.razor` | SignalR-driven inbox with kind-aware deep links (Phase 5 added defect.assigned). |

---

## 3.  Documents (root)

| File | What it is |
|---|---|
| `DEPLOYMENT_RUNBOOK.md` | The runbook a deployer follows to take Phase 3+4 to production. Build → bootstrap → verify → rollback. 9 sections. |
| `PROJECT_STATE.md` | This file. |
| `Device_Setup_Runbook.pdf` (+ .docx) | Printable guide for whoever onboards tablets onto the system. 12-step per-device checklist + troubleshooting table. |
| `DMR_Reg_10_3_Compliance_Statement.pdf` (+ .docx) | SHE-manager-signable statement for DMR inspector handover. Clause-by-clause mapping to system features. |
| `Mine_Tablet_Procurement_RFQ.docx` | Vendor-ready RFQ for 32-device tablet rollout. Spec tables + pricing-response section + B-BBEE scoring. |
| `EquipmentChecklist_Architecture.pdf` (older) | High-level architecture diagram. Pre-Phase-3. |

## 4.  UserStories/

| File | What it is |
|---|---|
| `US-001-SAP-Outbox-IBMMQ-Integration.pdf` (+ .docx + .md) | Tatenda-ready user story. 5-day implementation plan for swapping NoOpIntegrationPublisher with IbmMqIntegrationPublisher, including Docker setup for IBM MQ developer image. |

## 5.  PowerBI/

| File | What it is |
|---|---|
| `mine_analytics_views.sql` | 22 PostgreSQL views over the EF tables. Dim + fact + KPI views. Includes Phase 3 (planner_captured_at, dispatched_at), Phase 4 (fleet membership, OriginalSubmissionId, first-time-fix). |
| `mine_powerbi.pbids` | Power BI connection file — double-click to open the PostgreSQL connection. |
| `mine_powerbi_theme.json` | Branded dark-navy + teal theme. Matches mobile app + MVC. |
| `Mine_PowerBI_Dashboard_Spec_v2.docx` | Six-page design spec (Executive, SHE, Supervisor, Maintenance, Operator Competency, Configuration Audit). Hand to an analyst with the PBIDS + theme and they build the dashboard from it. |

---

## 6.  Major features by phase

### Phase 1 — Security baseline
Fido2 WebAuthn enrolment + sign-in, offline voucher service, Service Worker, PWA manifest.

### Phase 2 — Operator-side polish
Mobile UI revamp (sidebar layout, stats dashboard, photo + voice memo capture), supervisor + mechanic views, offline action queues, biometric on mobile.

### Phase 3 — Workflow expansion (the "Tatenda story" flow)
New roles Planner + ControlRoom, DefectOrder workflow columns, PlannerController + view (capture + reject), ControlRoomController + view (dispatch + reassign + escalate), sidebar + display polish ("Mechanic" → "Artisan" labels).

### Phase 4 — Operational completeness
Fleet entity + FleetId on Machine + User, fleet-aware Artisan dispatch (Mota-Engil / Moolmans), Admin Fleets CRUD, Phase 4.6 linked re-checks (OriginalSubmissionId + RE-CHECK badge), Phase 4.7 awareness-only supervisor route for defects, Phase 4.10 FleetId widgets on Employees + Machine Wizard.

### Phase 5 — Mobile rollout polish
Per-email biometric (BIO_KEY scoped per operator for shared tablets), runtime API URL from Preferences (one APK fits any deployment), QR code rendering of device fingerprint on Login.

### Cross-cutting deliverables
Audit hash-chain (cryptographic tamper-evidence), SAP PM integration scaffold, transactional outbox + admin monitoring page with retry / purge, ConfigurationService DB-backed runtime config, MHSA Section 22(a) operator competency tracking with daily reminder worker, sidebar pending-count badges.

---

## 7.  Configuration keys (Admin → Settings)

Grouped by category for orientation. All editable at runtime by Admin.

**Mine** — MineName, MineShortName, MineTagline, SheOfficerEmail, MineManagerEmail.
**Email** — SmtpHost, SmtpPort, SmtpUser, SmtpPassword (secret), FromAddress, FromDisplayName, ManagerEmail, AdminEmail.
**Security** — Fido2.ServerDomain, Fido2.ServerName, Fido2.Origins, Auth.SeededAdminEmail, Auth.SeededAdminPassword (secret).
**Integrations** — Sap.Enabled, Sap.BaseUrl, Sap.ApiKey (secret), Sap.WorkOrderEndpoint, Integrations.InboundKey (secret), Outbox.MaxRetries.

---

## 8.  Notification kinds the system emits

| Kind | Recipient | Trigger |
|---|---|---|
| `submission.nogo` | Supervisor (awareness only — Phase 4.7) | Operator submits NO-GO |
| `submission.gobut` | Supervisor (sign-off needed) | Operator submits GO-BUT 24H |
| `submission.approved` | Operator | Supervisor signs off |
| `submission.rejected` | Operator | Supervisor rejects |
| `defect.assigned` | Artisan (Phase 3) | Control Room dispatches |
| `defect.resolved` | Operator | Artisan completes defect |
| `conflict.rejected` | Losing actor | Offline action lost the race |
| `machine.awaiting_clearance` | Admin | Mechanic completed last open defect |
| `machine.cleared` | Operator | Admin signed off the machine for return to service |
| `competency.expiring` | Operator + supervisor + SHE | 30 / 7 days before competency expires |
| `competency.expired` | Same | Day of expiry |

## 9.  Audit action codes

User access — `user.created`, `user.password_reset`, `user.deactivated`, `user.reactivated`, `user.fleet_changed`.
Admin policy — `admin.created`, `admin.deactivated`.
Settings — `settings.changed`, `settings.reset`.
Competency — `competency.added`, `competency.revoked`, `competency.renewed`, `competency.expired`.
Submissions — `submission.signoff`, `submission.reject`, `submission.nogo`, `submission.blocked.no_competency`, `submission.attempted.no_competency`.
Defects — `defect.complete`, `defect.closed.via_integration`.
Phase 3 workflow — `planner.captured`, `planner.rejected`, `controlroom.dispatched`, `controlroom.reassigned`, `controlroom.escalated`.
Outbox — `outbox.dead_lettered`, `outbox.retried`, `outbox.purged`.
Machine clearance — `machine.clearance.grant`, `machine.clearance.deny`.

## 10.  Database schema additions by phase

| Phase | Tables | Columns added to existing |
|---|---|---|
| Original | (all base tables) | — |
| Phase 1 | `UserCredentials` | — |
| Round 3 | `Notifications`, `AuditEvents`, `AllowedDevices`, `OperatorCompetencies`, `AppSettings` | Machine: AwaitingAdminClearance, ClearedByAdminId, ClearedAt, AdminClearanceNotes |
| Phase 3 | `OutboxMessages` | DefectOrders: PlannerCapturedAt, PlannerCapturedById, JobCardNumber, DispatchedAt, DispatchedById. AuditEvents: PrevHash, RowHash |
| Phase 4 | `Fleets` | Machines: FleetId. AspNetUsers: FleetId. ChecklistSubmissions: OriginalSubmissionId |

All Phase 3+ additions use idempotent bootstrap in `Program.cs` — no EF migration files, no `dotnet ef database update` step required.

---

## 11.  What's intentionally NOT done (work not on the conversation backlog)

- **Real SAP integration end-to-end** — scaffold in place; needs a SAP tenant + basis-team coordination to wire up.
- **IBM MQ publisher class** — documented in US-001 user story; Tatenda's pickup.
- **Tests for Phase 3+4 controllers** — only existing controller tests are pre-Phase-3.
- **Power BI dashboard pages for Planner / Control Room / First-time-fix** — SQL views are extended, .docx hasn't been rebuilt with new visuals.
- **Fleet-based reporting visualisations** — KPI views exist; dashboard pages don't.
- **AWS deployment** — discussed but not built; needs container image, RDS provisioning, ALB.
- **Mobile push notifications via FCM** — currently SignalR only (requires app foreground).

---

## 12.  Quickstart for a new person on the project

1. **Read** `DEPLOYMENT_RUNBOOK.md` end to end (~15 min).
2. **Build** `dotnet build EquipmentChecklist.sln -c Release`.
3. **Run** the server locally with `dotnet run --project EquipmentChecklist`.
4. **Sign in** as the seeded admin (defaults in `Auth.SeededAdminEmail` / `Auth.SeededAdminPassword` config keys).
5. **Walk the Phase 3 verification flow** in §3 of the deployment runbook. If all seven steps pass, you understand the system.
6. **Browse this PROJECT_STATE.md** to map "where would I find X?" to a concrete path.

---

*Issued by TauGM · Equipment Checklist Programme · Enaleni Engineering · Belfast Coal Mine*
