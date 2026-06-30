# Equipment Pre-Use Checklist System

## What it is

The Equipment Pre-Use Checklist System is a digital replacement for the paper checklists that mine operators have historically filled in before starting a shift. It runs at Belfast Coal Mine and is built by Enaleni Engineering and Technology. It covers the full lifecycle of a defect from the moment an operator notices something wrong, through supervisor sign-off, jobcard capture, dispatch to a workshop technician, the repair itself, admin clearance, and the operator's re-check before the machine returns to production.

The system is mobile-first because that's where the work happens. Operators carry rugged Android tablets (Samsung Galaxy Tab Active 5) into the pit. Supervisors and admins use a mix of tablets and office PCs running the same web app. A small number of Windows tablets (Surface Pro 11) cover dispatch and control-room workstations. Every screen is designed to work offline — the operator can complete a full check, save photos and voice memos, sign with their finger, and submit the result without a network connection; the submission queues locally and syncs the moment connectivity returns.

The system enforces three legally relevant outcomes that paper could not. First, when an operator raises a NO-GO, the machine is software-immobilised immediately — the database flags it, the operator's next check is blocked, and a Phase 7.3 integration can lock the physical key in the Key Control cabinet so even a determined operator cannot drive it away. Second, every submission, sign-off, dispatch, repair, and clearance writes a row to a tamper-evident audit log with a cryptographic hash chain that satisfies DMR Regulation 10.3. Third, operator competency is tracked per machine type with expiry-based gating — an operator with a lapsed Class EC certificate cannot pass a pre-use check on a haul truck, full stop.

## The three surfaces

The system has three places users go to interact with it. **The mobile app** (MAUI Blazor Hybrid) is where operators, supervisors, and artisans spend most of their time. It runs on Android tablets in the field and Windows tablets at workshop benches, and works fully offline with automatic background sync. **The web app** (ASP.NET Core MVC) is where admins, planners, and control-room dispatchers work, and where managers go to view reports. **The Power BI dashboards** are eight-page management reports backed by twenty-five SQL views on the production database. They cover executive overview, safety and compliance, supervisor workload, maintenance trends, operator competency, configuration audit, planner queue throughput, and control room dispatch with first-time-fix metrics.

## The six roles

The system has six roles. Each one owns a specific stage of the workflow and only sees the screens relevant to their stage. Role assignment happens on the Admin Employees page, where the role dropdown lets you create any of the six.

### Operator

The operator is the person who actually drives or operates the machine. At Belfast Coal that includes haul truck drivers, dozer operators, grader operators, drill operators, water truck drivers, light-vehicle drivers, and anyone else who climbs into a cab. Each operator is assigned a tablet that stays with them through their shift.

Their daily work is the pre-use checklist. They open the mobile app, pick the machine they're about to use from their assigned list, and tap through every line of the checklist — brakes, lights, belts, fluid levels, tires, emergency stops, and machine-specific items. Each line is GO (the item is fine) or DEFECT (something is wrong). Defects get a description, optional photo, and optional voice memo. When they finish, they sign with a finger on the screen and submit.

The system calculates one of four statuses from the answers. **GO** means the machine is in order and ready to use. **GO-BUT 24H** means there's a minor defect the operator can work around but it needs repair within 24 hours. **GO-BUT 30D** means the defect can wait until next service. **NO-GO** means a critical item (brakes, seat belt, fire extinguisher, or anything tagged as a no-go item in the template) failed — the machine immobilises immediately and the operator cannot use it.

After a NO-GO is repaired and an admin clears the machine back to service, the operator gets a push notification and an "Awaiting your re-check" tile appears on their mobile dashboard. They have to re-do the checklist on that machine before anyone can use it again. The re-check is linked to the original NO-GO submission so the workshop can trace whether the fix actually held.

When the device has no network at all (deep in a pit cut, between WiFi access points), the submission is saved locally and the loud alert mode kicks in on critical defects — siren, screen flash, vibration — so anyone within hearing distance knows something is wrong. A peer broadcast over Bluetooth LE simultaneously tells nearby Equipment Checklist tablets the same thing, so a supervisor a hundred metres away gets a haptic alert and a popup even without a server connection.

### Supervisor

The supervisor's name in the spec is Eugene. He's the shift leader responsible for a team of operators. The system tracks his team via OperatorSupervisorAssignments — only his direct reports' submissions land in his queue.

His core job is review and approval. Every submission from his team requires his explicit acknowledgment before it flows downstream to the planner. **Clean GO** submissions go into his Quick Approve lane on the Supervisor page — he scans the list, sees the operator and machine for each row, and clicks Approve (no signature required because clean GO carries no risk transfer). At end of shift he can bulk-approve everything pending with a single button. **GO-BUT submissions** require more thought — the operator is asking to continue operating with a known defect, so Eugene reviews the defect description, decides whether to accept the risk (signs off with his finger and commits to the 24-hour or 30-day repair window) or reject it (machine immobilises, defect routes to a mechanic for immediate repair). **NO-GOs** route around him to the Planner directly because the system has already locked the machine — there's nothing for him to decide. He still gets an "awareness only" notification so he knows what's happening on his team.

### Planner

The planner is a new role added in Phase 3. His name in the spec is Walter, and he holds the Plant Maintenance Planner seat. Before this role existed, defects went straight from supervisor sign-off to whoever was nearby in the workshop — there was no formal SAP jobcard capture step, no audit trail of which defect became which work order, and no visibility into how long defects sat unprocessed.

Walter sits between supervisor approval and control-room dispatch. Every NO-GO defect that the supervisor saw lands in his queue. His job is to convert each one into a SAP jobcard number — either by typing one in manually or by letting the SAP integration assign one automatically through the outbox publisher. The act of clicking Capture stamps PlannerCapturedAt, attaches the jobcard, writes an audit event, and moves the defect into the Control Room queue. He can also reject a defect that's a duplicate of an existing jobcard or a false alarm, sending it back to the supervisor with a reason.

The Planner page also has a second lane that closes the no-defect route. When the supervisor has approved a clean GO or signed off a GO-BUT, the submission appears on Walter's "Approved submissions awaiting shift-log capture" list. He clicks Capture (no jobcard, just acknowledgment) and the submission is logged into the shift system for production reporting. Phase 6.2 added an end-of-shift bulk-capture button for both lanes so a busy planner can clear his entire queue with two clicks.

Walter's role exists because real mining operations need a deliberate gatekeeper between "operator says there's a problem" and "workshop assigns someone to fix it". Without him, defects accumulate informally, jobcards get duplicated, SAP and the maintenance app drift out of sync, and nobody can answer the question "how long did it take to get this fault into the system?".

### Control Room

The control room is the second new role from Phase 3. Her name in the spec is Lugisani, and she sits at the dispatch desk in the mine's control room. Her job is to look at the planner-captured jobcards and decide which artisan goes to which defect.

When a defect arrives in her queue she sees the machine number, the fleet badge (which contractor operates this machine), the jobcard number, the defect description, and a dropdown of eligible artisans. The dropdown is filtered by fleet — Mota-Engil machines only show Mota-Engil artisans (plus any floating artisans not bound to a contractor), Moolmans machines only show Moolmans artisans. The artisan with the lightest current open-defect load is pre-selected with a star icon, but she can override with one click. She picks an artisan, hits Dispatch, and a push notification fires to the artisan's mobile app while the row moves out of her queue.

When the originally-dispatched artisan goes off-shift or is otherwise unavailable, Lugisani uses the Reassign control on the Recently Dispatched table. When no artisan is available at all (everyone's on leave, on a different breakdown, etc.) she clicks Escalate — the defect stays in the queue but every admin gets notified that the control room can't dispatch.

She also has two read-only views that don't generate work for her but let her run the shift. The **Shift Status Board** is a card grid of every active machine with its current status colour-coded — green for operational, amber for GO-BUT, red for NO-GO, grey for "no recent check". She glances at it to see the fleet pulse. The **Approved-submissions inbox** (Phase 7.6) is where supervisor-approved clean GO and signed-off GO-BUT submissions appear for her to acknowledge — one click per submission, or bulk-acknowledge everything at end of shift. This closes the loop for the no-defect route in the original process flow.

Lugisani's role exists because dispatching to the right artisan is a real-time decision that requires situational awareness — who's available, who's overloaded, which contractor owns which machine, who's been at this jobcard for too long. The system pre-computes the optimal default but leaves the decision with her, because she knows things the system doesn't (who's just come off a difficult job, who's worked with this particular machine before).

### Artisan (also called Mechanic)

The artisan is the workshop technician who actually fixes the defect. At Belfast Coal these are people like Johan de Jongh (Mota-Engil fleet) and David Beukes (Moolmans fleet). The system uses the label "Mechanic" internally because that was the original role name in Phase 1 — every reference in the database and the older parts of the UI still says Mechanic — but the operations team uses Artisan in conversation, so the newer Phase 3+ screens (sidebar, dispatch dropdown, Power BI spec) use the new term. They mean the same thing.

When the control room dispatches a defect to an artisan, it appears on their mobile app's My Defects screen with a notification. They claim it (which stamps when they actually started), order parts via the in-app cart if needed (which fires an email to the parts manager), do the repair, and close the defect with a digital signature and resolution notes. The submission then moves to the admin clearance queue — the machine doesn't return to service until an admin signs off. The artisan can attach photos and voice memos to their notes the same way operators can on defects.

If the parts they need aren't available, they put the defect into "Awaiting Parts" status which surfaces in a separate dashboard tile so the manager can see how much repair work is blocked on procurement.

### Admin

The admin is the cross-cutting role. There's usually one or two of them per mine. They have access to everything the other roles see, plus a layer of administrative screens that nobody else touches.

Their daily action is **Pending Clearance** — when an artisan finishes a repair, the machine goes into "AwaitingAdminClearance" status. The admin reviews what was repaired, optionally adds clearance notes (a SHE-officer comment, an external inspector's note, etc.), and clicks Clear. The machine returns to service, the artisan(s) who did the work get a notification, and every operator who originally raised a NO-GO on that machine gets a push and a tile telling them to do a re-check.

Their occasional actions cover **employee management** (create new operators, supervisors, planners, control-room dispatchers, mechanics, and other admins; reset forgotten passwords; activate or deactivate accounts), **device management** (allowlist tablets so a stolen device can't sign in even with valid credentials; bulk-add devices via CSV during commissioning; mark shared cabinet tablets as "shared" to relax the device-bound login check), **competency tracking** (record machine-type certifications per operator with expiry dates so the system auto-blocks lapsed operators from passing a check), **fleet management** (define contractors with colour-coded badges that propagate to every dispatch and report), **settings** (mine identity, SMTP, integrations, daily-digest recipients — all runtime-editable from `/Admin/Settings`), **outbox monitoring** (when SAP or Key Control rejects an outbox message, dead-letter rows surface here with a red badge and retry button), and **audit chain verification** (run the cryptographic hash chain verifier to prove the audit log hasn't been tampered with — output is suitable evidence for a DMR inspector).

Their reports include the **Reports dashboard** with KPI cards, trend charts, operator and machine leaderboards, defect aging, competency health, configuration audit, the operational queue snapshot showing the depth of every workflow stage right now, and the per-artisan workshop load widget. The **Daily Ops Digest** (Phase 7.1) emails them yesterday's stats and current queue depths at 06:00 every weekday morning — no logging in required.

## The end-to-end workflow

Putting the six roles together, here's what happens when a NO-GO is raised.

The **operator** raises a NO-GO at start of shift. Their device immobilises the machine in the database. If the device is offline, the loud alert (siren + screen flash + vibration) fires locally and a BLE peer broadcast goes out so nearby tablets light up too. The submission queues for sync.

The **supervisor** gets an awareness notification — "NO-GO on ADT-04, machine immobilised, no sign-off needed from you" — so he knows what's happening but doesn't have to act.

The defect appears in the **planner**'s queue. He reviews the defect, types in the SAP jobcard number (or waits for SAP to assign one through the outbox), clicks Capture, and the defect moves to control room dispatch.

The **control room** sees the jobcard. The dispatch dropdown filters to artisans in the same fleet as the machine, with the lightest-loaded one pre-selected. The dispatcher picks one, clicks Dispatch, and a push notification fires to that artisan.

The **artisan** sees the new defect on their mobile app. They claim it, fix it (ordering parts if needed), and close it out with their signature.

The **admin** sees the machine on the Pending Clearance list. They review the artisan's notes, optionally add clearance notes, and click Clear. The machine returns to service.

The **operator** who originally raised the NO-GO gets a push notification — "ADT-04 ready for re-check". They open the app, see the "Awaiting your re-check" tile on the dashboard, tap it, and they're on the re-check screen. The new submission is auto-linked to the original NO-GO so the workshop can later see whether the fix held or whether the same defect was raised again within the seven-day first-time-fix window.

The clean-GO route is simpler: operator submits, supervisor approves with a click, planner captures into the shift log, and control room acknowledges. Three roles touch every submission per the mine SOP, but the click count is minimal because of bulk-approve, bulk-capture, and bulk-acknowledge end-of-shift buttons.

## Fleet segregation

A real mine has multiple contractors operating side-by-side. Belfast Coal works with Mota-Engil and Moolmans as the primary fleet contractors, plus a smaller fleet of direct-employee machines under "Belfast Direct". The system models this via the Fleet entity. Every machine carries a FleetId; every artisan optionally carries a FleetId (artisans without a fleet are "floating" and dispatchable to any fleet's machines); operators and supervisors are also fleet-tagged so reporting can split performance by contractor.

The fleet badges propagate everywhere: dispatch dropdowns are filtered, the Reports page has a per-fleet performance scorecard, the Daily Ops Digest splits its 7-day numbers by fleet, the Power BI dashboards have drill-through pages per contractor. When Mota-Engil's contract is up for renewal next year, the fleet manager has clean numbers to compare against Moolmans.

## Compliance and audit

The system is designed against three regulatory frameworks. **MHSA Section 22(a)** requires that operators are competent for the machine they operate; the OperatorCompetencies table, the daily expiry worker, the SHE-officer email alerts, and the submission-time gate together discharge that obligation. **DMR Regulation 10.3** requires tamper-evident records of every pre-use inspection; the AuditEvent table with its SHA-256 row hash chain is what an inspector reviews when proving an event happened or didn't. **CPS Level 8/9** is a regulatory standard for management of change in safety-critical systems; the Phase 6+ runtime configuration store with full audit on every settings change is how Belfast satisfies it.

The audit hash chain is worth explaining. Every AuditEvent row carries a PrevHash (the previous row's hash) and a RowHash (computed from this row's content plus PrevHash via SHA-256). The chain is serialised with a SemaphoreSlim so concurrent writes don't break the order. Anyone trying to tamper with a row in the middle of the history would have to recompute every row's hash from that point forward — operationally impossible without database access AND knowledge of the chain algorithm AND time the rest of the system isn't writing. The `/Admin/VerifyAuditChain` page lets the admin run the verifier on demand and produces inspector-ready output.

## Integrations

The system has three categories of outbound integration, all routed through the Transactional Outbox pattern so messages are durable across server crashes and network outages.

**SAP Plant Maintenance** integration creates a work order in SAP every time a planner captures a defect with a jobcard. The scaffold is in place (HTTP publisher, JSON envelope, config settings); the vendor-specific field mapping waits on the SAP basis team's input.

**Key Control physical interlock** locks the physical vehicle key in the cabinet when a NO-GO is raised on a mapped machine, and unlocks it when the admin clears the machine. The scaffold supports Traka, Morse Watchmans, KEYper, and similar vendor cabinets; the slot-mapping CSV upload at `/Admin/KeyControlSlots` populates the Machine.KeyControlSlotId column in bulk during commissioning.

**Daily Ops Digest** emails go out every morning at 06:00 UTC to the configured recipient list, with yesterday's check totals, NO-GOs raised, defects closed, current queue depths, and per-fleet 7-day performance. Phase 7.1.

## Offline resilience

Every workflow that involves a mobile device is offline-capable. Operators can complete a full pre-use check with no network and the submission queues to a local SQLite database with a 60-day retention guarantee. Supervisors can sign off submissions offline; the sign-off queues and reconciles when the device reconnects. Artisans can close defects offline. The SyncWorker drains the queues automatically every connectivity transition and on a 30-second timer.

When the operator's device is genuinely cut off in a deep pit cut, the Phase 8 stack kicks in: loud alert mode escalates the NO-GO with a siren, BLE peer broadcasting tells nearby Equipment Checklist tablets what's happening, and the Android foreground service keeps the BLE scanner alive through screen-off and app backgrounding so nothing is missed.

## Closing note

The system replaces a paper process that mining inspectors universally agree is brittle, fragmentary, and unauditable. Every step that used to involve a clipboard, a handover form, a Whatsapp message, or a verbal "tell Walter about it" is now recorded with a timestamp, an actor, and an audit row. The mine retains the regulatory evidence it needs without anyone having to remember to file it. Operations management gets real numbers — first-time-fix rate, dispatch latency, fleet performance — that simply did not exist in the paper world.

The six roles each own one stage of the lifecycle. The operator surfaces the problem. The supervisor accepts or rejects it. The planner files it formally. The control room dispatches it. The artisan fixes it. The admin signs it off. The operator re-checks. The loop closes. Every transition writes audit. Every fleet keeps its numbers separate. Every shift handover happens with a clean inbox on every desk.
