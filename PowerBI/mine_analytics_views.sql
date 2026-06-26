-- ════════════════════════════════════════════════════════════════════════════
--  Equipment Checklist — Mine Analytics Views for Power BI
--
--  Run this script ONCE against the same Postgres database the
--  EquipmentChecklist API points at. It creates a layer of denormalised
--  read-only views the Power BI dashboard binds to.
--
--  Why views instead of pointing Power BI directly at the EF tables:
--   • Stable shape — if a column is renamed in the app's models, the view
--     can be patched without touching the .pbix.
--   • Pre-joined — the dashboard never has to write joins in Power Query.
--   • Enum translation — integer columns (Status, Shift, Type, Role) are
--     decoded into human labels here, so visuals show "GO-BUT 24H" instead
--     of "2".
--   • Read-only by definition — even if the DirectQuery user has write
--     grants on the base tables, they can't accidentally mutate via a view.
--
--  Naming convention:
--     vw_fact_*         row-per-event tables that drive the visuals
--     vw_dim_*          slowly-changing dimension lookups (Power BI 'tables')
--     vw_kpi_*          single-row aggregates for the executive cards
--
--  All views use double-quoted identifiers because EF Core's default Npgsql
--  naming policy preserves PascalCase ("Machines", "ChecklistSubmissions").
-- ════════════════════════════════════════════════════════════════════════════

BEGIN;

-- ════════════════════════════════════════════════════════════════════════════
--  DIMENSIONS
--
--  These never change shape between refreshes — Power BI imports them once
--  per scheduled refresh. Even on DirectQuery they're tiny enough to be
--  cached effectively in Power BI's local model.
-- ════════════════════════════════════════════════════════════════════════════

-- ── vw_dim_user ─────────────────────────────────────────────────────────────
-- One row per app user with their role label and active status. Joined into
-- every fact view so visuals can slice by "Operator John Smith" or "All
-- supervisors". `Role` comes from the Identity.Role enum:
--   1 = Admin, 2 = Operator, 3 = Supervisor, 4 = Mechanic.
DROP VIEW IF EXISTS vw_dim_user CASCADE;
CREATE VIEW vw_dim_user AS
SELECT
    u."Id"                                          AS user_id,
    u."FullName"                                    AS full_name,
    u."EmployeeNumber"                              AS employee_number,
    u."Email"                                       AS email,
    u."IsActive"                                    AS is_active,
    u."Role"                                        AS role_code,
    CASE u."Role"
        WHEN 1 THEN 'Admin'
        WHEN 2 THEN 'Operator'
        WHEN 3 THEN 'Supervisor'
        WHEN 4 THEN 'Mechanic'
        ELSE 'Unknown' END                          AS role_label,
    u."CreatedAt"                                   AS created_at
FROM "AspNetUsers" u;
COMMENT ON VIEW vw_dim_user IS
    'One row per app user. role_label is the integer-to-text decode of the Role enum.';


-- ── vw_dim_machine ──────────────────────────────────────────────────────────
-- One row per machine, with the type label resolved from BOTH the free-text
-- TypeName (admin-entered) and the seeded MachineType enum (1=ADT, ...).
-- TypeName wins if both are present, mirroring the app's TypeDisplay()
-- helper so the dashboard agrees with what operators see on their phones.
DROP VIEW IF EXISTS vw_dim_machine CASCADE;
CREATE VIEW vw_dim_machine AS
SELECT
    m."Id"                                          AS machine_id,
    m."MachineNumber"                               AS machine_number,
    m."MachineName"                                 AS machine_name,
    COALESCE(NULLIF(m."TypeName", ''), CASE m."Type"
        WHEN 1  THEN 'ADT'
        WHEN 2  THEN 'Articulated Water Truck'
        WHEN 3  THEN 'Diesel Bowser'
        WHEN 4  THEN 'Drills'
        WHEN 5  THEN 'Excavator'
        WHEN 6  THEN 'FEL'
        WHEN 7  THEN 'Forklift'
        WHEN 8  THEN 'Grader'
        WHEN 9  THEN 'LDV'
        WHEN 10 THEN 'SRV / Water Bowser'
        WHEN 11 THEN 'Track Dozer'
        WHEN 12 THEN 'RDT'
        WHEN 13 THEN 'Truck Mounted Crane'
        WHEN 14 THEN 'TLB'
        ELSE 'Unknown' END)                         AS type_label,
    m."Type"                                        AS type_code,
    m."Description"                                 AS description,
    m."IsActive"                                    AS is_active,
    m."IsImmobilised"                               AS is_immobilised,
    m."ImmobilisedReason"                           AS immobilised_reason,
    m."AwaitingAdminClearance"                      AS awaiting_admin_clearance,
    m."ClearedAt"                                   AS last_cleared_at,
    m."CreatedAt"                                   AS created_at,
    -- Convenience flag for the executive page's "fleet readiness" tile.
    -- "Ready" means the machine is active, NOT immobilised, and NOT awaiting
    -- admin sign-off after a repair.
    (m."IsActive" AND NOT m."IsImmobilised"
                  AND NOT m."AwaitingAdminClearance") AS is_ready
FROM "Machines" m;
COMMENT ON VIEW vw_dim_machine IS
    'Machine master with resolved type label and a derived is_ready flag.';


-- ── vw_dim_date (rolling 3 years) ───────────────────────────────────────────
-- Date dimension generated in-line so Power BI's time intelligence (MTD,
-- YTD, prior-period) works correctly. Covers 2 years back from today to
-- 1 year forward so future-dated rows (rare but possible) still join.
DROP VIEW IF EXISTS vw_dim_date CASCADE;
CREATE VIEW vw_dim_date AS
SELECT
    d::date                                         AS date,
    EXTRACT(YEAR  FROM d)::int                      AS year,
    EXTRACT(MONTH FROM d)::int                      AS month,
    TO_CHAR(d, 'YYYY-MM')                           AS year_month,
    TO_CHAR(d, 'Mon YYYY')                          AS month_label,
    EXTRACT(DAY   FROM d)::int                      AS day,
    EXTRACT(DOW   FROM d)::int                      AS day_of_week,    -- 0=Sun
    TRIM(TO_CHAR(d, 'Day'))                         AS day_name,
    EXTRACT(WEEK  FROM d)::int                      AS iso_week,
    EXTRACT(QUARTER FROM d)::int                    AS quarter,
    'Q' || EXTRACT(QUARTER FROM d)::int
        || ' ' || EXTRACT(YEAR FROM d)::int         AS quarter_label
FROM GENERATE_SERIES(
        date_trunc('day', CURRENT_DATE - INTERVAL '2 years'),
        date_trunc('day', CURRENT_DATE + INTERVAL '1 year'),
        '1 day'::interval) AS d;
COMMENT ON VIEW vw_dim_date IS
    'Generated date dimension covering 2 years back to 1 year forward. Power BI marks this as the date table.';


-- ════════════════════════════════════════════════════════════════════════════
--  FACT — CHECKLIST SUBMISSIONS
--
--  The headline fact table. One row per submission, denormalised with
--  operator + supervisor + mechanic + machine attributes so 95% of the
--  dashboard visuals can hit this view without a single join.
-- ════════════════════════════════════════════════════════════════════════════
DROP VIEW IF EXISTS vw_fact_submissions CASCADE;
CREATE VIEW vw_fact_submissions AS
SELECT
    s."Id"                                          AS submission_id,
    s."LocalId"                                     AS local_id,
    s."MachineId"                                   AS machine_id,
    m."MachineNumber"                               AS machine_number,
    m."MachineName"                                 AS machine_name,
    COALESCE(NULLIF(m."TypeName", ''), CASE m."Type"
        WHEN 1  THEN 'ADT' WHEN 2 THEN 'Articulated Water Truck'
        WHEN 3  THEN 'Diesel Bowser' WHEN 4  THEN 'Drills'
        WHEN 5  THEN 'Excavator' WHEN 6  THEN 'FEL'
        WHEN 7  THEN 'Forklift' WHEN 8  THEN 'Grader'
        WHEN 9  THEN 'LDV' WHEN 10 THEN 'SRV / Water Bowser'
        WHEN 11 THEN 'Track Dozer' WHEN 12 THEN 'RDT'
        WHEN 13 THEN 'Truck Mounted Crane' WHEN 14 THEN 'TLB'
        ELSE 'Unknown' END)                         AS machine_type_label,
    s."OperatorId"                                  AS operator_id,
    op."FullName"                                   AS operator_name,
    op."EmployeeNumber"                             AS operator_employee_no,
    s."SupervisorId"                                AS supervisor_id,
    sup."FullName"                                  AS supervisor_name,
    s."MechanicId"                                  AS mechanic_id,
    mech."FullName"                                 AS mechanic_name,
    s."Shift"                                       AS shift_code,
    CASE s."Shift"
        WHEN 1 THEN 'Day'
        WHEN 2 THEN 'Afternoon'
        WHEN 3 THEN 'Night'
        ELSE 'Unknown' END                          AS shift_label,
    s."Status"                                      AS status_code,
    CASE s."Status"
        WHEN 0 THEN 'In progress'
        WHEN 1 THEN 'GO'
        WHEN 2 THEN 'GO-BUT 24H'
        WHEN 3 THEN 'GO-BUT 30D'
        WHEN 4 THEN 'NO-GO'
        WHEN 5 THEN 'Rejected'
        ELSE 'Unknown' END                          AS status_label,
    -- Top-level "is this submission compliant" boolean used by the
    -- Compliance % KPI. Anything in {GO, GO-BUT 24H, GO-BUT 30D} counts
    -- as compliant (it passed sign-off rules). NO-GO and Rejected are
    -- non-compliant. InProgress is excluded so unfinished checklists
    -- don't deflate the rate.
    CASE
        WHEN s."Status" IN (1, 2, 3) THEN TRUE
        WHEN s."Status" IN (4, 5)    THEN FALSE
        ELSE NULL END                               AS is_compliant,
    s."KmOrHourMeter"                               AS km_or_hour_meter,
    s."OperatorRemarks"                             AS operator_remarks,
    s."FitnessDeclarationSigned"                    AS fitness_declaration_signed,
    s."SubmittedAt"                                 AS submitted_at,
    s."SubmittedAt"::date                           AS submitted_date,
    s."SupervisorSignedAt"                          AS supervisor_signed_at,
    s."MechanicSignedAt"                            AS mechanic_signed_at,
    -- Sign-off latency in HOURS — how long the supervisor took to respond.
    -- Null while still pending; > 0 once signed. Used by the SHE page's
    -- "average sign-off delay" measure and the "late sign-off" pill.
    CASE WHEN s."SupervisorSignedAt" IS NOT NULL THEN
            EXTRACT(EPOCH FROM (s."SupervisorSignedAt" - s."SubmittedAt"))/3600.0
         ELSE NULL END                              AS supervisor_signoff_hours,
    -- Same idea but for the mechanic close-out timestamp.
    CASE WHEN s."MechanicSignedAt" IS NOT NULL THEN
            EXTRACT(EPOCH FROM (s."MechanicSignedAt" - s."SubmittedAt"))/3600.0
         ELSE NULL END                              AS mechanic_close_hours,
    s."RejectionReason"                             AS rejection_reason,
    s."IsSyncedToCloud"                             AS is_synced_to_cloud,
    -- True iff there's at least one defect on this submission. Cheaper than
    -- counting from SubmissionItems for the executive page tiles.
    EXISTS (SELECT 1 FROM "SubmissionItems" si
            WHERE si."SubmissionId" = s."Id"
              AND si."Status" = 2)                  AS has_defect,
    (SELECT COUNT(*) FROM "SubmissionItems" si
        WHERE si."SubmissionId" = s."Id"
          AND si."Status" = 2)::int                 AS defect_count,
    (SELECT COUNT(*) FROM "SubmissionItems" si)::int AS item_count_total,

    -- Phase 4.6 — Re-check linkage. is_re_check makes dashboard filters
    -- trivial ("show me only first-time submissions" or "show me only
    -- re-checks"). original_submission_id is the FK back to the failed
    -- submission this re-check supersedes — useful for drill-through.
    (s."OriginalSubmissionId" IS NOT NULL)            AS is_re_check,
    s."OriginalSubmissionId"                          AS original_submission_id
FROM "ChecklistSubmissions" s
JOIN "Machines"     m   ON m."Id"  = s."MachineId"
LEFT JOIN "AspNetUsers" op  ON op."Id"  = s."OperatorId"
LEFT JOIN "AspNetUsers" sup ON sup."Id" = s."SupervisorId"
LEFT JOIN "AspNetUsers" mech ON mech."Id" = s."MechanicId";
COMMENT ON VIEW vw_fact_submissions IS
    'One row per checklist submission with operator/supervisor/mechanic/machine attributes pre-joined. Drives most dashboard pages.';


-- ── vw_kpi_first_time_fix (Phase 4.6) ───────────────────────────────────
-- First-time-fix rate: of all NO-GO + GO-BUT submissions that had a
-- subsequent re-check within 24h, how many came back GO on the first
-- re-check? This is the single most important reliability metric a
-- maintenance manager cares about — "when we fix something, does it
-- stay fixed?" Per-machine and per-operator slices supported by the
-- view's columns; aggregate however the dashboard needs.
DROP VIEW IF EXISTS vw_kpi_first_time_fix CASCADE;
CREATE VIEW vw_kpi_first_time_fix AS
SELECT
    orig.machine_id,
    orig.machine_number,
    orig.machine_name,
    orig.operator_id,
    orig.operator_name,
    orig.submitted_date                              AS original_date,
    orig.status_label                                AS original_status,
    recheck.status_label                             AS recheck_status,
    (recheck.status_label = 'GO')                    AS first_time_fix
FROM vw_fact_submissions orig
JOIN vw_fact_submissions recheck
  ON recheck.original_submission_id = orig.submission_id
WHERE orig.status_label IN ('NO-GO', 'GO-BUT 24H', 'GO-BUT 30D');
COMMENT ON VIEW vw_kpi_first_time_fix IS
    'One row per (failed submission, its first re-check) pair. first_time_fix=TRUE means the repair held on the very next pre-shift check.';


-- ════════════════════════════════════════════════════════════════════════════
--  FACT — SUBMISSION LINE ITEMS
--
--  Row per pass/fail item per submission. Powers the "top failing items"
--  visual and the "criticality breakdown" (NO-GO items vs ordinary defects).
-- ════════════════════════════════════════════════════════════════════════════
DROP VIEW IF EXISTS vw_fact_submission_items CASCADE;
CREATE VIEW vw_fact_submission_items AS
SELECT
    si."Id"                                         AS submission_item_id,
    si."SubmissionId"                               AS submission_id,
    s."MachineId"                                   AS machine_id,
    s."OperatorId"                                  AS operator_id,
    s."SubmittedAt"                                 AS submitted_at,
    s."SubmittedAt"::date                           AS submitted_date,
    s."Shift"                                       AS shift_code,
    si."TemplateItemId"                             AS template_item_id,
    ti."ItemName"                                   AS item_name,
    ti."Section"                                    AS section,
    ti."IsNoGoItem"                                 AS is_critical,
    si."Status"                                     AS item_status_code,
    CASE si."Status"
        WHEN 1 THEN 'In order'
        WHEN 2 THEN 'Defect'
        ELSE 'Unknown' END                          AS item_status_label,
    si."Notes"                                      AS notes,
    -- Boolean accelerators for visuals. is_defect drives "defects today".
    -- is_critical_defect drives the SHE page's "critical NO-GO triggers".
    (si."Status" = 2)                               AS is_defect,
    (si."Status" = 2 AND ti."IsNoGoItem")           AS is_critical_defect,
    (si."PhotoData" IS NOT NULL)                    AS has_photo,
    (si."AudioData" IS NOT NULL)                    AS has_audio
FROM "SubmissionItems" si
JOIN "ChecklistSubmissions" s ON s."Id"  = si."SubmissionId"
JOIN "ChecklistTemplateItems" ti ON ti."Id" = si."TemplateItemId";
COMMENT ON VIEW vw_fact_submission_items IS
    'Per-item rows for every submission. is_critical_defect highlights NO-GO triggers.';


-- ════════════════════════════════════════════════════════════════════════════
--  FACT — DEFECT ORDERS
--
--  Maintenance lifecycle: one row per work order created when a defect needs
--  parts / mechanic attention. Powers the Maintenance page.
-- ════════════════════════════════════════════════════════════════════════════
DROP VIEW IF EXISTS vw_fact_defect_orders CASCADE;
CREATE VIEW vw_fact_defect_orders AS
SELECT
    d."Id"                                          AS defect_order_id,
    d."SubmissionId"                                AS submission_id,
    d."SubmissionItemId"                            AS submission_item_id,
    s."MachineId"                                   AS machine_id,
    m."MachineNumber"                               AS machine_number,
    m."MachineName"                                 AS machine_name,
    s."OperatorId"                                  AS reporting_operator_id,
    op."FullName"                                   AS reporting_operator_name,
    d."AssignedMechanicId"                          AS mechanic_id,
    mech."FullName"                                 AS mechanic_name,
    d."DefectDescription"                           AS defect_description,
    d."PartRequired"                                AS part_required,
    d."PartNumber"                                  AS part_number,
    d."RepairStatus"                                AS repair_status_code,
    CASE d."RepairStatus"
        WHEN 0 THEN 'Pending'
        WHEN 1 THEN 'In progress'
        WHEN 2 THEN 'Awaiting parts'
        WHEN 3 THEN 'Completed'
        ELSE 'Unknown' END                          AS repair_status_label,
    d."CreatedAt"                                   AS created_at,
    d."CreatedAt"::date                             AS created_date,
    d."ResolvedAt"                                  AS resolved_at,
    d."ResolutionNotes"                             AS resolution_notes,
    -- Time-to-resolve in hours. Null for still-open defects.
    CASE WHEN d."ResolvedAt" IS NOT NULL THEN
            EXTRACT(EPOCH FROM (d."ResolvedAt" - d."CreatedAt"))/3600.0
         ELSE NULL END                              AS time_to_resolve_hours,
    -- Age in hours of OPEN defects (so the SHE page can show "oldest defect:
    -- 7 days"). Null for closed defects so we don't double-count.
    CASE WHEN d."ResolvedAt" IS NULL THEN
            EXTRACT(EPOCH FROM (NOW() - d."CreatedAt"))/3600.0
         ELSE NULL END                              AS age_hours,
    (d."ResolvedAt" IS NULL)                        AS is_open,
    (d."ResolvedAt" IS NULL
        AND d."AssignedMechanicId" IS NULL)         AS is_unassigned,
    (d."MechanicSignature" IS NOT NULL)             AS has_mechanic_signature,

    -- ── Phase 3 workflow columns ─────────────────────────────────────────
    -- Each timestamp is set as the defect crosses the corresponding stage.
    -- workflow_stage is a derived enum-text so dashboards don't have to
    -- re-compute it. Reads top-to-bottom in the order the work happens.
    d."PlannerCapturedAt"                           AS planner_captured_at,
    d."PlannerCapturedById"                         AS planner_captured_by_id,
    planner."FullName"                              AS planner_captured_by_name,
    d."JobCardNumber"                               AS jobcard_number,
    d."DispatchedAt"                                AS dispatched_at,
    d."DispatchedById"                              AS dispatched_by_id,
    dispatcher."FullName"                           AS dispatched_by_name,
    (CASE
        WHEN d."ResolvedAt"        IS NOT NULL THEN 'Resolved'
        WHEN d."DispatchedAt"      IS NOT NULL THEN 'Artisan working'
        WHEN d."PlannerCapturedAt" IS NOT NULL THEN 'Awaiting dispatch'
        ELSE                                            'Awaiting planner'
     END)                                           AS workflow_stage,

    -- Stage-to-stage latency in hours. Null for the stages a defect hasn't
    -- reached yet. Useful for "how long did the Planner sit on this?".
    CASE WHEN d."PlannerCapturedAt" IS NOT NULL THEN
            EXTRACT(EPOCH FROM (d."PlannerCapturedAt" - d."CreatedAt"))/3600.0
         ELSE NULL END                              AS hours_in_planner_queue,
    CASE WHEN d."DispatchedAt" IS NOT NULL AND d."PlannerCapturedAt" IS NOT NULL THEN
            EXTRACT(EPOCH FROM (d."DispatchedAt" - d."PlannerCapturedAt"))/3600.0
         ELSE NULL END                              AS hours_in_dispatch_queue,
    CASE WHEN d."ResolvedAt" IS NOT NULL AND d."DispatchedAt" IS NOT NULL THEN
            EXTRACT(EPOCH FROM (d."ResolvedAt" - d."DispatchedAt"))/3600.0
         ELSE NULL END                              AS hours_with_artisan
FROM "DefectOrders" d
JOIN "ChecklistSubmissions" s ON s."Id"  = d."SubmissionId"
JOIN "Machines"     m   ON m."Id"  = s."MachineId"
LEFT JOIN "AspNetUsers" op         ON op."Id"         = s."OperatorId"
LEFT JOIN "AspNetUsers" mech       ON mech."Id"       = d."AssignedMechanicId"
LEFT JOIN "AspNetUsers" planner    ON planner."Id"    = d."PlannerCapturedById"
LEFT JOIN "AspNetUsers" dispatcher ON dispatcher."Id" = d."DispatchedById";
COMMENT ON VIEW vw_fact_defect_orders IS
    'One row per defect order with workflow-stage, latency, and resolution metrics.';


-- ── vw_kpi_planner_queue (Phase 3) ──────────────────────────────────────
-- Snapshot of the Planner inbox. Drives the SHE / planning dashboards'
-- "defects waiting capture" KPI plus the median-age-in-queue metric.
DROP VIEW IF EXISTS vw_kpi_planner_queue CASCADE;
CREATE VIEW vw_kpi_planner_queue AS
SELECT
    COUNT(*)                                          AS pending_count,
    COUNT(*) FILTER (WHERE EXTRACT(EPOCH FROM (NOW() - "CreatedAt"))/3600.0 > 24)
                                                      AS pending_over_24h,
    PERCENTILE_CONT(0.5) WITHIN GROUP (
        ORDER BY EXTRACT(EPOCH FROM (NOW() - "CreatedAt"))/3600.0)
                                                      AS median_hours_waiting,
    MIN("CreatedAt")                                  AS oldest_pending_at
FROM "DefectOrders"
WHERE "PlannerCapturedAt" IS NULL
  AND "RepairStatus" <> 3;   -- not Completed
COMMENT ON VIEW vw_kpi_planner_queue IS
    'Headline numbers for the Planner queue — pending count, aged-over-24h, median wait, oldest.';


-- ── vw_kpi_dispatch_queue (Phase 3) ─────────────────────────────────────
-- Same shape as vw_kpi_planner_queue but for the Control Room dispatch
-- queue. Threshold is 4 hours (vs Planner's 24h) — dispatch should be
-- much faster because the jobcard is already approved and ready to work.
DROP VIEW IF EXISTS vw_kpi_dispatch_queue CASCADE;
CREATE VIEW vw_kpi_dispatch_queue AS
SELECT
    COUNT(*)                                          AS pending_count,
    COUNT(*) FILTER (WHERE EXTRACT(EPOCH FROM (NOW() - "PlannerCapturedAt"))/3600.0 > 4)
                                                      AS pending_over_4h,
    PERCENTILE_CONT(0.5) WITHIN GROUP (
        ORDER BY EXTRACT(EPOCH FROM (NOW() - "PlannerCapturedAt"))/3600.0)
                                                      AS median_hours_waiting,
    MIN("PlannerCapturedAt")                          AS oldest_pending_at
FROM "DefectOrders"
WHERE "PlannerCapturedAt" IS NOT NULL
  AND "DispatchedAt"      IS NULL
  AND "RepairStatus"      <> 3;
COMMENT ON VIEW vw_kpi_dispatch_queue IS
    'Headline numbers for the Control Room dispatch queue.';


-- ════════════════════════════════════════════════════════════════════════════
--  FACT — AUDIT EVENTS
--
--  Append-only compliance trail. Powers the SHE / DMR reporting page.
-- ════════════════════════════════════════════════════════════════════════════
DROP VIEW IF EXISTS vw_fact_audit CASCADE;
CREATE VIEW vw_fact_audit AS
SELECT
    a."Id"                                          AS audit_id,
    a."ActorUserId"                                 AS actor_user_id,
    a."ActorName"                                   AS actor_name,
    a."ActorEmail"                                  AS actor_email,
    a."ActorRole"                                   AS actor_role,
    a."Action"                                      AS action_code,
    a."TargetType"                                  AS target_type,
    a."TargetId"                                    AS target_id,
    a."OccurredAtClient"                            AS occurred_at_client,
    a."OccurredAtServer"                            AS occurred_at_server,
    a."OccurredAtServer"::date                      AS occurred_date,
    a."DeviceKind"                                  AS device_kind,
    a."IpAddress"                                   AS ip_address,
    -- True for actions that should appear in MHSA reporting (sign-offs,
    -- rejections, NO-GO submissions, machine clearances).
    (a."Action" IN (
        'submission.signoff',
        'submission.reject',
        'submission.nogo',
        'defect.complete',
        'machine.clearance.grant',
        'machine.clearance.deny'))                  AS is_safety_critical
FROM "AuditEvents" a;
COMMENT ON VIEW vw_fact_audit IS
    'Audit-trail fact. is_safety_critical flags the actions the SHE officer cares about.';


-- ════════════════════════════════════════════════════════════════════════════
--  FACT — NOTIFICATIONS
--
--  Useful for "communication noise" analysis — how many supervisor pings
--  per day, what proportion are read promptly, etc.
-- ════════════════════════════════════════════════════════════════════════════
DROP VIEW IF EXISTS vw_fact_notifications CASCADE;
CREATE VIEW vw_fact_notifications AS
SELECT
    n."Id"                                          AS notification_id,
    n."UserId"                                      AS recipient_user_id,
    u."FullName"                                    AS recipient_name,
    n."Kind"                                        AS kind,
    n."Title"                                       AS title,
    n."CreatedAt"                                   AS created_at,
    n."ReadAt"                                      AS read_at,
    (n."ReadAt" IS NOT NULL)                        AS is_read,
    CASE WHEN n."ReadAt" IS NOT NULL THEN
            EXTRACT(EPOCH FROM (n."ReadAt" - n."CreatedAt"))/60.0
         ELSE NULL END                              AS read_latency_minutes
FROM "Notifications" n
LEFT JOIN "AspNetUsers" u ON u."Id" = n."UserId";
COMMENT ON VIEW vw_fact_notifications IS
    'Notification deliveries with read-acknowledgement latency.';


-- ════════════════════════════════════════════════════════════════════════════
--  KPI VIEWS — single-row aggregates for executive cards
--
--  These return one row each and are mapped to KPI / card visuals on the
--  Executive Overview page. Power BI cards can also pull from measures
--  defined in the data model, but materialising them as DB views means the
--  cardpage stays fast even without DAX, AND the same numbers can be
--  consumed by other clients (mobile app, email digests).
-- ════════════════════════════════════════════════════════════════════════════

-- ── vw_kpi_today ────────────────────────────────────────────────────────────
-- The "today" snapshot the mine manager glances at first thing in the
-- morning. All counts are scoped to the current local date in UTC; if your
-- mine runs across timezones, swap CURRENT_DATE for a tz-aware expression.
DROP VIEW IF EXISTS vw_kpi_today CASCADE;
CREATE VIEW vw_kpi_today AS
SELECT
    (SELECT COUNT(*) FROM vw_fact_submissions
        WHERE submitted_date = CURRENT_DATE)::int           AS submissions_today,
    (SELECT COUNT(*) FROM vw_fact_submissions
        WHERE submitted_date = CURRENT_DATE
          AND status_label = 'GO')::int                     AS go_today,
    (SELECT COUNT(*) FROM vw_fact_submissions
        WHERE submitted_date = CURRENT_DATE
          AND status_label IN ('GO-BUT 24H','GO-BUT 30D'))::int  AS gobut_today,
    (SELECT COUNT(*) FROM vw_fact_submissions
        WHERE submitted_date = CURRENT_DATE
          AND status_label = 'NO-GO')::int                  AS nogo_today,
    (SELECT COUNT(*) FROM vw_fact_defect_orders
        WHERE is_open = TRUE)::int                          AS open_defects,
    (SELECT COUNT(*) FROM vw_dim_machine
        WHERE is_immobilised = TRUE)::int                   AS immobilised_machines,
    (SELECT COUNT(*) FROM vw_dim_machine
        WHERE awaiting_admin_clearance = TRUE)::int         AS awaiting_clearance,
    (SELECT COUNT(*) FROM vw_dim_machine
        WHERE is_ready = TRUE)::int                         AS ready_machines,
    (SELECT COUNT(*) FROM vw_dim_machine)::int              AS total_machines;
COMMENT ON VIEW vw_kpi_today IS
    'Single-row "right now" KPI snapshot for the executive header cards.';


-- ── vw_kpi_compliance_30d ───────────────────────────────────────────────────
-- Rolling-30-day compliance %. Excludes InProgress submissions because they
-- haven't reached a sign-off state yet — counting them as failures would
-- punish the mine for late-night submissions that supervisors haven't got
-- to yet.
DROP VIEW IF EXISTS vw_kpi_compliance_30d CASCADE;
CREATE VIEW vw_kpi_compliance_30d AS
SELECT
    COUNT(*) FILTER (WHERE is_compliant IS NOT NULL)::int        AS finalised,
    COUNT(*) FILTER (WHERE is_compliant = TRUE)::int             AS compliant,
    COUNT(*) FILTER (WHERE is_compliant = FALSE)::int            AS non_compliant,
    -- Percentage as a real, not int — Power BI cards display 0–1 fractions
    -- with a "%" format string. Returns NULL when no submissions exist yet
    -- so the card displays "—" instead of "NaN".
    CASE WHEN COUNT(*) FILTER (WHERE is_compliant IS NOT NULL) = 0 THEN NULL
         ELSE COUNT(*) FILTER (WHERE is_compliant = TRUE)::numeric
              / COUNT(*) FILTER (WHERE is_compliant IS NOT NULL)::numeric
    END                                                          AS compliance_rate
FROM vw_fact_submissions
WHERE submitted_date >= CURRENT_DATE - INTERVAL '30 days';
COMMENT ON VIEW vw_kpi_compliance_30d IS
    'Rolling 30-day compliance rate (GO + GO-BUT divided by all finalised submissions).';


-- ── vw_kpi_signoff_30d ──────────────────────────────────────────────────────
-- How long supervisors are taking to sign off GO-BUT submissions over the
-- last 30 days. The SHE officer's target is usually <2h for GO-BUT 24H
-- (per MHSA section 5.1 best practice).
DROP VIEW IF EXISTS vw_kpi_signoff_30d CASCADE;
CREATE VIEW vw_kpi_signoff_30d AS
SELECT
    COUNT(*) FILTER (WHERE supervisor_signoff_hours IS NOT NULL)::int  AS signed_count,
    AVG(supervisor_signoff_hours)                                       AS avg_signoff_hours,
    PERCENTILE_CONT(0.5)  WITHIN GROUP (ORDER BY supervisor_signoff_hours)
                                                                        AS median_signoff_hours,
    PERCENTILE_CONT(0.95) WITHIN GROUP (ORDER BY supervisor_signoff_hours)
                                                                        AS p95_signoff_hours,
    COUNT(*) FILTER (WHERE supervisor_signoff_hours > 2)::int           AS over_2h_count,
    COUNT(*) FILTER (WHERE supervisor_signoff_hours > 24)::int          AS over_24h_count
FROM vw_fact_submissions
WHERE submitted_date >= CURRENT_DATE - INTERVAL '30 days'
  AND status_label IN ('GO-BUT 24H','GO-BUT 30D');
COMMENT ON VIEW vw_kpi_signoff_30d IS
    'Sign-off latency distribution over the rolling 30 days.';


-- ── vw_kpi_top_problem_machines ─────────────────────────────────────────────
-- Top 10 machines by defect count over the last 90 days. Drives the
-- maintenance page's "where to focus" table.
DROP VIEW IF EXISTS vw_kpi_top_problem_machines CASCADE;
CREATE VIEW vw_kpi_top_problem_machines AS
SELECT
    machine_id,
    machine_number,
    machine_name,
    machine_type_label,
    COUNT(*)::int                                       AS submissions_90d,
    COUNT(*) FILTER (WHERE has_defect)::int             AS submissions_with_defects,
    SUM(defect_count)::int                              AS total_defects,
    COUNT(*) FILTER (WHERE status_label = 'NO-GO')::int AS nogo_submissions,
    CASE WHEN COUNT(*) = 0 THEN NULL
         ELSE COUNT(*) FILTER (WHERE has_defect)::numeric / COUNT(*)::numeric
    END                                                 AS defect_rate
FROM vw_fact_submissions
WHERE submitted_date >= CURRENT_DATE - INTERVAL '90 days'
GROUP BY machine_id, machine_number, machine_name, machine_type_label
ORDER BY total_defects DESC, nogo_submissions DESC
LIMIT 10;
COMMENT ON VIEW vw_kpi_top_problem_machines IS
    'Top 10 machines by 90-day defect volume. The maintenance page''s "focus list".';


-- ── vw_kpi_top_failing_items ────────────────────────────────────────────────
-- Across all checklists, which item names fail most often? Tells the SHE
-- officer where to retrain operators or which item the engineering team
-- should redesign.
DROP VIEW IF EXISTS vw_kpi_top_failing_items CASCADE;
CREATE VIEW vw_kpi_top_failing_items AS
SELECT
    item_name,
    section,
    is_critical,
    COUNT(*) FILTER (WHERE is_defect)::int          AS defect_occurrences,
    COUNT(*) FILTER (WHERE is_critical_defect)::int AS critical_occurrences,
    COUNT(*)::int                                   AS total_inspections,
    CASE WHEN COUNT(*) = 0 THEN NULL
         ELSE COUNT(*) FILTER (WHERE is_defect)::numeric / COUNT(*)::numeric
    END                                             AS failure_rate
FROM vw_fact_submission_items
WHERE submitted_date >= CURRENT_DATE - INTERVAL '90 days'
GROUP BY item_name, section, is_critical
HAVING COUNT(*) FILTER (WHERE is_defect) > 0
ORDER BY defect_occurrences DESC
LIMIT 25;
COMMENT ON VIEW vw_kpi_top_failing_items IS
    'Top 25 most-failed checklist items in the last 90 days, including critical flag.';


-- ── vw_kpi_mechanic_workload ────────────────────────────────────────────────
-- Per-mechanic snapshot of what's on their plate and how fast they close
-- defects. Drives the Maintenance page's mechanic comparison table.
DROP VIEW IF EXISTS vw_kpi_mechanic_workload CASCADE;
CREATE VIEW vw_kpi_mechanic_workload AS
SELECT
    mech.user_id                                    AS mechanic_id,
    mech.full_name                                  AS mechanic_name,
    mech.employee_number                            AS employee_number,
    COUNT(d.*) FILTER (WHERE d.is_open)::int        AS open_orders,
    COUNT(d.*) FILTER (WHERE d.is_open
                       AND d.repair_status_label = 'Awaiting parts')::int  AS awaiting_parts,
    COUNT(d.*) FILTER (WHERE NOT d.is_open
                       AND d.resolved_at >= CURRENT_DATE - INTERVAL '30 days')::int
                                                    AS completed_30d,
    AVG(d.time_to_resolve_hours) FILTER (WHERE NOT d.is_open
                       AND d.resolved_at >= CURRENT_DATE - INTERVAL '90 days')
                                                    AS avg_resolve_hours_90d,
    MAX(d.age_hours) FILTER (WHERE d.is_open)       AS oldest_open_age_hours
FROM vw_dim_user mech
LEFT JOIN vw_fact_defect_orders d ON d.mechanic_id = mech.user_id
WHERE mech.role_label = 'Mechanic' AND mech.is_active
GROUP BY mech.user_id, mech.full_name, mech.employee_number
ORDER BY open_orders DESC;
COMMENT ON VIEW vw_kpi_mechanic_workload IS
    'Per-mechanic workload snapshot: open orders, awaiting parts, 30-day completed, avg resolve time.';


-- ── vw_kpi_operator_activity ────────────────────────────────────────────────
-- Per-operator output and defect-detection rate. Lets the supervisor see
-- which operators are conscientious (high defect-detection rate) vs which
-- might be rubber-stamping.
DROP VIEW IF EXISTS vw_kpi_operator_activity CASCADE;
CREATE VIEW vw_kpi_operator_activity AS
SELECT
    op.user_id                                       AS operator_id,
    op.full_name                                     AS operator_name,
    op.employee_number                               AS employee_number,
    COUNT(s.*)::int                                  AS submissions_30d,
    COUNT(s.*) FILTER (WHERE s.has_defect)::int      AS submissions_with_defects_30d,
    SUM(s.defect_count)::int                         AS defects_flagged_30d,
    COUNT(s.*) FILTER (WHERE s.status_label = 'NO-GO')::int  AS nogo_30d,
    CASE WHEN COUNT(s.*) = 0 THEN NULL
         ELSE COUNT(s.*) FILTER (WHERE s.has_defect)::numeric / COUNT(s.*)::numeric
    END                                              AS defect_detection_rate,
    MAX(s.submitted_at)                              AS last_submitted_at
FROM vw_dim_user op
LEFT JOIN vw_fact_submissions s
       ON s.operator_id = op.user_id
      AND s.submitted_date >= CURRENT_DATE - INTERVAL '30 days'
WHERE op.role_label = 'Operator' AND op.is_active
GROUP BY op.user_id, op.full_name, op.employee_number
ORDER BY submissions_30d DESC;
COMMENT ON VIEW vw_kpi_operator_activity IS
    'Per-operator 30-day activity summary including defect-detection rate.';


-- ════════════════════════════════════════════════════════════════════════════
--  COMPETENCY + CONFIG-CHANGE VIEWS (MHSA Section 22(a) round)
--
--  Added when the operator-competency module shipped. These power a new
--  SHE-officer page in the Power BI report that lists every operator's
--  current licence status, every renewal that's due in the next 30 days,
--  and every system-level configuration change captured by the audit
--  trail. Together they answer the inspector's two favourite questions:
--    1. "Show me how you stop someone running a haul truck after their
--        Code EC dropped off."
--    2. "Show me every change to safety-critical configuration with the
--        actor and timestamp."
-- ════════════════════════════════════════════════════════════════════════════


-- ── vw_fact_competencies ────────────────────────────────────────────────────
-- Every competency row, alive or revoked, with derived status bands. Joined
-- to the operator dimension so the Power BI page can slice by full name,
-- employee number, role, or supervisor without re-querying. The status
-- column is what drives the green/amber/red colouring in the renewal
-- queue visual — kept here so every consumer paints with the same brush.
DROP VIEW IF EXISTS vw_fact_competencies CASCADE;
CREATE VIEW vw_fact_competencies AS
SELECT
    c."Id"                                          AS competency_id,
    c."OperatorId"                                  AS operator_id,
    u."FullName"                                    AS operator_name,
    u."EmployeeNumber"                              AS employee_number,
    c."MachineType"                                 AS machine_type_code,
    (CASE c."MachineType"
        WHEN 1  THEN 'ADT'                  WHEN 2  THEN 'Articulated Water Truck'
        WHEN 3  THEN 'Diesel Bowser'        WHEN 4  THEN 'Drills'
        WHEN 5  THEN 'Excavator'            WHEN 6  THEN 'FEL'
        WHEN 7  THEN 'Forklift'             WHEN 8  THEN 'Grader'
        WHEN 9  THEN 'LDV'                  WHEN 10 THEN 'SRV / Water Bowser'
        WHEN 11 THEN 'Track Dozer'          WHEN 12 THEN 'RDT'
        WHEN 13 THEN 'Truck Mounted Crane'  WHEN 14 THEN 'TLB'
        ELSE 'Unknown' END)                         AS machine_type_label,
    c."CertificateNumber"                           AS certificate_number,
    c."IssuedBy"                                    AS issued_by,
    c."IssuedAt"                                    AS issued_at,
    c."ExpiresAt"                                   AS expires_at,
    c."IsActive"                                    AS is_active,
    c."RevokedAt"                                   AS revoked_at,
    c."RevocationReason"                            AS revocation_reason,
    -- Days remaining until expiry. Negative when already expired.
    EXTRACT(DAY FROM (c."ExpiresAt" - NOW()))::int  AS days_to_expiry,
    -- Status bucket — the single column every competency visual reads.
    -- Revoked beats expired beats expiring; "Valid" is the safe state.
    (CASE
        WHEN NOT c."IsActive"                                   THEN 'Revoked'
        WHEN c."ExpiresAt" < NOW()                              THEN 'Expired'
        WHEN c."ExpiresAt" < NOW() + INTERVAL '7  days'         THEN 'Expiring 7d'
        WHEN c."ExpiresAt" < NOW() + INTERVAL '30 days'         THEN 'Expiring 30d'
        ELSE 'Valid'
     END)                                           AS status,
    c."CreatedAt"                                   AS created_at
FROM "OperatorCompetencies" c
LEFT JOIN "AspNetUsers" u ON u."Id" = c."OperatorId";
COMMENT ON VIEW vw_fact_competencies IS
    'Per-competency row with derived days_to_expiry and status bucket.';


-- ── vw_kpi_competency_health ────────────────────────────────────────────────
-- Single-row KPI summary feeding the SHE-page header tiles. Each operator
-- can have many competencies; we count the WORST status per operator so
-- one expired licence flags the whole person red regardless of how many
-- other valid ones they hold. This is intentional: it matches how an
-- inspector audits — they ask "who is non-compliant TODAY", not "who has
-- the most certificates".
DROP VIEW IF EXISTS vw_kpi_competency_health CASCADE;
CREATE VIEW vw_kpi_competency_health AS
WITH worst_per_operator AS (
    SELECT
        operator_id,
        MIN(CASE status
                WHEN 'Revoked'       THEN 1
                WHEN 'Expired'       THEN 2
                WHEN 'Expiring 7d'   THEN 3
                WHEN 'Expiring 30d'  THEN 4
                WHEN 'Valid'         THEN 5
                ELSE 6 END)                     AS worst_rank
    FROM vw_fact_competencies
    WHERE operator_id IS NOT NULL
    GROUP BY operator_id
)
SELECT
    COUNT(*)                                                          AS operators_with_any_competency,
    COUNT(*) FILTER (WHERE worst_rank = 5)                            AS operators_fully_valid,
    COUNT(*) FILTER (WHERE worst_rank = 4)                            AS operators_expiring_30d,
    COUNT(*) FILTER (WHERE worst_rank = 3)                            AS operators_expiring_7d,
    COUNT(*) FILTER (WHERE worst_rank = 2)                            AS operators_expired,
    COUNT(*) FILTER (WHERE worst_rank = 1)                            AS operators_revoked,
    -- Compliance % = fully valid OR expiring-30d still counts as compliant
    -- for the headline number (they're allowed to operate); expired and
    -- revoked drop the score.
    CASE WHEN COUNT(*) = 0 THEN NULL ELSE
        ROUND(100.0 * COUNT(*) FILTER (WHERE worst_rank >= 4)
                    / COUNT(*), 1)
    END                                                               AS compliance_pct
FROM worst_per_operator;
COMMENT ON VIEW vw_kpi_competency_health IS
    'Headline compliance %, with operator counts in each status bucket.';


-- ── vw_kpi_competency_renewal_queue ─────────────────────────────────────────
-- Action list. Every active competency expiring inside the next 30 days
-- ordered earliest-first, plus already-expired ones at the top. This is
-- what the SHE-officer table reads, and it's also exactly what the daily
-- email worker iterates. Keeping the SQL identical between the two
-- consumers means a renewal that's pending in the dashboard is the same
-- row that nudged the operator's supervisor that morning.
DROP VIEW IF EXISTS vw_kpi_competency_renewal_queue CASCADE;
CREATE VIEW vw_kpi_competency_renewal_queue AS
SELECT
    competency_id,
    operator_id,
    operator_name,
    employee_number,
    machine_type_label,
    certificate_number,
    issued_by,
    issued_at,
    expires_at,
    days_to_expiry,
    status
FROM vw_fact_competencies
WHERE is_active
  AND status IN ('Expired', 'Expiring 7d', 'Expiring 30d')
ORDER BY
    CASE status WHEN 'Expired' THEN 1
                WHEN 'Expiring 7d' THEN 2
                WHEN 'Expiring 30d' THEN 3
                ELSE 9 END,
    expires_at ASC;
COMMENT ON VIEW vw_kpi_competency_renewal_queue IS
    'Action list — every active competency expiring within 30 days, oldest first.';


-- ── vw_kpi_competency_by_machine_type ───────────────────────────────────────
-- Donut-by-fleet. Per machine type (Haul Truck, FEL, Grader…) count how
-- many active operators are currently certified on it, how many are
-- approaching expiry, and how many have lapsed. Tells the planner at a
-- glance "we have 4 FEL operators expiring this month — start the
-- renewal pipeline NOW or we're down to two by quarter end".
DROP VIEW IF EXISTS vw_kpi_competency_by_machine_type CASCADE;
CREATE VIEW vw_kpi_competency_by_machine_type AS
SELECT
    machine_type_code,
    machine_type_label,
    COUNT(*) FILTER (WHERE is_active)                                 AS active_certifications,
    COUNT(*) FILTER (WHERE is_active AND status = 'Valid')            AS valid_count,
    COUNT(*) FILTER (WHERE is_active AND status = 'Expiring 30d')     AS expiring_30d_count,
    COUNT(*) FILTER (WHERE is_active AND status = 'Expiring 7d')      AS expiring_7d_count,
    COUNT(*) FILTER (WHERE is_active AND status = 'Expired')          AS expired_count,
    COUNT(*) FILTER (WHERE NOT is_active)                             AS revoked_count
FROM vw_fact_competencies
GROUP BY machine_type_code, machine_type_label
ORDER BY active_certifications DESC, machine_type_label;
COMMENT ON VIEW vw_kpi_competency_by_machine_type IS
    'Per-machine-type counts of currently-valid + expiring + expired certifications.';


-- ── vw_fact_settings_changes ────────────────────────────────────────────────
-- Configuration audit trail. Pulled from the existing audit table by
-- filtering on the settings-related action codes. Inspectors want a
-- "who changed what, when" report for any safety-relevant configuration
-- (SHE officer email, manager email, expiry-reminder cadence, allowed
-- devices, etc.). Keeping this as a view rather than a separate audit
-- stream means every existing audit pipeline (export, redaction,
-- retention) already covers it.
DROP VIEW IF EXISTS vw_fact_settings_changes CASCADE;
CREATE VIEW vw_fact_settings_changes AS
SELECT
    a."Id"                                          AS audit_id,
    a."OccurredAtServer"                            AS occurred_at,
    a."OccurredAtServer"::date                      AS occurred_date,
    a."ActorUserId"                                 AS actor_user_id,
    a."ActorName"                                   AS actor_name,
    a."ActorEmail"                                  AS actor_email,
    a."ActorRole"                                   AS actor_role,
    a."Action"                                      AS action_code,
    -- Human-readable category for slicers and legends. Maps each constant
    -- to the policy domain a SHE officer would group it under.
    (CASE
        WHEN a."Action" LIKE 'settings.%'    THEN 'Configuration'
        WHEN a."Action" LIKE 'admin.%'       THEN 'Admin policy'
        WHEN a."Action" LIKE 'user.%'        THEN 'User access'
        WHEN a."Action" LIKE 'competency.%'  THEN 'Competency'
        ELSE 'Other' END)                           AS action_category,
    a."TargetType"                                  AS target_type,
    a."TargetId"                                    AS target_id,
    a."PayloadJson"                                 AS payload_json,
    a."DeviceKind"                                  AS device_kind,
    a."IpAddress"                                   AS ip_address
FROM "AuditEvents" a
WHERE a."Action" IN (
    'settings.changed',
    'settings.reset',
    'admin.created',
    'admin.deactivated',
    'user.deactivated',
    'user.reactivated',
    'user.password_reset',
    'competency.added',
    'competency.revoked',
    'competency.renewed'
)
ORDER BY a."OccurredAtServer" DESC;
COMMENT ON VIEW vw_fact_settings_changes IS
    'Filtered audit trail covering every system-configuration and admin-policy change.';
