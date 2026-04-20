using EquipmentChecklist.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Data;

// 1. Inherit from IdentityDbContext to prevent primary key errors with ApplicationUser
public class LocalDbContext : IdentityDbContext<ApplicationUser>
{
    public LocalDbContext(DbContextOptions<LocalDbContext> options) : base(options) { }

    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<ChecklistTemplate> ChecklistTemplates => Set<ChecklistTemplate>();
    public DbSet<ChecklistTemplateItem> ChecklistTemplateItems => Set<ChecklistTemplateItem>();
    public DbSet<ChecklistSubmission> ChecklistSubmissions => Set<ChecklistSubmission>();
    public DbSet<SubmissionItem> SubmissionItems => Set<SubmissionItem>();

    // Offline pending sync queue
    public DbSet<PendingSyncRecord> PendingSyncRecords => Set<PendingSyncRecord>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ChecklistSubmission>(e =>
        {
            e.HasIndex(s => s.LocalId).IsUnique();

            // 1. Map Operator
            e.HasOne(s => s.Operator)
             .WithMany(u => u.Submissions)
             .HasForeignKey(s => s.OperatorId)
             .OnDelete(DeleteBehavior.Restrict);

            // 2. Map Supervisor
            e.HasOne(s => s.Supervisor)
             .WithMany() // Empty WithMany() tells EF it doesn't link to a collection on ApplicationUser
             .HasForeignKey(s => s.SupervisorId)
             .OnDelete(DeleteBehavior.SetNull);

            // 3. Map Mechanic
            e.HasOne(s => s.Mechanic)
             .WithMany()
             .HasForeignKey(s => s.MechanicId)
             .OnDelete(DeleteBehavior.SetNull);

            // 4. Map Rejected Mechanic
            e.HasOne(s => s.RejectedMechanic)
             .WithMany()
             .HasForeignKey(s => s.RejectedMechanicId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // Also map the Mechanic on MachineAssignment, as EF will discover this too
        builder.Entity<MachineAssignment>(e =>
        {
            e.HasOne(a => a.Operator)
             .WithMany(u => u.Assignments)
             .HasForeignKey(a => a.OperatorId)
             .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(a => a.Mechanic)
             .WithMany()
             .HasForeignKey(a => a.MechanicId)
             .OnDelete(DeleteBehavior.SetNull);
        });
    }
}