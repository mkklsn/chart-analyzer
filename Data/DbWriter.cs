using ChartAnalyzer.Zones;
using Microsoft.EntityFrameworkCore;

namespace ChartAnalyzer.Data;

/// <summary>
/// The only service that waits for the database. Prepares it at startup,
/// then runs queued work one item at a time.
/// </summary>
public sealed class DbWriter(
    IDbContextFactory<ChartDbContext> dbFactory,
    DbWriteQueue queue,
    ZoneStore zones,
    ILogger<DbWriter> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await PrepareDatabaseAsync(ct);

        await foreach (var work in queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                // Transient connection errors are retried by EF Core's retry strategy.
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await work.Run(db, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Database work failed and was dropped: {Description}", work.Description);
            }
        }
    }

    // Creates/updates the tables and loads your zones. Keeps trying until the database is reachable.
    private async Task PrepareDatabaseAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                await db.Database.MigrateAsync(ct);
                zones.Load(await db.Zones.AsNoTracking().ToListAsync(ct));

                logger.LogInformation("Database ready. Zones: {Zones}",
                    string.Join(", ", zones.GetAll().Select(z => $"{z.Profile} {z.Role} {z.Low:N0}-{z.High:N0}")));
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Database not ready, retrying in {Delay}s: {Message}", RetryDelay.TotalSeconds, ex.Message);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }
}
