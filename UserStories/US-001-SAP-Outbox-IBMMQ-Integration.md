# US-001 — SAP Integration via Outbox Pattern + IBM MQ

**Owner:** Tatenda
**Sprint:** TBD
**Estimate:** 5 days (3 dev + 1 testing + 1 IBM MQ setup)
**Type:** Story · Backend · Integration
**Status:** Ready for development

---

## User story

> **As** a business owner of Belfast Coal Mine,
> **I want** every defect raised by an operator's pre-shift checklist to be reliably forwarded to SAP Plant Maintenance,
> **so that** the maintenance team works defects from one trusted system (SAP), procurement plans parts from real defect history, and we never lose a defect because of a network blip or SAP downtime.

## Why this matters (business context)

Today an operator finds a wobbly wheel on Truck ADT-04 during their pre-shift check. Our app records it as a `DefectOrder`. To get that into SAP a human re-types it. The re-typing is where data goes missing: machine numbers get fat-fingered, urgency flags get dropped, half the defects never reach SAP at all because the supervisor is busy.

After this story ships:
- The operator finishes their checklist
- A row appears in SAP PM within seconds as a Maintenance Order
- If SAP is down or the network is broken, the message **does not get lost** — it parks in our outbox until SAP is back
- Every step is auditable end-to-end

This is the foundation for retiring the "WhatsApp the workshop supervisor" workaround everyone currently uses.

---

## Architecture overview

```
┌──────────────────┐    1. Same DB transaction    ┌─────────────────────┐
│ ChecklistService │─────────────────────────────▶│ DefectOrder         │
│ (defect created) │                              │ OutboxMessage(Draft)│
└──────────────────┘                              └─────────┬───────────┘
                                                            │
                                                            │ 2. Polled by worker
                                                            ▼
                                                  ┌─────────────────────┐
                                                  │ OutboxPublishWorker │
                                                  │ (background service)│
                                                  └─────────┬───────────┘
                                                            │ 3. Publish
                                                            ▼
                                                  ┌─────────────────────┐
                                                  │ IBM MQ Queue        │
                                                  │ DEV.QUEUE.SAP.DEFECT│
                                                  └─────────┬───────────┘
                                                            │ 4. SAP consumes
                                                            ▼
                                                  ┌─────────────────────┐
                                                  │ SAP PM              │
                                                  │ creates Work Order  │
                                                  └─────────────────────┘
```

**Key guarantee — atomicity.** The `DefectOrder` and the `OutboxMessage` are saved in the **same EF Core transaction**. Either both land or neither does. We can never have a defect on disk with no corresponding outbox message, OR an outbox message with no underlying defect.

**Key guarantee — at-least-once delivery.** The worker only deletes/marks-sent an outbox row AFTER IBM MQ has acknowledged the publish. If the worker crashes mid-publish, the row stays Draft and the next worker pass picks it up. SAP must handle duplicates idempotently (it does — uses our DefectOrderId as the idempotency key).

---

## Acceptance criteria

```gherkin
Scenario 1: Defect is written to outbox in the same transaction
  Given an operator submits a NO-GO checklist for ADT-04 with a critical brake defect
  When ChecklistService.ProcessSubmissionAsync completes successfully
  Then a DefectOrder row exists in the database
   And exactly one OutboxMessage row exists with:
     - AggregateType = "DefectOrder"
     - AggregateId   = the DefectOrder.Id
     - MessageType   = "DefectOrderCreated"
     - Status        = "Draft"
     - PayloadJson   = the serialised IntegrationDefectPayload
     - AttemptCount  = 0

Scenario 2: Background worker publishes Draft messages
  Given there are 3 OutboxMessage rows with Status = "Draft"
  When the OutboxPublishWorker runs its next pass
  Then all 3 messages are published to IBM MQ queue DEV.QUEUE.SAP.DEFECT
   And all 3 rows are updated to Status = "Sent"
   And all 3 rows have ProcessedAt set to UtcNow

Scenario 3: Publish failure is retried, not lost
  Given an OutboxMessage with Status = "Draft" and AttemptCount = 0
   And IBM MQ is unreachable
  When the OutboxPublishWorker tries to publish
  Then the row stays at Status = "Draft"
   And AttemptCount is incremented to 1
   And LastError contains the exception message
   And the next worker pass tries again with exponential backoff

Scenario 4: Permanently failed message lands in DeadLetter
  Given an OutboxMessage with AttemptCount = 5 (configurable max retries)
   And the publish still fails
  Then the row is updated to Status = "DeadLetter"
   And an audit event "outbox.dead_lettered" is written
   And the row appears on the Admin → Outbox Dead Letter page for manual review

Scenario 5: Atomicity — DB rollback rolls back the outbox too
  Given ChecklistService.ProcessSubmissionAsync is mid-transaction
  When SaveChangesAsync throws (e.g. DB constraint violation)
  Then neither the DefectOrder nor the OutboxMessage row exists
   And no message is published to IBM MQ

Scenario 6: Same DefectOrderId arriving twice in SAP is handled
  Given the worker publishes DefectOrder #123 successfully but the network drops before marking Sent
   And the worker retries on next pass and publishes it again
  Then SAP sees two messages with the same idempotency key
   And SAP processes the first and ignores the second (idempotent on their side)
   And our OutboxMessage eventually reaches Status = "Sent"
```

---

## Technical design

### New table: `OutboxMessages`

```sql
CREATE TABLE IF NOT EXISTS "OutboxMessages" (
    "Id"             bigserial PRIMARY KEY,
    "AggregateType"  varchar(40)   NOT NULL,    -- e.g. "DefectOrder"
    "AggregateId"    varchar(80)   NOT NULL,    -- e.g. "1234"
    "MessageType"    varchar(80)   NOT NULL,    -- e.g. "DefectOrderCreated"
    "PayloadJson"    text          NOT NULL,
    "Status"         varchar(20)   NOT NULL,    -- Draft / Sent / DeadLetter
    "AttemptCount"   integer       NOT NULL DEFAULT 0,
    "LastError"      varchar(2000) NULL,
    "CreatedAt"      timestamp with time zone NOT NULL,
    "ProcessedAt"    timestamp with time zone NULL,
    "NextAttemptAt"  timestamp with time zone NULL    -- for exponential backoff
);

CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_Status_NextAttemptAt"
    ON "OutboxMessages" ("Status", "NextAttemptAt");
```

Bootstrap via the same `ALTER TABLE IF NOT EXISTS` pattern we used for `AppSettings` and the audit hash-chain columns — see `Program.cs` for prior art.

### New entity: `Models/OutboxMessage.cs`

Standard EF Core POCO with `[Table("OutboxMessages")]`. Add `DbSet<OutboxMessage> OutboxMessages` to `ApplicationDbContext`.

### Modified: `ChecklistService.cs`

Where it currently calls `_integration.PublishDefectOrderCreatedAsync(payload)` fire-and-forget, replace with:

```csharp
_db.OutboxMessages.Add(new OutboxMessage {
    AggregateType = "DefectOrder",
    AggregateId   = order.Id.ToString(),
    MessageType   = "DefectOrderCreated",
    PayloadJson   = JsonSerializer.Serialize(payload),
    Status        = "Draft",
    CreatedAt     = DateTime.UtcNow,
    NextAttemptAt = DateTime.UtcNow,
    AttemptCount  = 0
});
// SaveChangesAsync was already going to be called for the DefectOrder.
// Both rows land in the same transaction — atomic.
```

### New service: `Services/Integrations/OutboxPublishWorker.cs`

A `BackgroundService` that wakes every 5 seconds, queries:

```csharp
var batch = await _db.OutboxMessages
    .Where(m => m.Status == "Draft" && m.NextAttemptAt <= DateTime.UtcNow)
    .OrderBy(m => m.CreatedAt)
    .Take(50)
    .ToListAsync(ct);
```

For each row, call `IIntegrationPublisher.PublishAsync(row.MessageType, row.PayloadJson)` — but the publisher is now `IbmMqIntegrationPublisher` (see below). On success, set `Status = "Sent"`, `ProcessedAt = UtcNow`. On failure, increment `AttemptCount`, set `LastError`, compute `NextAttemptAt = UtcNow + (2^AttemptCount) seconds` (exponential backoff capped at 1 hour). After `AttemptCount >= 5`, set `Status = "DeadLetter"` and write an audit event.

### New publisher: `Services/Integrations/IbmMqIntegrationPublisher.cs`

Replaces the current `SapPmIntegrationPublisher` (which posted directly via HTTP). New implementation publishes to IBM MQ using the official client:

```csharp
// NuGet: IBMXMSDotnetClient (the IBM-supported .NET 8 client)
using IBM.XMS;

public class IbmMqIntegrationPublisher : IIntegrationPublisher
{
    public async Task PublishAsync(string messageType, string payloadJson, CancellationToken ct)
    {
        var factoryFactory = XMSFactoryFactory.GetInstance(XMSC.CT_WMQ);
        var cf = factoryFactory.CreateConnectionFactory();
        cf.SetStringProperty(XMSC.WMQ_HOST_NAME,       _hostName);   // localhost in dev
        cf.SetIntProperty   (XMSC.WMQ_PORT,            _port);       // 1414
        cf.SetStringProperty(XMSC.WMQ_CHANNEL,         _channel);    // DEV.APP.SVRCONN
        cf.SetStringProperty(XMSC.WMQ_QUEUE_MANAGER,   _qManager);   // QM1
        cf.SetIntProperty   (XMSC.WMQ_CONNECTION_MODE, XMSC.WMQ_CM_CLIENT);
        cf.SetStringProperty(XMSC.USERID,              _user);       // app
        cf.SetStringProperty(XMSC.PASSWORD,            _password);   // passw0rd in dev

        using var conn    = cf.CreateConnection();
        using var session = conn.CreateSession(false, AcknowledgeMode.AutoAcknowledge);
        using var dest    = session.CreateQueue("queue:///" + _queueName);
        using var producer= session.CreateProducer(dest);

        var msg = session.CreateTextMessage(payloadJson);
        msg.SetStringProperty("messageType",     messageType);
        msg.SetStringProperty("idempotencyKey",  /* DefectOrderId */ );
        msg.JMSCorrelationID = Guid.NewGuid().ToString();

        producer.Send(msg);
    }
}
```

### Configuration keys (in AppSettings table)

Seed these in `Program.cs → SeedAppSettingsAsync`:

| Key | Default | Description |
|---|---|---|
| `Mq.Enabled` | `false` | Master switch. False → outbox rows pile up but worker doesn't publish. Useful when SAP isn't ready yet. |
| `Mq.HostName` | `localhost` | IBM MQ broker hostname. |
| `Mq.Port` | `1414` | IBM MQ TCP port. |
| `Mq.QueueManager` | `QM1` | Queue manager name. |
| `Mq.Channel` | `DEV.APP.SVRCONN` | Server-connection channel. |
| `Mq.QueueName` | `DEV.QUEUE.SAP.DEFECT` | Queue to publish to. |
| `Mq.User` | `app` | MQ user. |
| `Mq.Password` | `passw0rd` | MQ password (secret). |
| `Mq.MaxRetries` | `5` | Outbox attempts before DeadLetter. |
| `Mq.WorkerIntervalSeconds` | `5` | How often the worker polls Draft messages. |

### Admin UI

Add `/Admin/Outbox` showing:
- Count of Draft / Sent (last 24h) / DeadLetter rows
- Table of recent 50 messages with Id, Status, AttemptCount, CreatedAt, LastError
- "Retry now" button on DeadLetter rows (resets Status=Draft, AttemptCount=0)
- "Purge sent older than 30 days" button

---

## Implementation steps (in order)

### Day 1 — Local IBM MQ Docker setup

1. **Install Docker Desktop** if not already (https://docs.docker.com/desktop/install/windows-install/).

2. **Pull the IBM MQ developer image** (free, fully featured for dev use):

   ```powershell
   docker pull icr.io/ibm-messaging/mq:latest
   ```

3. **Create the local MQ container** with persistent storage:

   ```powershell
   docker volume create qm1data

   docker run --name ibm-mq-dev `
     -e LICENSE=accept `
     -e MQ_QM_NAME=QM1 `
     -e MQ_APP_PASSWORD=passw0rd `
     -e MQ_ADMIN_PASSWORD=passw0rd `
     -v qm1data:/mnt/mqm `
     -p 1414:1414 `
     -p 9443:9443 `
     -d `
     icr.io/ibm-messaging/mq:latest
   ```

4. **Verify it's up.** Open `https://localhost:9443/ibmmq/console` (accept the self-signed cert). Username `admin`, password `passw0rd`. You should see Queue Manager QM1 running, with default queues `DEV.QUEUE.1`, `DEV.QUEUE.2`, `DEV.QUEUE.3` already created.

5. **Create our application queue** in the web console:
   - Queues → Create → Local
   - Name: `DEV.QUEUE.SAP.DEFECT`
   - Defaults are fine
   - Save

6. **Document the setup** — add a `docker-compose.yml` at repo root so the next dev can `docker compose up -d` instead of remembering the command.

### Day 2 — Outbox table + entity + write path

7. **Create `Models/OutboxMessage.cs`** (POCO with the columns listed above).
8. **Register the `DbSet`** in `ApplicationDbContext`.
9. **Bootstrap the table** in `Program.cs` using `ALTER TABLE IF NOT EXISTS` pattern (see audit hash-chain code as reference).
10. **Modify `ChecklistService.cs`** to write to the outbox in the same transaction as `DefectOrder` creation. Remove the existing fire-and-forget call to `_integration.PublishDefectOrderCreatedAsync`.
11. **Write a unit test** that calls `ProcessSubmissionAsync` and asserts both rows exist after `SaveChangesAsync`, and neither exists if the save throws.
12. **Manually test**: submit a NO-GO from the mobile app, query the DB, confirm the OutboxMessage row appears with Status='Draft'.

### Day 3 — Publisher + background worker

13. **Add NuGet package** `IBMXMSDotnetClient` to `EquipmentChecklist.csproj`.
14. **Create `Services/Integrations/IbmMqIntegrationPublisher.cs`** as outlined above.
15. **Create `Services/Integrations/OutboxPublishWorker.cs`** as a `BackgroundService`:
    - Poll every N seconds
    - Fetch batch of up to 50 Draft messages where `NextAttemptAt <= now`
    - For each, call publisher
    - On success: Status='Sent', ProcessedAt=now
    - On failure: AttemptCount++, LastError=ex.Message, NextAttemptAt=now+backoff
    - On AttemptCount >= MaxRetries: Status='DeadLetter', write audit event
16. **Register both services in `Program.cs`** DI graph:
    ```csharp
    builder.Services.AddScoped<IIntegrationPublisher, IbmMqIntegrationPublisher>();
    builder.Services.AddHostedService<OutboxPublishWorker>();
    ```
17. **Seed the `Mq.*` config keys** in `SeedAppSettingsAsync`.
18. **Manually test end-to-end**: submit NO-GO → wait 10 seconds → check OutboxMessage row shows Status='Sent' → open MQ console and verify the message is on `DEV.QUEUE.SAP.DEFECT`.

### Day 4 — Admin UI + dead-letter handling

19. **Create `/Admin/Outbox`** controller action + Razor view.
20. **Add the dead-letter retry button**.
21. **Add the purge-old-sent button**.
22. **Surface the link** in `_Layout.cshtml` sidebar under "Integrations".

### Day 5 — Testing + handover

23. **Write integration tests** (against a Testcontainers IBM MQ instance):
    - Happy path
    - MQ down → row stays Draft + backoff
    - Permanent failure → DeadLetter after 5 attempts
    - Atomic rollback
24. **Update `Device_Setup_Runbook.docx`** with the new "Integrations → Outbox" admin page.
25. **Demo to stakeholders** with SAP basis team present.

---

## Docker setup quick reference

Save this as `docker-compose.yml` at repo root:

```yaml
version: '3.8'
services:
  ibm-mq:
    image: icr.io/ibm-messaging/mq:latest
    container_name: ibm-mq-dev
    environment:
      LICENSE: accept
      MQ_QM_NAME: QM1
      MQ_APP_PASSWORD: passw0rd
      MQ_ADMIN_PASSWORD: passw0rd
    ports:
      - "1414:1414"   # MQ traffic
      - "9443:9443"   # web console (HTTPS)
    volumes:
      - qm1data:/mnt/mqm
    restart: unless-stopped

volumes:
  qm1data:
```

Then any dev can run:
```powershell
docker compose up -d        # start MQ
docker compose logs -f      # tail logs
docker compose down         # stop MQ
docker compose down -v      # stop AND wipe data (clean slate)
```

---

## Definition of Done

- All 6 acceptance scenarios pass automated tests
- `docker-compose.yml` checked in at repo root
- IBM MQ developer container running locally produces visible messages on `DEV.QUEUE.SAP.DEFECT` after a NO-GO submission
- `/Admin/Outbox` page exists and shows real counts
- Dead-letter retry button works
- Audit event `outbox.dead_lettered` fires on permanent failures
- Code review by senior dev signed off
- Demo recorded (screen capture) and shared with SAP basis team
- This document moved from `UserStories/` to `UserStories/Completed/` with PR link appended

---

## Open questions for the team

1. **SAP basis team — should we send OData JSON or IDoc XML inside the MQ message?** Default in this story: JSON. If they want IDoc, change `PayloadJson` to `PayloadXml` and adjust `IbmMqIntegrationPublisher` to serialise differently. Same outbox, different payload shape.
2. **What's our MaxRetries for production?** Default 5 = ~30 minutes of retries. SHE may want longer (24 hours) so even an overnight SAP outage doesn't dead-letter anything.
3. **Do we purge Sent rows after 30 days or keep them indefinitely?** Indefinite makes audit easier; 30-day purge keeps the table small. Recommendation: keep 90 days, purge older.

---

## Risks

- **IBM MQ licence cost in production.** The dev image is free; production licences are per-PVU and start around $5k/year for a small queue manager. Confirm corporate IT has a licence before going to prod, OR consider RabbitMQ as a free alternative (the outbox pattern works the same against either; only `IbmMqIntegrationPublisher` changes).
- **MQ admin skills are scarce in SA mining.** Most SAP basis teams know MQ administration; if yours doesn't, factor in training time.
- **Atomicity gotcha** — the outbox insert MUST happen inside the same `_db` transaction as the `DefectOrder` insert. If a future refactor moves them into separate `SaveChangesAsync` calls, atomicity is silently broken. Add a unit test that asserts both rows are in the same transaction.

---

## References

- `Services/Integrations/IIntegrationPublisher.cs` — existing interface (we re-use it)
- `Services/Integrations/SapPmIntegrationPublisher.cs` — existing HTTP scaffold (will be replaced by `IbmMqIntegrationPublisher`)
- `Services/AuditService.cs` — pattern for `BackgroundService` with `ChainLock` (similar concurrency considerations)
- `Program.cs → SeedAppSettingsAsync` — pattern for new config keys
- IBM MQ Developer docs: https://developer.ibm.com/components/ibm-mq/
- IBM XMS .NET client docs: https://www.ibm.com/docs/en/ibm-mq/9.3?topic=mq-using-net-xms

---

*Issued by TauGM · Equipment Checklist Programme · Enaleni Engineering*
