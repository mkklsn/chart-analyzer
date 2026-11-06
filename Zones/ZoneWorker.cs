using ChartAnalyzer.Analysis;
using ChartAnalyzer.Data;
using ChartAnalyzer.Telegram;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Zones;

/// <summary>
/// Checks watched zones and sends alerts and summaries:
/// - Local zones: checked every hour; new climaxes and warnings are alerted right away; summary daily.
/// - Macro zones: checked after each daily close (00:00 UTC); summary weekly on Mondays.
/// Summaries go out at ZoneSummaryHour, local time.
/// </summary>
public sealed class ZoneWorker(
    ZoneStore zones,
    ZoneMonitor monitor,
    TelegramNotifier notifier,
    DbWriteQueue dbQueue,
    AlertLog alertLog,
    IOptions<AnalyzerOptions> options,
    ILogger<ZoneWorker> logger) : BackgroundService
{
    private readonly AnalyzerOptions _options = options.Value;
    private readonly TimeZoneInfo _timeZone = options.Value.GetTimeZone();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // A first check right after startup (alerts only), so a restart doesn't delay alerts.
        while (!zones.IsLoaded)
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        await CheckZonesAsync(startup: true, ct);

        while (!ct.IsCancellationRequested)
        {
            await WaitForNextHourAsync(ct);
            await CheckZonesAsync(startup: false, ct);
        }
    }

    private async Task CheckZonesAsync(bool startup, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var local = TimeZoneInfo.ConvertTime(now, _timeZone);
        var summaryHour = !startup && local.Hour == _options.ZoneSummaryHour;
        var afterDailyClose = now.Hour == 0;

        foreach (var zone in zones.GetAll())
        {
            var macro = zone.Profile == ZoneProfile.Macro;
            var sendSummary = summaryHour && (!macro || local.DayOfWeek == DayOfWeek.Monday);

            // Macro zones only change at the daily close, so they're analyzed then (and for their summary).
            if (macro && !startup && !afterDailyClose && !sendSummary)
                continue;

            try
            {
                var analysis = await monitor.AnalyzeAsync(zone, ct);
                await SendNewAlertsAsync(zone, analysis, ct);
                if (sendSummary)
                    await notifier.SendAsync(monitor.FormatSummary(analysis), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                logger.LogWarning("Zone {Low}-{High} check failed, will retry next hour: {Message}", zone.Low, zone.High, ex.Message);
            }
        }
    }

    private async Task SendNewAlertsAsync(WatchedZone zone, ZoneAnalysis analysis, CancellationToken ct)
    {
        var fresh = analysis.Signs.Where(s => s.Alert && zone.MarkAlerted(s.Key)).ToList();
        if (fresh.Count == 0)
            return;

        var kind = $"{(zone.Profile == ZoneProfile.Macro ? "macro " : "")}{(zone.Role == ZoneRole.Support ? "support" : "resistance")}";
        var text = $"🔔 {kind} zone {zone.Low:N0}–{zone.High:N0}\n" + string.Join("\n", fresh.Select(s => $"{s.Symbol} {s.Text}"));
        logger.LogWarning("Zone alert: {Text}", text.Replace('\n', ' '));
        var delivered = await notifier.SendAsync(text, ct);
        PersistAlertedKeys(dbQueue, zone);
        alertLog.Record(new SentAlert
        {
            Kind = AlertKind.Zone,
            Zones = $"{kind} {zone.Low:N0}–{zone.High:N0}",
            Text = text,
            Details = AlertLog.Serialize(fresh.Select(s => new { s.Kind, s.Key, s.Text })),
            SentAt = DateTimeOffset.UtcNow,
            Delivered = delivered,
        });
    }

    /// <summary>Saves which alerts have been sent for the zone, so a restart doesn't repeat them.</summary>
    public static void PersistAlertedKeys(DbWriteQueue dbQueue, WatchedZone zone)
    {
        var (low, high, keys) = (zone.Low, zone.High, zone.AlertedKeys);
        dbQueue.Enqueue($"save alerted keys for zone {low}-{high}", async (db, ct) =>
        {
            var stored = await db.Zones.FirstOrDefaultAsync(z => z.Low == low && z.High == high, ct);
            if (stored is null)
                return;
            stored.AlertedKeys = keys;
            await db.SaveChangesAsync(ct);
        });
    }

    // Runs 90 seconds after each hour, once the hour's last candle has closed on Binance.
    private static Task WaitForNextHourAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var hourStart = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        return Task.Delay(hourStart.AddHours(1).AddSeconds(90) - now, ct);
    }
}
