using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

public class ChecklistService
{
    private readonly ApplicationDbContext _db;
    private readonly NotificationService? _notifications;
    private readonly AuditService?        _audit;

    // The notification + audit services are optional so existing tests that
    // construct `new ChecklistService(db)` don't need to change — they
    // exercise pure status calc / persistence and aren't asserting on
    // the side effects.
    public ChecklistService(ApplicationDbContext db,
                            NotificationService? notifications = null,
                            AuditService?        audit         = null)
    {
        _db            = db;
        _notifications = notifications;
        _audit         = audit;
    }

    /// <summary>
    /// Processes a submitted checklist, computes the GO/NO-GO/GO-BUT status,
    /// immobilises the machine if required, and creates defect orders.
    /// </summary>
    public async Task<ChecklistSubmission> ProcessSubmissionAsync(SubmitChecklistDto dto, string operatorId)
    {
        var machine = await _db.Machines
            .Include(m => m.Template)
            .ThenInclude(t => t!.Items)
            .FirstOrDefaultAsync(m => m.Id == dto.MachineId)
            ?? throw new Exception("Machine not found");

        var submission = new ChecklistSubmission
        {
            MachineId = dto.MachineId,
            OperatorId = operatorId,
            Shift = dto.Shift,
            KmOrHourMeter = dto.KmOrHourMeter,
            OperatorRemarks = dto.OperatorRemarks,
            FitnessDeclarationSigned = dto.FitnessDeclarationSigned,
            OperatorSignature = dto.OperatorSignature,
            SubmittedAt = DateTime.UtcNow
        };

        var submissionItems = dto.Items.Select(i => new SubmissionItem
        {
            TemplateItemId = i.TemplateItemId,
            Status         = i.Status,
            Notes          = i.Notes,
            // Decode the optional defect photo. Stored inline as bytea —
            // small enough that one query pulls it back with the submission,
            // big enough that we'll want to add a compression pass later.
            PhotoData      = string.IsNullOrEmpty(i.PhotoBase64)
                                 ? null
                                 : Convert.FromBase64String(i.PhotoBase64),
            PhotoMimeType  = i.PhotoMimeType,
            // Same shape as the photo: decode base64 → bytea column. Null when
            // the operator didn't record anything (always null on InOrder items).
            AudioData      = string.IsNullOrEmpty(i.AudioBase64)
                                 ? null
                                 : Convert.FromBase64String(i.AudioBase64),
            AudioMimeType  = i.AudioMimeType,
            Submission     = submission
        }).ToList();

        submission.Items = submissionItems;
        submission.Status = CalculateStatus(submissionItems, machine.Template!.Items.ToList());

        // Immobilise machine on NO-GO
        if (submission.Status == ChecklistStatus.NoGo)
        {
            machine.IsImmobilised = true;
            machine.ImmobilisedReason = $"NO-GO defect on {DateTime.UtcNow:yyyy-MM-dd HH:mm} by operator {operatorId}";
        }

        _db.ChecklistSubmissions.Add(submission);
        await _db.SaveChangesAsync();

        // On NO-GO, immediately create a DefectOrder per defective item and assign
        // it to the machine's currently-responsible mechanic so they see the work
        // (and the Order-Parts buttons) in their dashboard without waiting for a
        // supervisor rejection step.
        if (submission.Status == ChecklistStatus.NoGo)
        {
            var assignedMechanicId = await _db.MachineAssignments
                .Where(a => a.MachineId == machine.Id && a.IsActive && a.MechanicId != null)
                .OrderByDescending(a => a.AssignedFrom)
                .Select(a => a.MechanicId)
                .FirstOrDefaultAsync();

            var defects = submissionItems.Where(i => i.Status == ItemStatus.Defect).ToList();
            foreach (var d in defects)
            {
                _db.DefectOrders.Add(new DefectOrder
                {
                    SubmissionId       = submission.Id,
                    SubmissionItemId   = d.Id,
                    DefectDescription  = (d.Notes ?? d.TemplateItem.ItemName).Length > 200
                                            ? (d.Notes ?? d.TemplateItem.ItemName).Substring(0, 200)
                                            : (d.Notes ?? d.TemplateItem.ItemName),
                    AssignedMechanicId = assignedMechanicId,
                    RepairStatus       = assignedMechanicId == null
                                            ? RepairStatus.Pending
                                            : RepairStatus.InProgress,
                    CreatedAt          = DateTime.UtcNow
                });
            }
            if (defects.Any()) await _db.SaveChangesAsync();
        }

        // ── Notify the operator's supervisor ──
        // GO submissions are routine — no notification.
        // GO-BUT and NO-GO need supervisor eyeballs, so we ping them.
        if (_notifications is not null &&
            (submission.Status == ChecklistStatus.NoGo ||
             submission.Status == ChecklistStatus.GoButRepair24H))
        {
            await NotifyOperatorsSupervisorAsync(submission, machine);
        }

        // ── Audit: one append-only row per submission ──
        // Fired AFTER the SaveChanges above so we don't write an audit
        // row for a submission that failed to persist. Includes the rolled-up
        // status + defect count in PayloadJson so the admin trail is useful
        // at-a-glance without joining back to ChecklistSubmissions.
        if (_audit is not null)
        {
            await _audit.LogAsync(
                action:     AuditActions.SubmissionCreated,
                targetType: "Submission",
                targetId:   submission.Id,
                payload:    new
                {
                    machineId       = machine.Id,
                    machineNumber   = machine.MachineNumber,
                    status          = submission.Status.ToString(),
                    defectCount     = submission.Items.Count(i => i.Status == ItemStatus.Defect),
                    immobilised     = submission.Status == ChecklistStatus.NoGo
                });
        }

        return submission;
    }

    /// <summary>
    /// Find the supervisor(s) currently assigned to this operator and push
    /// a notification so they don't have to refresh the queue to learn the
    /// machine needs attention.
    /// </summary>
    private async Task NotifyOperatorsSupervisorAsync(
        ChecklistSubmission submission, Machine machine)
    {
        if (_notifications is null) return;

        var supervisorIds = await _db.OperatorSupervisorAssignments
            .Where(a => a.OperatorId == submission.OperatorId && a.IsActive)
            .Select(a => a.SupervisorId)
            .Distinct()
            .ToListAsync();

        var (kind, title, body) = submission.Status switch
        {
            ChecklistStatus.NoGo => (
                NotificationKinds.SubmissionNoGo,
                $"🚫 NO-GO submitted on {machine.MachineNumber}",
                $"{machine.MachineName} immobilised — defect orders raised."
            ),
            ChecklistStatus.GoButRepair24H => (
                NotificationKinds.SubmissionGoBut,
                $"✍ Sign-off needed on {machine.MachineNumber}",
                $"{machine.MachineName} reported defects — review and approve."
            ),
            _ => ("", "", "")
        };

        foreach (var supId in supervisorIds)
        {
            await _notifications.PushAsync(
                userId:              supId,
                kind:                kind,
                title:               title,
                body:                body,
                relatedSubmissionId: submission.Id,
                relatedMachineId:    machine.Id);
        }
    }

    /// <summary>
    /// Calculates overall status based on which items have defects and
    /// whether any are flagged as NO-GO items.
    /// </summary>
    public static ChecklistStatus CalculateStatus(
        List<SubmissionItem> items,
        List<ChecklistTemplateItem> templateItems)
    {
        var defects = items.Where(i => i.Status == ItemStatus.Defect).ToList();

        if (!defects.Any()) return ChecklistStatus.Go;

        // Any defect on a NO-GO template item → immediate NO-GO
        var noGoItemIds = templateItems.Where(t => t.IsNoGoItem).Select(t => t.Id).ToHashSet();
        if (defects.Any(d => noGoItemIds.Contains(d.TemplateItemId)))
            return ChecklistStatus.NoGo;

        // All other defects → GO-BUT, requiring supervisor sign-off
        return ChecklistStatus.GoButRepair24H;
    }

    /// <summary>
    /// Thrown when a write loses a race against a concurrent write — e.g.
    /// two supervisors offline-approve the same submission and the second
    /// one's drain finds the row already signed by the first. Carries
    /// enough detail for the caller to build a useful "you lost the race"
    /// notification for the operator/supervisor who got bumped.
    ///
    /// <para>Controllers map this to HTTP 409 Conflict.</para>
    /// </summary>
    public class ConflictException : Exception
    {
        public string  WinnerName    { get; }
        public string? WinnerId      { get; }
        public string? MachineNumber { get; }
        public string? MachineName   { get; }

        public ConflictException(string message,
                                 string winnerName,
                                 string? winnerId      = null,
                                 string? machineNumber = null,
                                 string? machineName   = null)
            : base(message)
        {
            WinnerName    = winnerName;
            WinnerId      = winnerId;
            MachineNumber = machineNumber;
            MachineName   = machineName;
        }
    }

    /// <summary>
    /// Supervisor approves a GO-BUT submission (status W).
    /// </summary>
    public async Task SupervisorSignOffAsync(int submissionId, string supervisorId, ChecklistStatus resolvedStatus, string? signaturePng = null)
    {
        var submission = await _db.ChecklistSubmissions
            .Include(s => s.Machine)
            .Include(s => s.Supervisor)
            .FirstOrDefaultAsync(s => s.Id == submissionId)
            ?? throw new Exception("Submission not found");

        // ── Conflict detection ───────────────────────────────────────────
        // If someone else already signed (the winner has a non-null
        // SupervisorId that's not us) we DO NOT silently overwrite. This
        // is the case the offline-drain queues hit when two supervisors
        // independently approve the same submission and the second one's
        // drain lands. Throw a ConflictException so the controller can
        // both notify the loser AND return 409 instead of overwriting.
        if (!string.IsNullOrEmpty(submission.SupervisorId) &&
            submission.SupervisorId != supervisorId)
        {
            var winner = submission.Supervisor?.FullName ?? "another supervisor";
            throw new ConflictException(
                message:       $"This submission was already signed off by {winner}.",
                winnerName:    winner,
                winnerId:      submission.SupervisorId,
                machineNumber: submission.Machine?.MachineNumber,
                machineName:   submission.Machine?.MachineName);
        }

        if (submission.Status is not (ChecklistStatus.GoButRepair24H or ChecklistStatus.GoTillNextService))
            throw new Exception("Only GO-BUT submissions require supervisor sign-off");

        if (string.IsNullOrWhiteSpace(signaturePng))
            throw new Exception("A digital signature is required to approve this submission.");

        submission.SupervisorId = supervisorId;
        submission.SupervisorSignedAt = DateTime.UtcNow;
        submission.SupervisorSignature = signaturePng;
        submission.Status = resolvedStatus;
        await _db.SaveChangesAsync();

        // Audit row written AFTER the save so an aborted commit doesn't
        // leave us claiming a sign-off that never happened.
        if (_audit is not null)
        {
            await _audit.LogAsync(
                action:     AuditActions.SubmissionSignedOff,
                targetType: "Submission",
                targetId:   submission.Id,
                payload:    new
                {
                    operatorId   = submission.OperatorId,
                    resolution   = resolvedStatus.ToString(),
                    supervisorId = supervisorId
                });
        }

        // Notify the operator that their submission has a verdict.
        if (_notifications is not null)
        {
            var machine = await _db.Machines.FindAsync(submission.MachineId);
            var resolutionLabel = resolvedStatus == ChecklistStatus.GoTillNextService
                ? "GO-BUT · repair by next service"
                : "GO-BUT · repair within 24H";
            await _notifications.PushAsync(
                userId:              submission.OperatorId,
                kind:                NotificationKinds.SubmissionApproved,
                title:               $"✓ Approved on {machine?.MachineNumber ?? "your submission"}",
                body:                resolutionLabel,
                relatedSubmissionId: submission.Id,
                relatedMachineId:    submission.MachineId);
        }
    }

    /// <summary>
    /// Mechanic marks a defect order as resolved and clears machine immobilisation if all defects resolved.
    /// </summary>
    public async Task ResolveDefectAsync(int defectOrderId, string mechanicId, string notes, string? signaturePng = null)
    {
        var order = await _db.DefectOrders
            .Include(d => d.Submission)
            .ThenInclude(s => s.Machine)
            .Include(d => d.AssignedMechanic)
            .FirstOrDefaultAsync(d => d.Id == defectOrderId)
            ?? throw new Exception("Defect order not found");

        // ── Conflict detection ───────────────────────────────────────────
        // Defect already marked Completed by another mechanic. Refusing
        // prevents the second mechanic's signature from overwriting the
        // first's, which would corrupt the audit trail on the repair.
        if (order.RepairStatus == RepairStatus.Completed &&
            !string.IsNullOrEmpty(order.AssignedMechanicId) &&
            order.AssignedMechanicId != mechanicId)
        {
            var winner = order.AssignedMechanic?.FullName ?? "another mechanic";
            throw new ConflictException(
                message:       $"This defect was already closed by {winner}.",
                winnerName:    winner,
                winnerId:      order.AssignedMechanicId,
                machineNumber: order.Submission?.Machine?.MachineNumber,
                machineName:   order.Submission?.Machine?.MachineName);
        }

        if (string.IsNullOrWhiteSpace(signaturePng))
            throw new Exception("A digital signature is required to close this defect.");

        order.RepairStatus = RepairStatus.Completed;
        order.AssignedMechanicId = mechanicId;
        order.ResolvedAt = DateTime.UtcNow;
        order.ResolutionNotes = notes;
        order.MechanicSignature = signaturePng;

        // ── Admin-clearance gate ────────────────────────────────────────
        //
        // Previous behaviour: when all defects on an immobilised machine
        // became Completed, we cleared IsImmobilised automatically. The
        // mechanic's word was enough — no admin sign-off.
        //
        // New behaviour: when the last defect closes, the machine flips
        // to AwaitingAdminClearance. IsImmobilised STAYS true so operators
        // cannot start a checklist on it. An admin must explicitly clear
        // the machine before it returns to service. This protects against
        // a mechanic marking work fixed prematurely.
        var machine = order.Submission.Machine;
        var hasUnresolved = await _db.DefectOrders
            .Where(d => d.Submission.MachineId == machine.Id
                && d.RepairStatus != RepairStatus.Completed)
            .AnyAsync();

        var awaitingClearance = false;
        if (!hasUnresolved && machine.IsImmobilised && !machine.AwaitingAdminClearance)
        {
            // Hold the machine in "awaiting clearance" — don't drop the
            // immobilisation flag. Admin will release via /Admin/ClearMachine.
            machine.AwaitingAdminClearance = true;
            awaitingClearance = true;
        }

        await _db.SaveChangesAsync();

        // Audit: the defect completion always logs. The "awaiting clearance"
        // transition gets its own audit row so the trail walks cleanly from
        // mechanic action → admin action.
        if (_audit is not null)
        {
            await _audit.LogAsync(
                action:     AuditActions.DefectCompleted,
                targetType: "DefectOrder",
                targetId:   order.Id,
                payload:    new
                {
                    machineId         = machine.Id,
                    machineNumber     = machine.MachineNumber,
                    mechanicId        = mechanicId,
                    awaitingClearance = awaitingClearance
                });
        }

        // Notify the operator that their repair landed (but the machine
        // is still down until admin clears).
        if (_notifications is not null)
        {
            var operatorId = order.Submission.OperatorId;
            var title = awaitingClearance
                ? $"🔧 {machine.MachineNumber} ready for admin clearance"
                : $"🔧 Repair logged on {machine.MachineNumber}";
            var body = awaitingClearance
                ? "All defects resolved — awaiting admin sign-off before the machine returns to service."
                : $"Mechanic closed: {notes}";
            await _notifications.PushAsync(
                userId:              operatorId,
                kind:                NotificationKinds.DefectResolved,
                title:               title,
                body:                body,
                relatedSubmissionId: order.SubmissionId,
                relatedMachineId:    machine.Id);

            // Tell every admin so somebody picks it up.
            if (awaitingClearance)
            {
                await NotifyAdminsAsync(
                    kind:  NotificationKinds.MachineAwaitingClearance,
                    title: $"⏳ {machine.MachineNumber} awaiting clearance",
                    body:  $"{machine.MachineName} — all defects resolved by mechanic. Inspect and clear when ready.",
                    relatedMachineId: machine.Id);
            }
        }
    }

    /// <summary>
    /// Push the same notification to every active Admin. Uses the
    /// <see cref="UserManager{TUser}"/> from the consumer when available
    /// — if Admin lookup fails, swallow so the caller's work proceeds.
    /// </summary>
    private async Task NotifyAdminsAsync(
        string kind, string title, string? body = null,
        int? relatedSubmissionId = null, int? relatedMachineId = null)
    {
        if (_notifications is null) return;
        try
        {
            // Pull admin user ids straight from the role-link table — avoids
            // dragging UserManager into ChecklistService just for this.
            var adminIds = await (
                from ur in _db.UserRoles
                join r  in _db.Roles on ur.RoleId equals r.Id
                where r.Name == "Admin"
                select ur.UserId).Distinct().ToListAsync();

            foreach (var id in adminIds)
            {
                await _notifications.PushAsync(
                    userId:              id,
                    kind:                kind,
                    title:               title,
                    body:                body,
                    relatedSubmissionId: relatedSubmissionId,
                    relatedMachineId:    relatedMachineId);
            }
        }
        catch
        {
            // Admin notification failures must never break the surrounding
            // mechanic write. The defect.completed audit row already proves
            // the action happened.
        }
    }

    public async Task SaveSubmissionOfflineAsync(ChecklistSubmission submission, LocalDbContext localDb)
    {
        // 1. Generate a unique LocalId for idempotency
        submission.LocalId = Guid.NewGuid();
        submission.SubmittedAt = DateTime.UtcNow;
        submission.IsSyncedToCloud = false;

        // 2. Save the submission and its items to SQLite
        localDb.ChecklistSubmissions.Add(submission);

        // 3. Queue it for the Background SyncService
        var syncRecord = new PendingSyncRecord
        {
            LocalSubmissionId = submission.LocalId,
            QueuedAt = DateTime.UtcNow,
            RetryCount = 0
        };
        localDb.PendingSyncRecords.Add(syncRecord);

        await localDb.SaveChangesAsync();
    }

    public async Task<DashboardStatsDto> GetDashboardStatsAsync()
    {
        var today = DateTime.UtcNow.Date;

        return new DashboardStatsDto
        {
            TotalMachines = await _db.Machines.CountAsync(m => m.IsActive),
            ImmobilisedMachines = await _db.Machines.CountAsync(m => m.IsImmobilised),
            GoMachines = await _db.Machines.CountAsync(m => !m.IsImmobilised && m.IsActive),
            PendingDefects = await _db.DefectOrders.CountAsync(d => d.RepairStatus != RepairStatus.Completed),
            TodaySubmissions = await _db.ChecklistSubmissions.CountAsync(s => s.SubmittedAt >= today),
            RecentSubmissions = await _db.ChecklistSubmissions
                .Include(s => s.Machine)
                .Include(s => s.Operator)
                .OrderByDescending(s => s.SubmittedAt)
                .Take(10)
                .Select(s => new RecentSubmissionDto
                {
                    SubmissionId = s.Id,
                    MachineName = s.Machine.MachineName,
                    MachineNumber = s.Machine.MachineNumber,
                    OperatorName = s.Operator.FullName,
                    Status = s.Status,
                    SubmittedAt = s.SubmittedAt
                })
                .ToListAsync()
        };
    }
}
