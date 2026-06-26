# Deployment + Verification Runbook
## Equipment Pre-Use Checklist System — Phase 3 + 4

**Audience:** Tatenda (or whoever next pulls the repo and deploys).
**Estimated time:** 90 minutes for build + deploy + full verification walk-through.
**Prerequisites:** .NET 8 SDK, PostgreSQL 14+, a Windows / Linux server with the API project deployed (IIS, Kestrel-behind-Nginx, or a Windows service — pick whichever matches your existing pattern).

---

## 0.  What changed in Phase 3 + 4

Three new roles (`Planner`, `ControlRoom`, plus existing `Mechanic` re-labelled "Artisan" in UI), three new tables (`Fleets`, `OutboxMessages`, `OperatorCompetencies`), about a dozen new columns added via bootstrap `ALTER TABLE IF NOT EXISTS`, a transactional outbox pattern feeding an integration publisher (SAP scaffold + no-op default), a cryptographic hash chain on the audit trail, fleet-aware Artisan dispatch (Mota-Engil → only Mota-Engil Artisans), linked re-checks, and awareness-only supervisor routing for defects. Mobile app: per-email biometric, runtime server URL, QR code of device fingerprint on the not-authorised callout.

All schema changes are **idempotent** — they use `ALTER TABLE IF NOT EXISTS` and `CREATE TABLE IF NOT EXISTS`. Re-running the deployment is harmless. No EF migration files were added; bootstrap happens in `Program.cs` on startup.

---

## 1.  Build

```powershell
cd C:\Users\tauma\source\repos\Equip
git pull
dotnet restore
dotnet build EquipmentChecklist.sln -c Release
```

**Expected:** `Build succeeded. 0 Errors`. Likely warnings about nullable references in the Phase 3 controllers — those are pre-existing project conventions, not blockers.

**If the build fails**, the most likely causes given the round of changes are:
1. A missing `using` on one of the new controllers (`PlannerController`, `ControlRoomController`) — error will name the namespace.
2. A property name on `DefectOrder` or `ChecklistSubmission` I assumed wrong — error will name the property.
3. `Microsoft.JSInterop.IJSRuntime` not found on mobile project — should already be a MAUI Blazor dependency.

Paste the first error to the dev who shipped the round and they'll fix it in ten minutes.

---

## 2.  Deploy + first-boot bootstrap

Stop the running API. Copy the new build output to the server (whatever your existing process is). Start the API.

**Watch the first-boot log carefully.** In order, you should see:

```
info: Microsoft.EntityFrameworkCore.Migrations[20405]
      No migrations were applied. The database is already up to date.
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... CREATE TABLE IF NOT EXISTS "AllowedDevices" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... CREATE TABLE IF NOT EXISTS "OperatorCompetencies" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... CREATE TABLE IF NOT EXISTS "AppSettings" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... CREATE TABLE IF NOT EXISTS "OutboxMessages" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... CREATE TABLE IF NOT EXISTS "Fleets" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... ALTER TABLE "DefectOrders" ADD COLUMN IF NOT EXISTS "PlannerCapturedAt" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... ALTER TABLE "ChecklistSubmissions" ADD COLUMN IF NOT EXISTS "OriginalSubmissionId" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... ALTER TABLE "Machines" ADD COLUMN IF NOT EXISTS "FleetId" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... ALTER TABLE "AspNetUsers" ADD COLUMN IF NOT EXISTS "FleetId" ...
info: Microsoft.EntityFrameworkCore.Database.Command[20101]
      Executed DbCommand ... ALTER TABLE "AuditEvents" ADD COLUMN IF NOT EXISTS "PrevHash" ...
info: EquipmentChecklist.Services.Integrations.OutboxPublishWorker[0]
      OutboxPublishWorker started (interval 5s)
```

Then the seed pass:

```
-- Roles (idempotent — only adds missing ones)
-- Fleets (only on first boot when count==0)
-- AppSettings (only on first boot)
```

**Sanity-check the database directly** to confirm:

```sql
-- All six roles should exist.
SELECT "Name" FROM "AspNetRoles" ORDER BY "Name";
-- Expected: Admin, ControlRoom, Mechanic, Operator, Planner, Supervisor

-- Three seeded fleets on first boot.
SELECT "Name", "Color", "IsActive" FROM "Fleets" ORDER BY "Id";
-- Expected: Belfast Direct (#0E9488), Mota-Engil (#D97706), Moolmans (#8B5CF6)

-- New columns on DefectOrders.
SELECT column_name FROM information_schema.columns
  WHERE table_name = 'DefectOrders'
    AND column_name IN ('PlannerCapturedAt','PlannerCapturedById','JobCardNumber','DispatchedAt','DispatchedById');
-- Expected: all 5 rows

-- Outbox + AuditEvents hash-chain columns.
SELECT column_name FROM information_schema.columns
  WHERE table_name = 'OutboxMessages';
-- Expected: 11 columns including Status, AttemptCount, NextAttemptAt

SELECT column_name FROM information_schema.columns
  WHERE table_name = 'AuditEvents'
    AND column_name IN ('PrevHash','RowHash');
-- Expected: 2 rows
```

If any of those queries return fewer rows than expected, the bootstrap didn't fire cleanly — check the API log for SQL errors and re-deploy.

---

## 3.  Verify Phase 3 workflow end-to-end

Create the test accounts via Admin → Employees (or use the seeded admin to add them):

| Role | Email | Password |
|---|---|---|
| Operator | `thuli.test@belfast.co.za` | `Operator@1` |
| Supervisor | `eugene.test@belfast.co.za` | `Sup@1234` |
| Planner | `walter.test@belfast.co.za` | `Plan@1234` |
| ControlRoom | `lugisani.test@belfast.co.za` | `Ctrl@1234` |
| Mechanic (Artisan) | `johan.test@belfast.co.za` | `Art@1234` |

Tag Johan as a Mota-Engil Artisan via the inline Fleet dropdown on Admin → Employees. Tag at least one machine (e.g. ADT-04) as Mota-Engil via Admin → Machine Wizard.

Then walk this loop:

**Step 1 — Operator submits NO-GO.** Sign in as Thuli, find ADT-04, start the checklist, mark the brakes as Critical Defect, submit. The operator should see the NO-GO result page.

**Step 2 — Supervisor awareness.** Sign in as Eugene. The dashboard bell should show "🚫 NO-GO on ADT-04 — for awareness". Eugene's sign-off queue (`/Supervisor`) should NOT contain the submission — defects auto-route to Planner.

**Step 3 — Planner captures.** Sign in as Walter. `/Planner` shows the defect at the top with stats showing Pending = 1. Type a jobcard number (e.g. `WO-7741`), click Capture. The row disappears from the queue.

**Step 4 — Control Room dispatches.** Sign in as Lugisani. `/ControlRoom` shows the defect with the green "Mota-Engil" fleet chip. The Artisan dropdown contains ONLY Johan (because he's the only Mota-Engil Artisan). Pick Johan, click Dispatch. The row moves to the "🔄 Recently dispatched (last 24h)" section.

**Step 5 — Artisan receives notification.** Sign in as Johan. The notification bell shows "Dispatched: ADT-04". `/Mechanic` shows the defect at the top of his queue with the jobcard number visible. Tap into it, click Complete, sign, save.

**Step 6 — Operator re-checks.** Sign in as Thuli again, do a fresh checklist on ADT-04, all items in order, submit. On Thuli's `/Submissions` history, the new clean submission shows the teal **"🔁 RE-CHECK of #1234"** chip below the machine name.

**Step 7 — Supervisor sees re-check confirmation.** Sign in as Eugene. Notification bell shows the supervisor's awareness ping for the clean re-check.

If all seven steps succeed, the Phase 3 + 4 workflow is live end-to-end.

---

## 4.  Verify the outbox + audit hash chain

Open Admin → Outbox. After Step 1 above, you should see one Draft → Sent row for the defect (NoOpIntegrationPublisher succeeds silently). After Step 3 (Planner Capture) and Step 4 (Control Room Dispatch), the audit trail should show new rows — check Admin → Audit, filter on actions `planner.captured` and `controlroom.dispatched`.

Open Admin → Verify Audit Chain. Headline should read **CHAIN INTACT** with green checkmark. Rows scanned will equal whatever your audit table count is. If the headline reads **CHAIN BROKEN** the very first time you run this, the most likely cause is that pre-deployment audit rows have NULL PrevHash + NULL RowHash and the verifier is intentionally treating that as "start of chain" — the explainer paragraph on the result page covers this.

---

## 5.  Verify fleet-aware dispatch refuses cross-fleet

The strongest test of Phase 4's fleet segregation is the **negative path**:

Create a second Artisan `david.test@belfast.co.za`, tag him as Moolmans on Admin → Employees. Re-do Steps 1–4 above. At Step 4, the Artisan dropdown should still contain ONLY Johan, NOT David — because ADT-04 is tagged Mota-Engil and David is Moolmans. Confirms the fleet filter is doing its job.

Now create a third defect on a machine that has NO fleet tag. At Step 4 for that defect, the Artisan dropdown should contain BOTH Johan and David — because untagged machines are dispatchable to any Artisan (pre-Phase-4 behaviour preserved).

---

## 6.  Verify mobile app rollout

Install the new APK on a test Android tablet. On first launch, you should NOT immediately see the Login form — there's no compile-time URL, so you need to set one.

Tap the small "⚙ Server URL settings" link at the bottom of Login. The Setup screen opens. Type the production API URL (e.g. `https://api.belfastmine.co.za/`), tap Save. Green "Saved — restart the app to apply" callout appears with a "↻ Restart now" button. Tap it. App closes. Re-open it.

On Login, type the operator's email + password. The device-not-authorised callout should appear with both the QR code AND the hex string. Open Admin → Devices on the rollout laptop, click "+ Authorise a device", scan the QR with your phone camera and paste, set the operator + fleet, Save. Retry sign-in on the tablet — should succeed.

Now sign in as a different operator on the same tablet. Their biometric opt-in is independent — first operator's "Use biometrics" tick doesn't leak onto the second.

---

## 7.  Known gaps / next-round items

- **SAP integration is scaffold only.** `NoOpIntegrationPublisher` is the registered implementation; defect publishes drain successfully but go nowhere external. To wire real SAP, swap to `SapPmIntegrationPublisher` in Program.cs (one-line comment switch) and configure `Sap.Enabled` / `Sap.BaseUrl` / `Sap.ApiKey` via Admin → Settings.
- **IBM MQ integration** is documented in `UserStories/US-001-SAP-Outbox-IBMMQ-Integration.pdf` for Tatenda to pick up. Outbox infrastructure is in place; only the `IbmMqIntegrationPublisher` class needs writing.
- **Tests for Phase 3 + 4 controllers** don't exist. Existing controller tests in `EquipmentChecklist.Tests/` are the pattern to follow.
- **Power BI dashboard pages** for the new Planner/Control Room queues don't yet exist in the spec doc — the SQL views are extended (`vw_kpi_planner_queue`, `vw_kpi_dispatch_queue`, `vw_kpi_first_time_fix`) but the .docx hasn't been rebuilt to include the new pages.

---

## 8.  Rollback plan

The bootstrap migrations are additive — no DROPs, no DELETEs. To roll back to the pre-Phase-3 code:

1. Stop the API.
2. Deploy the previous build.
3. Start the API.

The new columns + tables remain in the database, but the old code doesn't reference them and they're nullable, so nothing breaks. Once you're confident the rollback is stable, you can drop them manually:

```sql
-- ONLY if you're certain you're never going back to Phase 3+4.
ALTER TABLE "DefectOrders" DROP COLUMN IF EXISTS "PlannerCapturedAt";
ALTER TABLE "DefectOrders" DROP COLUMN IF EXISTS "PlannerCapturedById";
ALTER TABLE "DefectOrders" DROP COLUMN IF EXISTS "JobCardNumber";
ALTER TABLE "DefectOrders" DROP COLUMN IF EXISTS "DispatchedAt";
ALTER TABLE "DefectOrders" DROP COLUMN IF EXISTS "DispatchedById";
ALTER TABLE "ChecklistSubmissions" DROP COLUMN IF EXISTS "OriginalSubmissionId";
ALTER TABLE "Machines"    DROP COLUMN IF EXISTS "FleetId";
ALTER TABLE "AspNetUsers" DROP COLUMN IF EXISTS "FleetId";
DROP TABLE IF EXISTS "OutboxMessages";
DROP TABLE IF EXISTS "Fleets";
-- AuditEvents PrevHash/RowHash should NOT be dropped — keeping them
-- preserves the tamper-evidence record for the period they were live.
```

---

## 9.  Quick smoke-test SQL (paste into psql to confirm health)

```sql
\echo '── Counts of key entities ──'
SELECT 'Roles'                AS entity, COUNT(*) FROM "AspNetRoles"
UNION ALL SELECT 'Users',           COUNT(*) FROM "AspNetUsers"
UNION ALL SELECT 'Machines',        COUNT(*) FROM "Machines"
UNION ALL SELECT 'Fleets',          COUNT(*) FROM "Fleets"
UNION ALL SELECT 'Submissions',     COUNT(*) FROM "ChecklistSubmissions"
UNION ALL SELECT 'Defect orders',   COUNT(*) FROM "DefectOrders"
UNION ALL SELECT 'Audit events',    COUNT(*) FROM "AuditEvents"
UNION ALL SELECT 'Outbox msgs',     COUNT(*) FROM "OutboxMessages"
UNION ALL SELECT 'Competencies',    COUNT(*) FROM "OperatorCompetencies"
UNION ALL SELECT 'Allowed devices', COUNT(*) FROM "AllowedDevices";

\echo '── Outbox health (should be 0 dead-lettered) ──'
SELECT "Status", COUNT(*) FROM "OutboxMessages" GROUP BY "Status";

\echo '── Workflow stage of open defects ──'
SELECT
    CASE
        WHEN "ResolvedAt"        IS NOT NULL THEN '4. Resolved'
        WHEN "DispatchedAt"      IS NOT NULL THEN '3. Artisan working'
        WHEN "PlannerCapturedAt" IS NOT NULL THEN '2. Awaiting dispatch'
        ELSE                                          '1. Awaiting planner'
    END AS workflow_stage,
    COUNT(*) AS defects
FROM "DefectOrders"
GROUP BY 1 ORDER BY 1;

\echo '── Audit chain quick sample (first 5 rows should chain forward) ──'
SELECT "Id", LEFT("Action", 30) AS action,
       LEFT(COALESCE("PrevHash", '(start)'), 16) AS prev16,
       LEFT(COALESCE("RowHash", '(legacy)'), 16) AS row16
FROM "AuditEvents"
ORDER BY "Id" DESC
LIMIT 5;
```

---

*Issued by TauGM · Equipment Checklist Programme · Enaleni Engineering*
*Pairs with: `Device_Setup_Runbook.pdf`, `Mine_PowerBI_Dashboard_Spec_v2.docx`, `UserStories/US-001-SAP-Outbox-IBMMQ-Integration.pdf`, `DMR_Reg_10_3_Compliance_Statement.pdf`*
