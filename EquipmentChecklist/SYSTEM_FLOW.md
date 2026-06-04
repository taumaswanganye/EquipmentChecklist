# Equipment Pre-Use Checklist — System Flow & User Documentation

**Document owner:** Operations / Training Lead
**Target audience:** End users (operators, supervisors, mechanics), trainers, mine management
**System:** Equipment Pre-Use Checklist
**Last reviewed:** 2026-06-04

This document walks every role through the system from the moment they sign in to the moment they sign out. Read top-to-bottom if you're new; jump to the role section you care about if you're not.

---

## 1. The four roles at a glance

The system has exactly four roles. A user can hold more than one (e.g., an Admin who is also a Supervisor), but most users hold just one.

| Role | What they do | Where they work | Primary surface |
|---|---|---|---|
| **Admin** | Onboards users, fleet, templates. Reads reports. Configures the mine. | Office | Web |
| **Supervisor** | Reviews submissions, signs off on partial passes, rejects unsafe ones | Office + field | Web + mobile |
| **Mechanic** | Owns the defect repair queue. Claims work, orders parts, closes repairs. | Workshop + field | Mobile (mostly) |
| **Operator** | Runs the pre-use inspection on assigned machines every shift | Underground / open-pit | Mobile (entirely) |

The system has one canonical story it tells, and every role plays a chapter:

```
Operator runs checklist  ─►  Submission saved  ─►  Status decided
                                                   │
                                ┌──────────────────┼──────────────────┐
                                │                  │                  │
                              [GO]             [GO-BUT]            [NO-GO]
                                │                  │                  │
                          Machine cleared    Supervisor sign-off  Machine immobilised
                                              required             Defect orders raised
                                                  │                       │
                                              Approve / Reject       Mechanic claims
                                                                          │
                                                                    Order parts / repair
                                                                          │
                                                                  Machine re-mobilised
```

---

## 2. Admin journey

### 2.1 First sign-in

Admin opens the web app at `https://checklist.your-mine.co.za` and lands on the **Sign-in** page. They enter the seeded admin credentials (and, if WebAuthn is configured, can enrol a fingerprint here for future sign-ins).

After a successful sign-in, ASP.NET Identity sets the cookie, the layout redraws with the admin sidebar, and the browser navigates to **/Home/Index** — the admin **Dashboard**.

### 2.2 The admin dashboard

The dashboard is what an admin sees every morning:

```
┌─────────────────────────────────────────────────────────────────────────┐
│  🔔 3   ☀️  Admin (admin@belfast.co.za)  ▾                              │
├──────┬──────────────────────────────────────────────────────────────────┤
│ 🏠   │                                                                  │
│ 🚜   │   Welcome back, Sipho                                            │
│ 👥   │                                                                  │
│ 📋   │   ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐            │
│ 🖼️   │   │ Fleet    │ │ Today's  │ │ Open     │ │ Awaiting │            │
│ 📈   │   │ machines │ │ checks   │ │ defects  │ │ sign-off │            │
│ 📜   │   │   42     │ │   28     │ │   7      │ │   3      │            │
│ ⚙️   │   └──────────┘ └──────────┘ └──────────┘ └──────────┘            │
│      │                                                                  │
│      │   Recent submissions                                             │
│      │   ┌────────────────────────────────────────────────────────┐    │
│      │   │ GRD-007 · Front loader     │ Sipho M   │ GO    │ 07:12 │    │
│      │   │ DZR-014 · Dozer            │ Thabo K   │ GO-BUT│ 07:09 │    │
│      │   │ TRK-022 · Haul truck       │ Lerato N  │ NO-GO │ 06:58 │    │
│      │   └────────────────────────────────────────────────────────┘    │
└──────┴──────────────────────────────────────────────────────────────────┘
```

The bell in the topbar shows unread notifications. The sidebar carries the admin's tools:

| Icon | Item | Purpose |
|---|---|---|
| 🏠 | Home | Dashboard |
| 🚜 | Machines | Fleet roster — add, edit, immobilise |
| 👥 | Employees | User management — create, assign roles, deactivate |
| 📋 | Templates | Per-machine inspection templates |
| 🖼️ | Icon Library | Reusable item icons used by templates |
| 📈 | Reports | KPI dashboard + Excel/PDF exports |
| 📜 | Audit Trail | Append-only event log |
| ⚙️ | Settings | Mine config, debug tools |

### 2.3 Onboarding users (Employees)

**Admin → 👥 Employees → ▶ Add Employee** opens a form that creates an ApplicationUser:

```
Full name      ┌────────────────────────┐
               │ Sipho Khumalo          │
               └────────────────────────┘
Employee #     ┌────────────────────────┐
               │ EMP-1042               │
               └────────────────────────┘
Email          ┌────────────────────────┐
               │ sipho.k@belfast.co.za  │
               └────────────────────────┘
Role           ⦿ Operator
               ○ Supervisor
               ○ Mechanic
               ○ Admin
Initial pass.  ┌────────────────────────┐
               │ ••••••••               │
               └────────────────────────┘
                                 [Create]
```

On save, the system writes a row into `AspNetUsers` + `AspNetUserRoles`, sends a welcome email (if SMTP is configured), and lands the admin back on the Employees list. The new user can now sign in on web (supervisor/mechanic/admin) or on mobile (any role).

### 2.4 Adding machines to the fleet

**Admin → 🚜 Machines → ▶ Add Machine** opens a three-step wizard:

1. **Step 1 — Identity:** Machine number (the placard on the equipment), human-readable name, type ("Front Loader", "Drill Rig", custom free-text).
2. **Step 2 — Template:** pick an existing checklist template or upload a new one (PDF or Excel). The PDF parser reads bullet items and lets the admin review + reorder them before saving.
3. **Step 3 — Assignment:** pick one or more operators authorised to drive this machine, and the mechanics responsible for its maintenance.

The wizard hard-saves at each step so a page reload doesn't lose work.

### 2.5 Assigning operators to supervisors

**Admin → 👥 Employees → [click operator] → Assign Supervisor** picks the supervisor who reviews this operator's GO-BUT submissions. This relationship is many-to-many — an operator may report to more than one supervisor (e.g., A-shift vs B-shift).

### 2.6 Uploading checklist templates

A template is a per-machine list of items to inspect. Items can be **standard** (defect → GO-BUT) or **NO-GO** (defect → immediate NO-GO and machine immobilisation).

**Admin → 📋 Templates → ▶ Upload Template** accepts:

- A structured Excel workbook (one sheet per machine — supports bulk upload of many templates at once).
- A scanned PDF of an existing paper checklist (parses bullets, admin curates).
- A blank slate where items are added one-by-one.

For each item the admin sets:

```
Item name     "Brake hoses condition"
Icon          (pick from library)
Critical?     ☑ Mark a defect as NO-GO  ← if checked, this item failing
                                          immobilises the machine
Sort order    7
```

### 2.7 Reading reports

**Admin → 📈 Reports** opens the KPI dashboard:

- **KPI cards:** total submissions, GO rate %, GO-BUT count, NO-GO count, defects this period, open defects.
- **Charts:** submissions trend (line), defects trend (line), result-breakdown doughnut, top-10 machines by defect count (bar).
- **Leaderboards:** top operators by submission count, defect-aging buckets (<1d / 1-3d / 3-7d / >7d).
- **Raw table:** the submissions in the current window with search and date filters.

Two export buttons (Excel and PDF) emit the filtered scope as a structured workbook or a print-ready PDF that procurement / external auditors can file.

### 2.8 Reading the audit trail

**Admin → 📜 Audit Trail** is the answer to "who did what when?" Every state change writes a row that can never be modified:

```
┌──────────────────┬──────────────┬────────────────────────┬──────────────┐
│ When (server)    │ Actor        │ Action                 │ Target       │
├──────────────────┼──────────────┼────────────────────────┼──────────────┤
│ 2026-06-04 07:12 │ Sipho M      │ submission.created     │ Submission   │
│                  │ Operator     │                        │ #2487        │
├──────────────────┼──────────────┼────────────────────────┼──────────────┤
│ 2026-06-04 07:13 │ Lerato N     │ submission.signoff     │ Submission   │
│                  │ Supervisor   │                        │ #2487        │
├──────────────────┼──────────────┼────────────────────────┼──────────────┤
│ 2026-06-04 07:14 │ Thabo K      │ defect.claimed         │ DefectOrder  │
│                  │ Mechanic     │                        │ #341         │
└──────────────────┴──────────────┴────────────────────────┴──────────────┘
```

Filters: search (free-text across actor + action + target), date range, action-name dropdown, per-actor refinement.

### 2.9 Immobilising / releasing machines

Admins can manually immobilise a machine outside the inspection flow (e.g., during major service):

**Admin → 🚜 Machines → [click machine] → ⛔ Immobilise** writes the reason + flips `IsImmobilised = true`. The machine immediately disappears from operators' "available to inspect" list. **Admin → Release** clears it.

---

## 3. Supervisor journey

### 3.1 Sign-in and dashboard

Supervisor signs in (web at desk, mobile in the field). The sidebar shows three items:

| Icon | Item | Purpose |
|---|---|---|
| ✍️ | Sign-off Queue | Submissions awaiting their approval |
| 👥 | My Operators | Operators reporting to them |
| 🚫 | NO-GO Machines | Equipment they're aware of being down |

The dashboard summarises queue depth: "**3** sign-offs waiting · **2** rejections sent today · **5** operators active."

### 3.2 Reviewing the sign-off queue

**Supervisor → ✍️ Sign-off Queue** lists every GO-BUT submission from their team that hasn't been approved or rejected yet:

```
┌──────────────────────────────────────────────────────────────────────┐
│   ⚠ GO-BUT                            GRD-007 · Front loader          │
│   Operator: Sipho Khumalo · #EMP-1042 · 2026-06-04 07:12 · Shift A    │
│                                                                       │
│   Defects Reported (2)                                                │
│     ⚠ Hydraulic leak (front-left)  – Slow drip, no spray              │
│     ⚠ Cabin light                  – Bulb out                         │
│                                                                       │
│   Shift A · 1,247 hours · 2 defects                                   │
│                                                                       │
│   [ ✍ Review & sign off ]  [ ✓ Quick approve ]  [ 📄 PDF ]            │
└──────────────────────────────────────────────────────────────────────┘
```

Three paths forward:

- **Review & sign off** — opens the full submission detail page, complete with operator signature, item-by-item state, photos, voice memos. From there the supervisor can sign off (24H or 30-day) or reject.
- **Quick approve** — opens a modal with just the signature pad. For routine GO-BUTs (minor defects the supervisor has already discussed with the operator), this is the one-tap path.
- **PDF** — opens the read-only PDF in a popup. Sometimes the supervisor wants to email it to a manager before deciding.

### 3.3 Approving

A sign-off requires a fresh digital signature on the spot. The pad is rendered with a signature_pad.js canvas; the supervisor draws, clicks **Approve · 24H** (repair within 24 hours) or **Approve · 30D** (repair by next service), and the system:

1. Updates the submission: status → GoButRepair24H / GoTillNextService, supervisor id + signature + timestamp stamped.
2. Writes an audit row: `submission.signoff`.
3. Pushes a notification to the operator: "✓ Approved on GRD-007 — repair within 24H".
4. Returns the supervisor to the queue, one card lighter.

### 3.4 Rejecting

If the supervisor decides the machine is unsafe even with the defects, they reject:

```
┌────────────────────────────────────────────────────────────┐
│  Reject submission                              ✕         │
├────────────────────────────────────────────────────────────┤
│  Why are you rejecting?                                    │
│  ┌──────────────────────────────────────────────────────┐ │
│  │ Brakes failing — machine unsafe                       │ │
│  └──────────────────────────────────────────────────────┘ │
│                                                            │
│  Assign to mechanic:                                       │
│  ⦿ Thabo K (3 open jobs)                                   │
│  ○ Lerato N (1 open job)  ← suggested by workload          │
│  ○ Bongani M (5 open jobs)                                 │
│                                                            │
│                                  [Cancel]    [Reject]      │
└────────────────────────────────────────────────────────────┘
```

On reject the system:

1. Flips the submission to `Rejected`.
2. Immobilises the machine.
3. Creates a `DefectOrder` for every defective item, assigned to the chosen mechanic.
4. Sends the mechanic an email with the rejection PDF attached.
5. Pushes a notification to the operator: "✕ Sign-off rejected on GRD-007 — see machine status".
6. Writes audit rows for `submission.reject` AND `machine.immobilised`.

### 3.5 Offline supervisor

When working in the field (e.g., riding alongside an operator who flagged a defect), the supervisor's mobile app caches:

- The sign-off queue at last sync.
- The full template + assignment graph for offline lookup.

The supervisor can approve or reject offline. The action goes into the **ActionQueue** with a 🌥 badge ("waiting to sync") visible on the queue card. When the device gets signal, the `SyncWorker` drains the queue.

**If they lost the race** (another supervisor approved the same submission first), the server returns 409 + writes a `conflict.rejected` notification. The mobile bell ticks up to **🔔 1** and the message reads:

> ✕ Sign-off rejected — GRD-007
> This submission was already signed off by Lerato Ndlovu.

This is the "no silent overwrite" guarantee.

---

## 4. Mechanic journey

### 4.1 Sign-in and dashboard

Mechanic signs in (mostly mobile — they're in the workshop or alongside a machine, not at a desk). The dashboard shows their **defect queue** stats:

```
┌─────────────────────────────────────────────────────────┐
│  My Defects                                              │
│                                                          │
│  ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐ ┌─────┐                 │
│  │ 4   │ │ 2   │ │ 7   │ │ 3   │ │ 1   │                 │
│  │Open │ │Wait │ │Pool │ │Done │ │NO-GO│                 │
│  │mine │ │parts│ │free │ │today│ │mach │                 │
│  └─────┘ └─────┘ └─────┘ └─────┘ └─────┘                 │
│                                                          │
│  🛒 Parts cart (2)                                       │
└─────────────────────────────────────────────────────────┘
```

### 4.2 Browsing defects

**Mechanic → 🔧 My Defects** has two tabs:

- **Assigned to me** — the work they own.
- **Unassigned** — the pool they can pick from.

```
🔧 Assigned to me (4)        👀 Unassigned (7)

  ┌────────────────────────────────────────────────────────┐
  │ GRD-007 · Front loader                  ⚠ NO-GO        │
  │ Brake hoses failing — operator: Sipho M                │
  │ 👤 Sipho M  · 📅 2026-06-04 07:12 · 🧰 Brake pad BP-1234│
  │                                                        │
  │ [ ✋ Claim ]   [ ✍ Open detail ]                       │
  └────────────────────────────────────────────────────────┘
```

Search and date filters narrow the list. Cards order by severity (immobilised machines first).

### 4.3 Claiming a job

Tap **✋ Claim** on an unassigned defect. The server flips `AssignedMechanicId` to you and `RepairStatus` to `InProgress`. Audit row written. The job moves to "Assigned to me" on next refresh.

If two mechanics tap Claim at nearly the same time (race condition), one wins; the other sees a notification:

> ✕ Claim rejected — DZR-014
> This job was already claimed by Thabo Khumalo.

### 4.4 Ordering parts

Tap **✍ Open detail** on a claimed defect, then **🧰 Order Part**. A form opens:

```
What's the failed component?   ┌──────────────────────────────┐
                               │ Brake hose                    │
                               └──────────────────────────────┘
Part number (optional)         ┌──────────────────────────────┐
                               │ BP-1234                       │
                               └──────────────────────────────┘
                                                       [Order]
```

On order:

1. Defect status → `AwaitingParts`.
2. Audit row written.
3. Email goes to the parts manager with a PDF parts-order receipt.
4. The mechanic can keep working on other things; this defect stays in their queue with a 🧰 badge.

### 4.5 Completing a repair

When the part arrives and is fitted, tap **✓ Mark complete** on the defect. A signature pad opens. The mechanic draws their signature, optionally adds a repair note, and confirms.

The system:

1. Flips `RepairStatus` to `Completed`, stamps signature + notes + timestamp.
2. Writes audit row `defect.completed`.
3. Checks whether this was the LAST open defect on the machine. If yes, clears the immobilisation flag, writes another audit row `machine.released`.
4. Pushes a notification to the operator who flagged the defect: "🔧 GRD-007 — repair completed, machine clear."

### 4.6 Parts cart (multi-item ordering)

For big repairs the mechanic adds multiple parts to a cart and orders them in one email batch.

**Mechanic → 🛒 Parts Cart → [Add Item]** lets them stage as many parts as they like, then **Send to Manager** generates one PDF with all of them and emails it.

### 4.7 Offline mechanic

Mechanics in the workshop usually have signal; underground, they may not. The same offline-first patterns apply:

- The defect queue is cached on the device.
- Claim / order-part / complete actions queue locally.
- The drainer ships them when signal returns.

---

## 5. Operator journey

### 5.1 Sign-in (mobile)

Operators almost never use the web — they're underground. They open the mobile app and see the sign-in screen:

```
   ⛏ Pre-Checklist

   Email     ┌──────────────────────────┐
             │ sipho.k@belfast.co.za    │
             └──────────────────────────┘
   Password  ┌──────────────────────────┐
             │ ••••••••                  │
             └──────────────────────────┘
                       [ Sign in ]

   [ 🔒 Unlock with fingerprint ]
```

First sign-in requires online network access and password. After that, biometric unlock works fully offline (the AndroidX BiometricPrompt is wired directly to the OS — no server round-trip).

### 5.2 Dashboard

After signing in, the operator lands on **My Machines**:

```
┌──────────────────────────────────────────────────────┐
│  My Machines               🔔 0   🌥 0 queued        │
├──────────────────────────────────────────────────────┤
│                                                       │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌──────────┐ │
│  │ FLEET    │ │ OPERATL  │ │ NO-GO    │ │ TODAY    │ │
│  │   3      │ │   2      │ │   1      │ │   2      │ │
│  └──────────┘ └──────────┘ └──────────┘ └──────────┘ │
│                                                       │
│  ┌──────────────────────┐  ┌──────────────────────┐  │
│  │ GRD-007              │  │ DZR-014              │  │
│  │ Front Loader · CAT   │  │ Dozer · D9R          │  │
│  │                      │  │                      │  │
│  │ [ ▶ Start checklist ]│  │ [ ▶ Start checklist ]│  │
│  └──────────────────────┘  └──────────────────────┘  │
│                                                       │
│  ┌──────────────────────┐                            │
│  │ TRK-022              │                            │
│  │ Haul truck · 777G    │                            │
│  │  ⛔ IMMOBILISED      │                            │
│  │  Brake system fault  │                            │
│  │ [ Machine Immobilised ]                           │
│  └──────────────────────┘                            │
└──────────────────────────────────────────────────────┘
```

Immobilised machines are visible but un-startable. This is intentional — the operator should *see* that the machine is down and *why*, not just have it disappear.

### 5.3 Running a checklist

Tap **▶ Start checklist** on an available machine. The Checklist page loads:

```
‹ Back                                            Save draft

GRD-007 · Front Loader · CAT
Shift   ⦿ Day (A)   ○ Afternoon (B)   ○ Night (C)
KM/hrs  ┌─────────┐
        │ 1247    │
        └─────────┘

  ───────────────  INSPECTION ITEMS  ───────────────

  ┌──────────────────────────┐  ┌──────────────────────────┐
  │ 🛢️ Engine oil level      │  │ 🦺 Cabin seatbelt        │
  │ ⦿ In order    ○ Defect  │  │ ⦿ In order    ○ Defect  │
  └──────────────────────────┘  └──────────────────────────┘
  ┌──────────────────────────┐  ┌──────────────────────────┐
  │ 🚨 Reverse alarm  CRITICAL│  │ 💧 Hydraulic level       │
  │ ⦿ In order    ○ Defect  │  │ ○ In order    ⦿ Defect  │
  │                          │  │ Note: slow drip front-L  │
  │                          │  │ 📷 photo  🎤 voice memo │
  └──────────────────────────┘  └──────────────────────────┘
                       . . .

  Fitness declaration  ☑ I am fit and capable of operating
                         this machine

  Remarks             ┌───────────────────────────────┐
                      │ Slight pull to the left under │
                      │ load, otherwise fine.         │
                      └───────────────────────────────┘

  Signature           ┌───────────────────────────────┐
                      │ [signature pad]                │
                      └───────────────────────────────┘

                                              [ Submit ]
```

For each item, the operator picks **In order** or **Defect**. When they tap Defect:

- A note field opens.
- A 📷 button opens the camera (photo is resized to 1280×960 @ 80% JPEG before saving).
- A 🎤 button opens the voice recorder.

Items flagged as **CRITICAL** (NO-GO items) are visually distinct. A defect on any of them auto-decides the submission as NO-GO.

### 5.4 Submitting

Tap **Submit**. The progress overlay shows the stages:

```
   ⚙️  Validating items…              ✓ Done
   ☁️  Uploading to server…            ⏳ Sending…
   📄  Generating PDF receipt…
   ✓  All done
```

The "Submit-always-saves" guarantee: if the upload fails for ANY reason (server down, no signal, timeout, server returned non-2xx), the submission falls through to the local queue. The overlay relabels: "Saved offline — will sync when signal returns." The operator never loses work.

### 5.5 Outcome screen

After submit, the operator sees the result:

```
┌──────────────────────────────────────┐
│                                       │
│            🟢 GO                      │
│                                       │
│   GRD-007 is cleared for your shift. │
│                                       │
│   ┌────────────────────────────────┐ │
│   │  📄 View PDF receipt           │ │
│   └────────────────────────────────┘ │
│   ┌────────────────────────────────┐ │
│   │  📋 My Submissions             │ │
│   └────────────────────────────────┘ │
│   ┌────────────────────────────────┐ │
│   │  ▶  Start another machine      │ │
│   └────────────────────────────────┘ │
└──────────────────────────────────────┘
```

The three possible outcomes:

- **🟢 GO** — no defects. Operator drives.
- **🟡 GO-BUT** — non-critical defects. Operator drives, but a supervisor signature is required (pending until the supervisor signs off).
- **🔴 NO-GO** — critical defect. Machine immobilised. Operator does not drive. Mechanic workflow triggered automatically.

### 5.6 Browsing history

**Operator → 📋 My Submissions** shows every checklist the operator has submitted. Filter pills + search + date range narrow the list. Each row offers View (modal with full details) and PDF.

### 5.7 Receiving notifications

The bell ticks up when:

- A supervisor approves their GO-BUT submission.
- A supervisor rejects their submission.
- A mechanic completes a repair on a machine they flagged.

Tapping the bell opens the dropdown. Tapping a notification jumps to the related submission detail.

### 5.8 Offline operator (the headline feature)

The operator's entire workflow runs offline:

| Step | Online? |
|---|---|
| First-ever sign-in | Yes (after that, biometric works offline) |
| Open the app | No — cached shell loads instantly |
| See assigned machines | No — cached roster |
| Run checklist | No — template cached at last sync |
| Capture photo + voice memo | No |
| Submit | Either — straight upload OR queued |
| See result | Yes (either real status from server, or "QUEUED" badge) |
| View PDF receipt | No — generated on-device with jsPDF |

The status pill in the topbar tells the operator at a glance: **🟢 Online**, **🟡 Server unreachable**, **🌙 Offline**. The 🌥 queued badge counts pending submissions.

---

## 6. Cross-cutting flows

### 6.1 Notifications

Every role has a 🔔 bell in the topbar. Notifications are cross-role:

| Trigger | Recipient | Kind |
|---|---|---|
| Operator submits NO-GO | Their supervisor | `submission.nogo` |
| Operator submits GO-BUT | Their supervisor | `submission.gobut` |
| Supervisor signs off | The operator | `submission.approved` |
| Supervisor rejects | The operator | `submission.rejected` |
| Mechanic completes repair | The operator | `defect.resolved` |
| Queued action loses a race | The losing actor | `conflict.rejected` |

Pushed live via SignalR when the user is online. Persisted to the database so a user who was offline at the moment sees them when they next sign in.

### 6.2 The PDF receipt

Every submission generates a PDF receipt that's the official record. Two paths:

- **Server-side (QuestPDF):** rendered when the submission lands at the server, served from `/Checklist/ViewPdf/{id}`. Embeds defect photos and the operator + supervisor signatures inline.
- **On-device (jsPDF):** rendered immediately on the operator's phone, even offline. Cached locally so the operator can view it without re-rendering.

Both PDFs follow the same MHSA-compliant layout: header with mine name + tagline, machine identity block, items table, defects callout box, signatures, footer with compliance text.

### 6.3 Audit trail visibility

Audit rows are visible at **/Admin/Audit** (web). Admins can filter by:

- Date range (default last 7 days)
- Action name (dropdown derived from distinct values in scope)
- Per-actor email
- Free-text search across actor / target / payload

A typical investigation: "Who signed off submission 1247 and when?" — search by target id, see the `submission.signoff` row with actor and timestamp. Click through to the submission detail to see the signature.

---

## 7. End-to-end scenarios

These are real worked examples. Walk through one of them with a trainee to ground every role.

### 7.1 Scenario A — clean GO

**Setting:** start of shift A, 06:45. Operator Sipho arrives at front-loader GRD-007.

1. Sipho opens the app on his Android device. Cached dashboard shows GRD-007 available.
2. Taps **▶ Start checklist**. Walks around the machine, ticks 18 items In order. None flagged.
3. Signs the fitness declaration, draws his signature, taps **Submit**.
4. Online — the server accepts: status → `Go`, no notifications.
5. Sipho gets the green outcome card, taps **PDF** to preview, then climbs into the cab and starts work.

No supervisor or mechanic involvement. The audit trail shows one row: `submission.created` by Sipho on Submission #2487, machine GRD-007, status `Go`.

### 7.2 Scenario B — GO-BUT with supervisor sign-off

**Setting:** mid-shift, 09:30. Operator Thabo notices a cabin light is out on dozer DZR-014.

1. Thabo starts the checklist on DZR-014. Marks "Cabin lights" as Defect, types "Driver-side bulb blown". Other 17 items In order.
2. Calculated status: GO-BUT (light is not a NO-GO item).
3. Submission posts to server. Server pushes a notification to Thabo's supervisor Lerato: "✍ Sign-off needed on DZR-014".
4. Lerato opens her web app at her desk, sees the bell badge, navigates to the sign-off queue.
5. She taps **Quick approve**, signs, taps **Approve · 24H**.
6. Server flips status → `GoButRepair24H`, audit row written, notification pushed back to Thabo: "✓ Approved on DZR-014 — repair within 24H".
7. Thabo carries on driving. The defect is flagged in the system; a mechanic will fit the bulb when convenient.

Audit trail: `submission.created`, `submission.signoff`. Two rows tell the whole story.

### 7.3 Scenario C — NO-GO triggering mechanic workflow

**Setting:** start of shift B, 14:30. Operator Lerato N. checks haul-truck TRK-022.

1. Lerato finds the brake hoses leaking. Marks "Brake system" as Defect — **this item is CRITICAL**.
2. The auto-calculation: NO-GO. Lerato submits anyway (the system requires her to submit even on NO-GO so the record exists).
3. Server: status → `NoGo`, machine `IsImmobilised = true`, `ImmobilisedReason` = "NO-GO submitted at 14:30 — Brake system".
4. System auto-creates a DefectOrder for the brake-system item, leaves it unassigned (mechanic claims from the pool).
5. Notification pushed to Lerato's supervisor: "🚫 NO-GO submitted on TRK-022".
6. Thabo the mechanic sees the new unassigned defect in his pool. Taps **✋ Claim**. Order moves to "Assigned to me".
7. Thabo inspects, identifies he needs brake hose BP-1234. Taps **🧰 Order Part**, fills the form. Status → `AwaitingParts`, email goes to the parts manager with a PDF parts-order.
8. Parts arrive next morning. Thabo fits them. Taps **✓ Mark complete**, signs, adds a note.
9. Server: status → `Completed`. This was the only open defect on TRK-022 so it also clears `IsImmobilised`.
10. Notification pushed to Lerato: "🔧 TRK-022 — repair completed, machine clear."
11. Lerato can now drive TRK-022 (after she runs a fresh pre-use checklist, which she does).

Audit trail for the whole chain: `submission.created` (Lerato), `machine.immobilised` (auto), `defect.created` (auto × N), `defect.claimed` (Thabo), `defect.part_ordered` (Thabo), `defect.completed` (Thabo), `machine.released` (auto). Seven rows, every actor identified, every timestamp authoritative.

### 7.4 Scenario D — conflict resolution

**Setting:** end of shift A. Two supervisors, Lerato and Bongani, both have access to operator Sipho's GO-BUT submission #2487.

1. Both supervisors are in the field with intermittent signal.
2. Lerato opens her queue offline, quick-approves submission #2487, signs. The action sits in her ActionQueue waiting to sync.
3. Bongani opens his queue offline at the same time. Doesn't see Lerato's pending action (it hasn't reached the server). Quick-approves the same submission #2487, signs. Goes into his ActionQueue.
4. Lerato's device gets signal first. SyncWorker drains: her sign-off lands, server flips status, audit row written.
5. Bongani's device gets signal a minute later. SyncWorker drains his action: the server sees `SupervisorId` is already set (Lerato's) — **returns 409 Conflict**, writes a `conflict.rejected` notification for Bongani.
6. Bongani's drainer treats the 409 as Permanent, drops the row, calls `RefreshUnreadCountAsync()`.
7. Bongani's bell ticks up. He taps it and sees: "✕ Sign-off rejected — DZR-014. This submission was already signed off by Lerato Ndlovu."
8. No silent overwrite. Both supervisors know what happened. The audit shows Lerato signed at 09:31 and Bongani's conflict notification at 09:33.

### 7.5 Scenario E — fully offline operator (lost signal mid-shift)

**Setting:** Sipho is underground in level 4 where signal is dead.

1. Sipho's app was last online at 06:30 (started shift on surface, dropped underground at 07:00).
2. At 09:15 he runs his second machine — GRD-009.
3. Status pill shows **🌙 Offline**. He submits anyway.
4. The "Submit-always-saves" flow kicks in: server is unreachable, the submission goes into SubmissionQueue. On-device jsPDF generates the receipt.
5. Sipho keeps working all morning. Submits two more checklists. All sit in the queue with **🌥 2 queued** badge in the topbar.
6. At 12:00 he comes up for lunch. WiFi auto-connects. `Connectivity.ConnectivityChanged` fires.
7. SyncWorker drains: three submissions ship to the server in order. Server-side audit + notifications fire as if they'd come in real-time. The queued badge clears.
8. If a queued submission was older than 60 days (e.g., a phone left on a shelf), the SyncWorker's prune pass drops it with a warning toast: "Dropped 1 submission older than 60 days — it never reached the server."

---

## 8. Quick-reference role × action matrix

| Action | Operator | Supervisor | Mechanic | Admin |
|---|---|---|---|---|
| Submit a pre-use checklist | ✅ | — | — | — |
| View own submissions | ✅ | ✅ | ✅ | ✅ |
| View team's submissions | — | ✅ | — | ✅ |
| Sign off a GO-BUT submission | — | ✅ | — | ✅ |
| Reject a submission | — | ✅ | — | ✅ |
| Claim a defect order | — | — | ✅ | ✅ |
| Order parts | — | — | ✅ | ✅ |
| Complete a repair | — | — | ✅ | ✅ |
| Immobilise a machine manually | — | — | — | ✅ |
| Create users | — | — | — | ✅ |
| Add machines / templates | — | — | — | ✅ |
| Read reports / KPIs | — | partial | partial | ✅ |
| Read audit trail | — | — | — | ✅ |

---

## 9. Common questions

**Q: An operator submitted a checklist on the wrong machine — how do they fix it?**
A: They can't edit a submission (audit integrity). They submit a fresh checklist on the correct machine and add a remark on both explaining the situation. The audit trail captures both.

**Q: A supervisor approves a submission, then changes their mind. Can they undo?**
A: No — once a sign-off is committed it's part of the audit record. To reverse, they must reject the same submission, which creates a new audit entry. The original sign-off is still visible in the trail with both timestamps.

**Q: A mechanic claims a defect by mistake — can they un-claim?**
A: Not directly. They open the detail and select **Reassign**, picking another mechanic. That's a normal write so it's audited. If no one else is free, an admin can unassign from the Machines page.

**Q: What if a machine doesn't have a template yet?**
A: The operator sees the machine card but the **Start checklist** button is disabled with the label "No template". The admin must upload a template before the operator can inspect.

**Q: What happens if the operator's phone is stolen?**
A: The cached SQLite database holds machine roster + recent submissions, but submissions all belong to the operator's account. An admin can deactivate the user account (Employees → Deactivate), which invalidates future sign-ins and biometric unlocks. The offline voucher expires within 24 hours so the device can't continue to access the system after that.

**Q: How long does data live?**
A: Server data (submissions, defects, audit) is retained indefinitely for compliance. Mobile cache prunes anything queued > 60 days (90 days for audit) with a warning toast.

---

*End of document.*
