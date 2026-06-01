using EquipmentChecklist.Data;
using EquipmentChecklist.Models;

namespace EquipmentChecklist.Tests.Helpers;

/// <summary>
/// Fluent helpers for seeding the in-memory database with the minimum
/// graph each test needs. Saves every test from rewriting machine /
/// template / item / user boilerplate.
///
/// Convention: every helper returns the inserted entity (or list) so the
/// test body can hold on to the ID for assertions.
/// </summary>
public static class TestData
{
    public const string OperatorId   = "op-1";
    public const string SupervisorId = "sup-1";
    public const string MechanicId   = "mech-1";

    /// <summary>
    /// Insert one machine + one template + N items. Items default to NOT-NO-GO
    /// (regular defect = GO-BUT). Mark specific indexes as NO-GO via the
    /// <paramref name="noGoIndexes"/> list.
    /// </summary>
    public static Machine SeedMachineWithTemplate(
        this ApplicationDbContext db,
        int itemCount = 3,
        IReadOnlyCollection<int>? noGoIndexes = null,
        string? mechanicId = null)
    {
        var machine = new Machine
        {
            MachineNumber = "GRD-001",
            MachineName   = "Test Grader",
            Type          = MachineType.Grader,
            IsActive      = true,
            IsImmobilised = false
        };
        db.Machines.Add(machine);
        db.SaveChanges();  // get an ID for FK on template

        var template = new ChecklistTemplate
        {
            MachineType = MachineType.Grader,
            Name        = "Pre-shift Grader",
            MachineId   = machine.Id
        };
        db.ChecklistTemplates.Add(template);
        db.SaveChanges();

        noGoIndexes ??= Array.Empty<int>();
        for (int i = 0; i < itemCount; i++)
        {
            db.ChecklistTemplateItems.Add(new ChecklistTemplateItem
            {
                TemplateId = template.Id,
                ItemName   = $"Item {i + 1}",
                SortOrder  = i,
                IsNoGoItem = noGoIndexes.Contains(i)
            });
        }
        db.SaveChanges();

        // Reload with template + items eagerly available for the service.
        machine = db.Machines
            .Where(m => m.Id == machine.Id)
            .Select(m => m)
            .First();
        machine.Template = db.ChecklistTemplates
            .Where(t => t.Id == template.Id)
            .Select(t => t)
            .First();
        machine.Template.Items = db.ChecklistTemplateItems
            .Where(i => i.TemplateId == template.Id)
            .OrderBy(i => i.SortOrder)
            .ToList();

        // Optional: assign a responsible mechanic so the NO-GO path can
        // create defect orders with InProgress + mechanic FK populated.
        if (!string.IsNullOrEmpty(mechanicId))
        {
            db.MachineAssignments.Add(new MachineAssignment
            {
                MachineId    = machine.Id,
                OperatorId   = OperatorId,
                MechanicId   = mechanicId,
                IsActive     = true,
                AssignedFrom = DateTime.UtcNow.AddDays(-1)
            });
            db.SaveChanges();
        }

        return machine;
    }

    /// <summary>
    /// Build the SubmissionItem list (NOT yet attached to a submission) the
    /// service expects in <c>SubmitChecklistDto.Items</c>.
    /// </summary>
    public static List<EquipmentChecklist.DTOs.SubmissionItemDto> BuildItems(
        IReadOnlyList<ChecklistTemplateItem> templateItems,
        IReadOnlyList<ItemStatus> statuses)
    {
        if (templateItems.Count != statuses.Count)
            throw new ArgumentException(
                "templateItems.Count must equal statuses.Count");

        var list = new List<EquipmentChecklist.DTOs.SubmissionItemDto>();
        for (int i = 0; i < templateItems.Count; i++)
        {
            list.Add(new EquipmentChecklist.DTOs.SubmissionItemDto
            {
                TemplateItemId = templateItems[i].Id,
                Status         = statuses[i],
                Notes          = statuses[i] == ItemStatus.Defect ? "test defect" : null
            });
        }
        return list;
    }
}
