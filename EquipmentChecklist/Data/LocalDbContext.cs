using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Data;

/// <summary>
/// Local SQLite context for offline field use on tablets/phones.
/// Submissions are synced back to PostgreSQL when connectivity is restored.
/// </summary>
public class LocalDbContext : DbContext
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
            e.HasIndex(s => s.LocalId).IsUnique());
    }
}

/// <summary>
/// Tracks offline submissions that need to be synced to the cloud.
/// </summary>
public class PendingSyncRecord
{
    public int Id { get; set; }
    public Guid LocalSubmissionId { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    public int RetryCount { get; set; } = 0;
    public string? LastError { get; set; }
}
