using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EquipmentChecklist.Tests.Helpers;

/// <summary>
/// Per-test seeding helpers that complement the factory-level user/role
/// seed. Keeps each test's setup compact while still creating real
/// machines, templates, and submissions for the role-aware endpoints
/// to reason about.
/// </summary>
public static class ApiTestSeed
{
    /// <summary>
    /// Insert a machine + template + items so the supervisor / mechanic
    /// endpoints have something to return. Returns the machine ID for
    /// the test to assert against.
    /// </summary>
    public static async Task<int> SeedMachineAsync(this ApiTestFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var machine = new Machine
        {
            MachineNumber = $"M-{Guid.NewGuid().ToString("N")[..6]}",
            MachineName   = "Test Machine",
            Type          = MachineType.Grader,
            IsActive      = true
        };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();

        var template = new ChecklistTemplate
        {
            Name        = "Test Template",
            MachineType = MachineType.Grader,
            MachineId   = machine.Id
        };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        db.ChecklistTemplateItems.Add(new ChecklistTemplateItem
        {
            TemplateId = template.Id,
            ItemName   = "Brakes",
            SortOrder  = 0,
            IsNoGoItem = true
        });
        await db.SaveChangesAsync();

        return machine.Id;
    }

    /// <summary>
    /// Insert a GO-BUT submission landing in the sign-off queue, owned by
    /// the operator with <paramref name="operatorEmail"/>. Returns the
    /// SubmissionId.
    /// </summary>
    public static async Task<int> SeedPendingSubmissionAsync(
        this ApiTestFactory f, int machineId, string operatorEmail)
    {
        using var scope = f.Services.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var op    = await users.FindByEmailAsync(operatorEmail)
                    ?? throw new Exception($"Operator {operatorEmail} not seeded");

        var sub = new ChecklistSubmission
        {
            MachineId   = machineId,
            OperatorId  = op.Id,
            Status      = ChecklistStatus.GoButRepair24H,
            Shift       = Shift.Day,
            SubmittedAt = DateTime.UtcNow
        };
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();
        return sub.Id;
    }

    /// <summary>
    /// Wire the given mechanic to the machine via a <see cref="MachineAssignment"/>.
    /// Required for the PDF/Details endpoints' mechanic access check —
    /// without an active assignment, a mechanic shouldn't be able to view
    /// submissions for that machine.
    /// </summary>
    public static async Task AssignMechanicAsync(
        this ApiTestFactory f, int machineId, string mechanicEmail,
        string? operatorEmail = null)
    {
        using var scope = f.Services.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var mech = await users.FindByEmailAsync(mechanicEmail)
                   ?? throw new Exception($"Mechanic {mechanicEmail} not seeded");
        var op   = operatorEmail is null ? null : await users.FindByEmailAsync(operatorEmail);

        db.MachineAssignments.Add(new MachineAssignment
        {
            MachineId    = machineId,
            OperatorId   = op?.Id ?? "",
            MechanicId   = mech.Id,
            IsActive     = true,
            AssignedFrom = DateTime.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Lookup helper — returns the seeded user's Id. Useful when a test
    /// needs to send a UserId in a request body (e.g. supervisor reject's
    /// mechanic-FK field).
    /// </summary>
    public static async Task<string> UserIdAsync(
        this ApiTestFactory f, string email)
    {
        using var scope = f.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var u = await users.FindByEmailAsync(email)
                ?? throw new Exception($"User {email} not seeded");
        return u.Id;
    }

    /// <summary>
    /// Create a NO-GO submission + one defect order sitting unassigned in
    /// the mechanic queue. Returns (submissionId, defectOrderId).
    /// </summary>
    public static async Task<(int submissionId, int defectOrderId)>
        SeedUnassignedDefectAsync(this ApiTestFactory f, int machineId, string operatorEmail)
    {
        using var scope = f.Services.CreateScope();
        var db    = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var op    = await users.FindByEmailAsync(operatorEmail)
                    ?? throw new Exception($"Operator {operatorEmail} not seeded");

        // We need a real template item for the SubmissionItem FK.
        var tplItem = await db.ChecklistTemplateItems
            .Where(i => i.Template.MachineId == machineId)
            .FirstOrDefaultAsync()
            ?? throw new Exception($"No template items seeded for machine {machineId}");

        var sub = new ChecklistSubmission
        {
            MachineId   = machineId,
            OperatorId  = op.Id,
            Status      = ChecklistStatus.NoGo,
            Shift       = Shift.Day,
            SubmittedAt = DateTime.UtcNow
        };
        db.ChecklistSubmissions.Add(sub);
        await db.SaveChangesAsync();

        var item = new SubmissionItem
        {
            SubmissionId   = sub.Id,
            TemplateItemId = tplItem.Id,
            Status         = ItemStatus.Defect,
            Notes          = "test defect"
        };
        db.SubmissionItems.Add(item);
        await db.SaveChangesAsync();

        var order = new DefectOrder
        {
            SubmissionId       = sub.Id,
            SubmissionItemId   = item.Id,
            DefectDescription  = "test defect",
            AssignedMechanicId = null,
            RepairStatus       = RepairStatus.Pending,
            CreatedAt          = DateTime.UtcNow
        };
        db.DefectOrders.Add(order);
        await db.SaveChangesAsync();
        return (sub.Id, order.Id);
    }
}
