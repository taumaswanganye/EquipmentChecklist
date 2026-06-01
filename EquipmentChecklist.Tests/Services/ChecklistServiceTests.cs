using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using EquipmentChecklist.Services;
using EquipmentChecklist.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EquipmentChecklist.Tests.Services;

/// <summary>
/// Unit tests for the core business-logic service.
///
/// Split into regions by method so a green dot column can be skimmed quickly
/// in test runners.
///
/// Pattern: every test creates its own DbContext via <see cref="TestDb.Create"/>
/// — no shared state, parallel-safe.
/// </summary>
public class ChecklistServiceTests
{
    // ═════════════════════════════════════════════════════════════════════════
    //                          CalculateStatus (pure)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public void CalculateStatus_AllItemsInOrder_ReturnsGo()
    {
        var template = new List<ChecklistTemplateItem>
        {
            new() { Id = 1, IsNoGoItem = false },
            new() { Id = 2, IsNoGoItem = true  },
            new() { Id = 3, IsNoGoItem = false },
        };
        var items = template
            .Select(t => new SubmissionItem { TemplateItemId = t.Id, Status = ItemStatus.InOrder })
            .ToList();

        ChecklistService.CalculateStatus(items, template)
            .Should().Be(ChecklistStatus.Go);
    }

    [Fact]
    public void CalculateStatus_EmptyItems_ReturnsGo()
    {
        // Defensive: an empty submission (no items at all) should NOT
        // accidentally trip NO-GO. There are no defects, so Go is correct.
        ChecklistService.CalculateStatus(new(), new())
            .Should().Be(ChecklistStatus.Go);
    }

    [Fact]
    public void CalculateStatus_NonCriticalDefect_ReturnsGoButRepair24H()
    {
        var template = new List<ChecklistTemplateItem>
        {
            new() { Id = 1, IsNoGoItem = false },
            new() { Id = 2, IsNoGoItem = true  },
        };
        var items = new List<SubmissionItem>
        {
            new() { TemplateItemId = 1, Status = ItemStatus.Defect   },
            new() { TemplateItemId = 2, Status = ItemStatus.InOrder  },
        };

        ChecklistService.CalculateStatus(items, template)
            .Should().Be(ChecklistStatus.GoButRepair24H);
    }

    [Fact]
    public void CalculateStatus_CriticalDefect_ReturnsNoGo()
    {
        var template = new List<ChecklistTemplateItem>
        {
            new() { Id = 1, IsNoGoItem = false },
            new() { Id = 2, IsNoGoItem = true  },
        };
        var items = new List<SubmissionItem>
        {
            new() { TemplateItemId = 1, Status = ItemStatus.InOrder },
            new() { TemplateItemId = 2, Status = ItemStatus.Defect  },
        };

        ChecklistService.CalculateStatus(items, template)
            .Should().Be(ChecklistStatus.NoGo);
    }

    [Fact]
    public void CalculateStatus_CriticalAndNonCriticalDefects_ReturnsNoGo()
    {
        // Critical wins. A NO-GO item failing is the hardest signal — the
        // presence of additional non-critical defects can't downgrade.
        var template = new List<ChecklistTemplateItem>
        {
            new() { Id = 1, IsNoGoItem = false },
            new() { Id = 2, IsNoGoItem = true  },
        };
        var items = new List<SubmissionItem>
        {
            new() { TemplateItemId = 1, Status = ItemStatus.Defect },
            new() { TemplateItemId = 2, Status = ItemStatus.Defect },
        };

        ChecklistService.CalculateStatus(items, template)
            .Should().Be(ChecklistStatus.NoGo);
    }

    [Fact]
    public void CalculateStatus_DefectOnUnknownTemplateItem_StillTreatedAsNonCritical()
    {
        // Edge case: submission has a defect on a template item that isn't in
        // the passed template list (e.g. template was edited mid-flight).
        // We default to GO-BUT — safer than silently returning Go.
        var template = new List<ChecklistTemplateItem>
        {
            new() { Id = 99, IsNoGoItem = true },
        };
        var items = new List<SubmissionItem>
        {
            new() { TemplateItemId = 1, Status = ItemStatus.Defect },
        };

        ChecklistService.CalculateStatus(items, template)
            .Should().Be(ChecklistStatus.GoButRepair24H);
    }

    [Fact]
    public void CalculateStatus_MultipleNonCriticalDefects_StillGoButRepair24H()
    {
        var template = Enumerable.Range(1, 5)
            .Select(i => new ChecklistTemplateItem { Id = i, IsNoGoItem = false })
            .ToList();
        var items = template
            .Select(t => new SubmissionItem { TemplateItemId = t.Id, Status = ItemStatus.Defect })
            .ToList();

        ChecklistService.CalculateStatus(items, template)
            .Should().Be(ChecklistStatus.GoButRepair24H);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                       ProcessSubmissionAsync (integration)
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ProcessSubmission_AllInOrder_PersistsAsGo_WithoutImmobilising()
    {
        using var db = TestDb.Create();
        var machine = db.SeedMachineWithTemplate(itemCount: 3);
        var svc     = new ChecklistService(db);

        var dto = new SubmitChecklistDto
        {
            MachineId         = machine.Id,
            Shift             = Shift.Day,
            KmOrHourMeter     = 1000,
            OperatorSignature = "data:image/png;base64,abc",
            Items = TestData.BuildItems(
                machine.Template!.Items.ToList(),
                new[] { ItemStatus.InOrder, ItemStatus.InOrder, ItemStatus.InOrder })
        };

        var saved = await svc.ProcessSubmissionAsync(dto, TestData.OperatorId);

        saved.Status.Should().Be(ChecklistStatus.Go);
        // Machine left alone.
        (await db.Machines.FindAsync(machine.Id))!
            .IsImmobilised.Should().BeFalse();
        // No defect orders.
        (await db.DefectOrders.CountAsync())
            .Should().Be(0);
    }

    [Fact]
    public async Task ProcessSubmission_NonCriticalDefect_PersistsAsGoBut_WithoutImmobilising()
    {
        using var db = TestDb.Create();
        var machine = db.SeedMachineWithTemplate(itemCount: 2);
        var svc     = new ChecklistService(db);

        var dto = new SubmitChecklistDto
        {
            MachineId         = machine.Id,
            Shift             = Shift.Day,
            KmOrHourMeter     = 1000,
            OperatorSignature = "data:image/png;base64,abc",
            Items = TestData.BuildItems(
                machine.Template!.Items.ToList(),
                new[] { ItemStatus.Defect, ItemStatus.InOrder })
        };

        var saved = await svc.ProcessSubmissionAsync(dto, TestData.OperatorId);

        saved.Status.Should().Be(ChecklistStatus.GoButRepair24H);
        (await db.Machines.FindAsync(machine.Id))!.IsImmobilised.Should().BeFalse();
        // GO-BUT does NOT auto-create defect orders — those wait for
        // supervisor sign-off / reject flow.
        (await db.DefectOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ProcessSubmission_CriticalDefect_Immobilises_AndCreatesDefectOrder()
    {
        using var db = TestDb.Create();
        // Item 0 is critical.
        var machine = db.SeedMachineWithTemplate(itemCount: 2, noGoIndexes: new[] { 0 });
        var svc     = new ChecklistService(db);

        var dto = new SubmitChecklistDto
        {
            MachineId         = machine.Id,
            Shift             = Shift.Night,
            KmOrHourMeter     = 1234,
            OperatorSignature = "data:image/png;base64,abc",
            Items = TestData.BuildItems(
                machine.Template!.Items.ToList(),
                new[] { ItemStatus.Defect, ItemStatus.InOrder })
        };

        var saved = await svc.ProcessSubmissionAsync(dto, TestData.OperatorId);

        saved.Status.Should().Be(ChecklistStatus.NoGo);

        var m = await db.Machines.FindAsync(machine.Id);
        m!.IsImmobilised.Should().BeTrue();
        m.ImmobilisedReason.Should().Contain(TestData.OperatorId);

        // One defect order created (one defective item).
        var orders = await db.DefectOrders.ToListAsync();
        orders.Should().HaveCount(1);
        // No assigned mechanic yet → Pending + null assignment.
        orders[0].AssignedMechanicId.Should().BeNull();
        orders[0].RepairStatus.Should().Be(RepairStatus.Pending);
    }

    [Fact]
    public async Task ProcessSubmission_CriticalDefect_RoutesToAssignedMechanic()
    {
        using var db = TestDb.Create();
        var machine = db.SeedMachineWithTemplate(
            itemCount: 2,
            noGoIndexes: new[] { 1 },
            mechanicId:  TestData.MechanicId);
        var svc = new ChecklistService(db);

        var dto = new SubmitChecklistDto
        {
            MachineId         = machine.Id,
            Shift             = Shift.Day,
            KmOrHourMeter     = 500,
            OperatorSignature = "data:image/png;base64,abc",
            Items = TestData.BuildItems(
                machine.Template!.Items.ToList(),
                new[] { ItemStatus.InOrder, ItemStatus.Defect })
        };

        var saved = await svc.ProcessSubmissionAsync(dto, TestData.OperatorId);

        saved.Status.Should().Be(ChecklistStatus.NoGo);

        var order = (await db.DefectOrders.ToListAsync()).Single();
        order.AssignedMechanicId.Should().Be(TestData.MechanicId);
        // Has a mechanic → InProgress straight away (no Pending hand-off).
        order.RepairStatus.Should().Be(RepairStatus.InProgress);
    }

    [Fact]
    public async Task ProcessSubmission_UnknownMachine_Throws()
    {
        using var db = TestDb.Create();
        var svc = new ChecklistService(db);

        var dto = new SubmitChecklistDto
        {
            MachineId         = 9999,
            Shift             = Shift.Day,
            OperatorSignature = "x",
            Items             = new List<SubmissionItemDto>()
        };

        var act = async () => await svc.ProcessSubmissionAsync(dto, TestData.OperatorId);

        await act.Should().ThrowAsync<Exception>().WithMessage("*Machine not found*");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                          SupervisorSignOffAsync
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SupervisorSignOff_OnGoSubmission_Throws()
    {
        using var db = TestDb.Create();
        var sub = new ChecklistSubmission { Status = ChecklistStatus.Go };
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();
        var svc = new ChecklistService(db);

        var act = async () => await svc.SupervisorSignOffAsync(
            sub.Id, TestData.SupervisorId, ChecklistStatus.GoButRepair24H, "sig");

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*Only GO-BUT submissions require supervisor sign-off*");
    }

    [Fact]
    public async Task SupervisorSignOff_WithoutSignature_Throws()
    {
        using var db = TestDb.Create();
        var sub = new ChecklistSubmission { Status = ChecklistStatus.GoButRepair24H };
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();
        var svc = new ChecklistService(db);

        var act = async () => await svc.SupervisorSignOffAsync(
            sub.Id, TestData.SupervisorId, ChecklistStatus.GoButRepair24H, signaturePng: null);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*signature is required*");
    }

    [Theory]
    [InlineData(ChecklistStatus.GoButRepair24H)]
    [InlineData(ChecklistStatus.GoTillNextService)]
    public async Task SupervisorSignOff_ValidPath_StampsApproval(ChecklistStatus startStatus)
    {
        using var db = TestDb.Create();
        var sub = new ChecklistSubmission { Status = startStatus };
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();
        var svc = new ChecklistService(db);

        await svc.SupervisorSignOffAsync(
            sub.Id, TestData.SupervisorId,
            resolvedStatus: ChecklistStatus.GoTillNextService,
            signaturePng:   "data:image/png;base64,abc");

        var reloaded = await db.ChecklistSubmissions.FindAsync(sub.Id);
        reloaded!.Status.Should().Be(ChecklistStatus.GoTillNextService);
        reloaded.SupervisorId.Should().Be(TestData.SupervisorId);
        reloaded.SupervisorSignedAt.Should().NotBeNull();
        reloaded.SupervisorSignature.Should().Be("data:image/png;base64,abc");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //                          ResolveDefectAsync
    // ═════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveDefect_WithoutSignature_Throws()
    {
        using var db = TestDb.Create();
        var machine = db.SeedMachineWithTemplate(itemCount: 1, noGoIndexes: new[] { 0 });
        var sub     = new ChecklistSubmission
        {
            MachineId  = machine.Id,
            OperatorId = TestData.OperatorId,
            Status     = ChecklistStatus.NoGo
        };
        var item = new SubmissionItem
        {
            TemplateItemId = machine.Template!.Items.First().Id,
            Status         = ItemStatus.Defect,
            Submission     = sub
        };
        sub.Items.Add(item);
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();

        var order = new DefectOrder
        {
            SubmissionId     = sub.Id,
            SubmissionItemId = item.Id,
            RepairStatus     = RepairStatus.InProgress
        };
        db.DefectOrders.Add(order);
        await db.SaveChangesAsync();
        var svc = new ChecklistService(db);

        var act = async () => await svc.ResolveDefectAsync(
            order.Id, TestData.MechanicId, "fixed", signaturePng: null);

        await act.Should().ThrowAsync<Exception>()
            .WithMessage("*signature is required*");
    }

    [Fact]
    public async Task ResolveDefect_LastOpenDefectOnMachine_ClearsImmobilisation()
    {
        using var db = TestDb.Create();
        var machine = db.SeedMachineWithTemplate(itemCount: 1, noGoIndexes: new[] { 0 });
        machine.IsImmobilised     = true;
        machine.ImmobilisedReason = "NO-GO on Brakes";
        await db.SaveChangesAsync();

        var sub = new ChecklistSubmission
        {
            MachineId  = machine.Id,
            OperatorId = TestData.OperatorId,
            Status     = ChecklistStatus.NoGo
        };
        var item = new SubmissionItem
        {
            TemplateItemId = machine.Template!.Items.First().Id,
            Status         = ItemStatus.Defect,
            Submission     = sub
        };
        sub.Items.Add(item);
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();

        var order = new DefectOrder
        {
            SubmissionId     = sub.Id,
            SubmissionItemId = item.Id,
            RepairStatus     = RepairStatus.InProgress
        };
        db.DefectOrders.Add(order);
        await db.SaveChangesAsync();
        var svc = new ChecklistService(db);

        await svc.ResolveDefectAsync(
            order.Id, TestData.MechanicId, "brakes replaced",
            signaturePng: "data:image/png;base64,abc");

        // The order itself is closed.
        var reloadedOrder = await db.DefectOrders.FindAsync(order.Id);
        reloadedOrder!.RepairStatus.Should().Be(RepairStatus.Completed);
        reloadedOrder.AssignedMechanicId.Should().Be(TestData.MechanicId);
        reloadedOrder.MechanicSignature.Should().Be("data:image/png;base64,abc");
        reloadedOrder.ResolvedAt.Should().NotBeNull();

        // The machine is back in service because no defects remain on it.
        var reloadedMachine = await db.Machines.FindAsync(machine.Id);
        reloadedMachine!.IsImmobilised.Should().BeFalse();
        reloadedMachine.ImmobilisedReason.Should().BeNull();
    }

    [Fact]
    public async Task ResolveDefect_WhenOtherDefectsRemain_KeepsMachineImmobilised()
    {
        using var db = TestDb.Create();
        var machine = db.SeedMachineWithTemplate(itemCount: 2, noGoIndexes: new[] { 0, 1 });
        machine.IsImmobilised     = true;
        machine.ImmobilisedReason = "NO-GO on Brakes + Steering";
        await db.SaveChangesAsync();

        var sub = new ChecklistSubmission
        {
            MachineId  = machine.Id,
            OperatorId = TestData.OperatorId,
            Status     = ChecklistStatus.NoGo
        };
        var item1 = new SubmissionItem
        {
            TemplateItemId = machine.Template!.Items.ElementAt(0).Id,
            Status         = ItemStatus.Defect,
            Submission     = sub
        };
        var item2 = new SubmissionItem
        {
            TemplateItemId = machine.Template!.Items.ElementAt(1).Id,
            Status         = ItemStatus.Defect,
            Submission     = sub
        };
        sub.Items.Add(item1); sub.Items.Add(item2);
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();

        var order1 = new DefectOrder { SubmissionId = sub.Id, SubmissionItemId = item1.Id, RepairStatus = RepairStatus.InProgress };
        var order2 = new DefectOrder { SubmissionId = sub.Id, SubmissionItemId = item2.Id, RepairStatus = RepairStatus.InProgress };
        db.DefectOrders.AddRange(order1, order2);
        await db.SaveChangesAsync();

        var svc = new ChecklistService(db);

        // Close ONE of two open defects.
        await svc.ResolveDefectAsync(
            order1.Id, TestData.MechanicId, "fixed brakes",
            signaturePng: "data:image/png;base64,abc");

        // The other defect is still open → machine stays immobilised.
        var reloadedMachine = await db.Machines.FindAsync(machine.Id);
        reloadedMachine!.IsImmobilised.Should().BeTrue();
        reloadedMachine.ImmobilisedReason.Should().NotBeNull();
    }
}
