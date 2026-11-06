using ChartAnalyzer.Analysis;
using ChartAnalyzer.Binance;
using ChartAnalyzer.Coinbase;
using ChartAnalyzer.Data;
using ChartAnalyzer.Models;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Zones;

/// <summary>
/// Analyzes watched zones on demand, on Binance spot plus the Binance perp and Coinbase for the per-market breakdown.
/// Keeps a candle cache per market and candle size (1-minute for local zones, 1-hour for macro zones),
/// so each run only fetches what's new.
/// </summary>
public sealed class ZoneMonitor(
    BinanceClient binance,
    BinancePerpClient perp,
    CoinbaseHistory coinbase,
    ZoneReport reports,
    IOptions<AnalyzerOptions> options,
    ILogger<ZoneMonitor> logger)
{
    private readonly AnalyzerOptions _options = options.Value;
    private readonly CandleCache _minutes = new(binance, options.Value.Symbol, ZoneSettings.Local);
    private readonly CandleCache _hours = new(binance, options.Value.Symbol, ZoneSettings.Macro);
    private readonly CandleCache _perpMinutes = new(perp, options.Value.Symbol, ZoneSettings.Local);
    private readonly CandleCache _perpHours = new(perp, options.Value.Symbol, ZoneSettings.Macro);

    public async Task<ZoneAnalysis> AnalyzeAsync(WatchedZone zone, CancellationToken ct)
    {
        var settings = ZoneSettings.For(zone.Profile);
        var now = DateTimeOffset.UtcNow;

        // Macro zones are judged on complete days only (up to the last daily close); local zones up to now.
        var to = zone.Profile == ZoneProfile.Macro ? new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero) : now;
        var from = zone.Since < to.AddDays(-settings.MaxDays) ? to.AddDays(-settings.MaxDays) : zone.Since;

        var macro = zone.Profile == ZoneProfile.Macro;
        var needFrom = from - settings.History;
        var candles = await (macro ? _hours : _minutes).GetAsync(needFrom, ct);

        // The other markets are extras: if one can't be fetched, the analysis goes ahead without it.
        var others = new List<MarketCandles>();
        try
        {
            others.Add(new MarketCandles(perp.Market, IsPerp: true, await (macro ? _perpHours : _perpMinutes).GetAsync(needFrom, ct),
                _options.PerpMinAbsorbedVolume));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning("Binance perp candles failed, zone analysis continues without them: {Message}", ex.Message);
        }
        others.Add(new MarketCandles(CoinbaseCandles.Exchange, IsPerp: false, await coinbase.GetAsync(needFrom, settings.CandleInterval, ct),
            _options.CoinbaseMinAbsorbedVolume));

        logger.LogDebug("Zone {Low:N0}-{High:N0}: Binance {Count} candles, {Others}", zone.Low, zone.High, candles.Count,
            string.Join(", ", others.Select(o => $"{o.Name} {o.Candles.Count}" + (o.Candles.Count > 0 ? $" from {o.Candles[0].OpenTime:MM-dd HH:mm}" : ""))));
        var analysis = reports.Analyze(candles, zone.ToPriceZone(), from, to, others);

        // Show the current price, even when the analysis stops at the last daily close.
        return analysis with { LastPrice = candles.LastOrDefault()?.Close ?? analysis.LastPrice };
    }

    public string FormatSummary(ZoneAnalysis analysis) => reports.FormatSummary(analysis);

    private sealed class CandleCache(BinanceClient binance, string symbol, ZoneSettings settings)
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private List<Candle> _candles = [];

        // Returns a snapshot of the cache from 'needFrom', fetching from Binance only what's missing.
        public async Task<IReadOnlyList<Candle>> GetAsync(DateTimeOffset needFrom, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                var interval = settings.CandleInterval;
                if (_candles.Count == 0 || _candles[0].OpenTime > needFrom + interval)
                    _candles = (await binance.GetClosedCandlesAsync(symbol, needFrom, now, ct, interval)).ToList();
                else
                    _candles.AddRange(await binance.GetClosedCandlesAsync(symbol, _candles[^1].OpenTime + interval, now, ct, interval));

                // Keep at most what the longest analysis needs.
                var oldest = now.AddDays(-settings.MaxDays - 1) - settings.History;
                _candles.RemoveAll(c => c.OpenTime < oldest);
                return _candles.Where(c => c.OpenTime >= needFrom).ToList();
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
