using EquipmentChecklist.Data;
using EquipmentChecklist.DTOs;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Services;

/// <summary>
/// Background service that periodically syncs offline (SQLite) submissions
/// to the cloud PostgreSQL database.
/// </summary>
public class SyncService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SyncService> _logger;
    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(2);

    public SyncService(IServiceScopeFactory scopeFactory, ILogger<SyncService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SyncPendingAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sync cycle failed");
            }
            await Task.Delay(SyncInterval, stoppingToken);
        }
    }

    private async Task SyncPendingAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var localDb = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
        var cloudDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var pending = await localDb.PendingSyncRecords
            .Where(p => p.RetryCount < 5)
            .ToListAsync();

        foreach (var record in pending)
        {
            try
            {
                var localSub = await localDb.ChecklistSubmissions
                    .Include(s => s.Items)
                    .FirstOrDefaultAsync(s => s.LocalId == record.LocalSubmissionId);

                if (localSub == null)
                {
                    localDb.PendingSyncRecords.Remove(record);
                    continue;
                }

                // Upsert into PostgreSQL using LocalId as idempotency key
                var exists = await cloudDb.ChecklistSubmissions
                    .AnyAsync(s => s.LocalId == localSub.LocalId);

                if (!exists)
                {
                    var cloudSub = new ChecklistSubmission
                    {
                        LocalId = localSub.LocalId,
                        MachineId = localSub.MachineId,
                        OperatorId = localSub.OperatorId,
                        Shift = localSub.Shift,
                        SubmittedAt = localSub.SubmittedAt,
                        KmOrHourMeter = localSub.KmOrHourMeter,
                        OperatorRemarks = localSub.OperatorRemarks,
                        FitnessDeclarationSigned = localSub.FitnessDeclarationSigned,
                        Status = localSub.Status,
                        IsSyncedToCloud = true,
                        Items = localSub.Items.Select(i => new SubmissionItem
                        {
                            TemplateItemId = i.TemplateItemId,
                            Status = i.Status,
                            Notes = i.Notes
                        }).ToList()
                    };
                    cloudDb.ChecklistSubmissions.Add(cloudSub);
                    await cloudDb.SaveChangesAsync();
                }

                localDb.PendingSyncRecords.Remove(record);
                await localDb.SaveChangesAsync();
                _logger.LogInformation("Synced submission {LocalId}", record.LocalSubmissionId);
            }
            catch (Exception ex)
            {
                record.RetryCount++;
                record.LastError = ex.Message;
                await localDb.SaveChangesAsync();
                _logger.LogWarning("Sync failed for {LocalId}: {Error}", record.LocalSubmissionId, ex.Message);
            }
        }
    }
}
