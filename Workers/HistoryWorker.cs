using ChartAnalyzer.Analysis;
using ChartAnalyzer.Binance;
using ChartAnalyzer.Models;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Workers;

/// <summary>
/// Scans a past time range, prints the result, then exits:
///   dotnet run -- --from 2026-10-01 --to 2026-10-08                          absorption signals and how they played out
///   dotnet run -- --from 2026-10-08 --to 2026-10-29 --support 80700-81600    zone report (or --resistance)
///   add --profile macro for a high-timeframe zone (daily candles), --summary true for the Telegram version
///   dotnet run -- --from 2026-09-01 --to 2026-10-15 --levels true             candidate levels (volume profile + absorption clusters)
/// </summary>
public sealed class HistoryWorker(
    BinanceClient spot,
    BinancePerpClient perp,
    AbsorptionDetector detector,
    ZoneReport zoneReports,
    LevelScanner levelScanner,
    IOptions<AnalyzerOptions> options,
    HistoryRange range,
    IHostApplicationLifetime lifetime,
    ILogger<HistoryWorker> logger) : BackgroundService
{
    private readonly AnalyzerOptions _options = options.Value;
    private readonly TimeZoneInfo _timeZone = options.Value.GetTimeZone();

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            if (range.Levels)
            {
                await ScanLevelsAsync(ct);
                return;
            }

            if (range.Perp && range.Zone is { Role: not ZoneRole.Any })
            {
                logger.LogError("--market perp only works for signal scans (with or without --zone), not zone reports.");
                return;
            }
            var binance = range.Perp ? perp : spot;
            decimal? minAbsorbed = range.Perp ? _options.PerpMinAbsorbedVolume : null;

            // Also load the time just before 'from', so the first candles in the range have a "usual" to compare with.
            // Zone reports read the candle size of their profile (1 minute local, 1 hour macro).
            var zoneReport = range.Zone is { Role: not ZoneRole.Any };
            var settings = ZoneSettings.For(range.Zone?.Profile ?? ZoneProfile.Local);
            var loadFrom = zoneReport
                ? range.From - settings.History
                : range.From.AddMinutes(-(_options.LookbackMinutes + 2 * _options.MaxWindowMinutes));
            logger.LogInformation("Loading {Market} {Symbol} candles from {From:yyyy-MM-dd HH:mm} to {To:yyyy-MM-dd HH:mm}...",
                binance.Market, _options.Symbol, range.From.ToLocalTime(), range.To.ToLocalTime());

            // ...and the time just after 'to', to see what happened next: 30 minutes for signals, 7 days for a zone report.
            var loadTo = zoneReport ? range.To.AddDays(7) : range.To.AddMinutes(SignalOutcome.WindowMinutes);
            if (loadTo > DateTimeOffset.UtcNow)
                loadTo = DateTimeOffset.UtcNow;
            var candles = await binance.GetClosedCandlesAsync(_options.Symbol, loadFrom, loadTo, ct,
                zoneReport ? settings.CandleInterval : TimeSpan.FromMinutes(1));

            if (zoneReport)
            {
                // The perp for the per-market breakdown. Coinbase isn't included here: it's only stored by the live app.
                var perpCandles = await perp.GetClosedCandlesAsync(_options.Symbol, loadFrom, loadTo, ct, settings.CandleInterval);
                var analysis = zoneReports.Analyze(candles, range.Zone!, range.From, range.To,
                    [new MarketCandles(perp.Market, IsPerp: true, perpCandles, _options.PerpMinAbsorbedVolume)]);
                var text = range.Summary ? zoneReports.FormatSummary(analysis) : zoneReports.FormatFull(analysis, candles);
                foreach (var line in text.Split('\n'))
                {
                    if (line.Trim().Length > 0)
                        logger.LogInformation("{Line}", line.TrimEnd('\r'));
                }
                return;
            }

            if (range.Zone is { } z)
                logger.LogInformation("Only reporting signals that touched the zone {Low:N0}-{High:N0}.", z.Low, z.High);

            bool InRange(Candle c) => c.OpenTime >= range.From && c.OpenTime < range.To;
            bool Touches(decimal low, decimal high) => range.Zone is not { } zone || (low <= zone.High && high >= zone.Low);

            var cooldown = new SignalCooldown(_options.CooldownMinutes);
            var results = new List<SignalResult>();
            var checkedCount = 0;
            for (var i = 0; i < candles.Count; i++)
            {
                if (!InRange(candles[i]))
                    continue;

                var check = detector.Check(candles, i, minAbsorbed);
                if (check is null)
                    continue;

                checkedCount++;
                if (cooldown.IsNewSignal(check) && Touches(check.Low, check.High))
                {
                    var outcome = SignalOutcome.Compute(candles, i);
                    results.Add(new SignalResult(check.Side!.Value, check.Candle.Close, null, outcome));
                    logger.LogWarning("{Label} {Details} → {Outcome}", check.Label, check.Describe(_timeZone), DescribeOutcome(check.Candle.Close, outcome));
                }
            }

            logger.LogInformation("Done. Checked {Checked} minutes, found {Signals} possible absorption signals.", checkedCount, results.Count);
            if (results.Count > 0)
            {
                // With a zone, compare against random minutes in that zone, not anywhere on the chart.
                var baseline = Baseline.Compute(candles, c => InRange(c) && Touches(c.Low, c.High));
                logger.LogInformation("How the signals played out:");
                foreach (var line in SignalStats.Format(results, baseline).Split('\n'))
                    logger.LogInformation("  {Line}", line.TrimEnd('\r'));
            }
        }
        catch (HttpRequestException ex)
        {
            logger.LogError("Binance request failed: {Message}", ex.Message);
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    // Levels from the range's 1-minute candles, plus up to 7 days after it to see what price did at each level.
    private async Task ScanLevelsAsync(CancellationToken ct)
    {
        var loadFrom = range.From.AddMinutes(-(_options.LookbackMinutes + 2 * _options.MaxWindowMinutes));
        var loadTo = range.To.AddDays(7) < DateTimeOffset.UtcNow ? range.To.AddDays(7) : DateTimeOffset.UtcNow;
        logger.LogInformation("Loading Binance {Symbol} 1-minute candles from {From:yyyy-MM-dd HH:mm} to {To:yyyy-MM-dd HH:mm} (about {Requests} requests)...",
            _options.Symbol, range.From.ToLocalTime(), loadTo.ToLocalTime(), (int)((loadTo - loadFrom).TotalMinutes / 1000) + 1);
        var candles = await spot.GetClosedCandlesAsync(_options.Symbol, loadFrom, loadTo, ct);

        var scan = levelScanner.Scan(candles, range.From, range.To, range.BinSize);
        foreach (var line in levelScanner.Format(scan, candles).Split('\n'))
            logger.LogInformation("{Line}", line.TrimEnd('\r'));
    }

    // Price change after 5/15/30 minutes, e.g. "5m +12 | 15m +42 | 30m -10".
    private static string DescribeOutcome(decimal close, SignalOutcome outcome) =>
        string.Join(" | ", SignalOutcome.Horizons.Select(m =>
            outcome.CloseAfter(m) is { } later ? $"{m}m {later - close:+0;-0;0}" : $"{m}m -"));
}

/// <summary>The time range to scan, from command-line arguments. 'To' is exclusive.</summary>
/// <param name="Zone">
/// Optional price zone. A support or resistance zone produces a zone report;
/// a zone of either role (--zone) lists the minute signals that touched it.
/// </param>
/// <param name="Summary">Print the short Telegram version of a zone report (--summary true).</param>
/// <param name="Perp">Scan the Binance perpetual instead of spot (--market perp).</param>
/// <param name="Levels">Find candidate support/resistance levels (--levels true).</param>
/// <param name="BinSize">Price bin size for the level scan's volume profile (--bin); null picks about 0.1% of the price.</param>
public sealed record HistoryRange(DateTimeOffset From, DateTimeOffset To, PriceZone? Zone, bool Summary = false, bool Perp = false,
    bool Levels = false, decimal? BinSize = null);
