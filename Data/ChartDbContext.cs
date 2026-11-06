using ChartAnalyzer.Analysis;
using Microsoft.EntityFrameworkCore;

namespace ChartAnalyzer.Data;

public sealed class ChartDbContext(DbContextOptions<ChartDbContext> options) : DbContext(options)
{
    public DbSet<WatchedZone> Zones => Set<WatchedZone>();
    public DbSet<ExchangeCandle> ExchangeCandles => Set<ExchangeCandle>();
    public DbSet<SentAlert> Alerts => Set<SentAlert>();
    public DbSet<ZoneAbsorption> ZoneAbsorptions => Set<ZoneAbsorption>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<decimal>().HavePrecision(18, 8);
        configurationBuilder.Properties<ZoneRole>().HaveConversion<string>().HaveMaxLength(12);
        configurationBuilder.Properties<ZoneProfile>().HaveConversion<string>().HaveMaxLength(12);
        configurationBuilder.Properties<AlertKind>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<AbsorptionSide>().HaveConversion<string>().HaveMaxLength(8);
        configurationBuilder.Properties<AbsorptionOutcome>().HaveConversion<string>().HaveMaxLength(8);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WatchedZone>(e =>
        {
            e.HasIndex(z => new { z.Low, z.High }).IsUnique();
            e.Property(z => z.AlertedKeys).HasMaxLength(8000);
        });

        modelBuilder.Entity<ExchangeCandle>(e =>
        {
            e.HasKey(c => new { c.Exchange, c.OpenTime });
            e.Property(c => c.Exchange).HasMaxLength(16);
        });

        modelBuilder.Entity<SentAlert>(e =>
        {
            e.ToTable("Alerts");
            e.HasIndex(a => a.SentAt);
            e.Property(a => a.Exchanges).HasMaxLength(100);
            e.Property(a => a.Zones).HasMaxLength(500);
        });

        modelBuilder.Entity<ZoneAbsorption>(e =>
        {
            e.HasIndex(a => a.Minute);
            e.Property(a => a.Id).ValueGeneratedNever();
            e.Property(a => a.Exchanges).HasMaxLength(100);
        });
    }
}
