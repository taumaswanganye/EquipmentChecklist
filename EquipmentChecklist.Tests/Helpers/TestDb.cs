using EquipmentChecklist.Data;
using EquipmentChecklist.Models;
using Microsoft.EntityFrameworkCore;

namespace EquipmentChecklist.Tests.Helpers;

/// <summary>
/// Spins up a fresh in-memory <see cref="ApplicationDbContext"/> per test.
///
/// Each call to <see cref="Create()"/> uses a unique GUID-named database so
/// tests run in parallel without seeing each other's writes. Disposing the
/// context drops the DB at the same time.
///
/// We disable the "package InMemory provider sees identity column" warning
/// — IDs auto-increment fine for the purposes of these tests.
/// </summary>
public static class TestDb
{
    public static ApplicationDbContext Create()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"checklist-tests-{Guid.NewGuid():N}")
            // ApplicationDbContext.OnConfiguring may try to use Npgsql; the
            // builder above wins because UseInMemoryDatabase is explicit, but
            // the analyzer still warns. Silencing it keeps test output clean.
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new ApplicationDbContext(options);
    }
}
