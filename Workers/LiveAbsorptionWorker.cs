using ChartAnalyzer.Analysis;
using ChartAnalyzer.Binance;
using ChartAnalyzer.Coinbase;
using ChartAnalyzer.Data;
using ChartAnalyzer.Models;
using ChartAnalyzer.Telegram;
using ChartAnalyzer.Zones;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Workers;

/// <summary>
/// After every minute, checks Binance spot, Coinbase and the Binance perp for absorption, and alerts when it
/// happens at a watched zone. The alert says where it shows: spot + perps, spot only, or perps only (often
/// liquidations rather than real buying or selling). If another market shows the same absorption a little later,
/// a short follow-up says it confirmed. Coinbase alone never starts an alert, but it can confirm one.
/// Alerts also show how the Coinbase price compares with Binance, against its usual gap.
/// </summary>
public sealed class LiveAbsorptionWorker(
    BinanceClient binance,
    BinancePerpClient perp,
    CoinbaseCandles coinbase,
    AbsorptionDetector detector,
    ZoneStore zones,
    TelegramNotifier notifier,
    AlertLog alertLog,
    IOptions<AnalyzerOptions> options,
    ILogger<LiveAbsorptionWorker> logger) : BackgroundService
{
    private const string Binance = "Binance";
    private const string Perp = "Binance perp";
    private static readonly string[] Exchanges = [Binance, CoinbaseCandles.Exchange, Perp];
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    // How far (in USD) the Coinbase gap must move from its usual level to be called out.
    private const decimal CoinbaseGapNotable = 30m;

    private readonly AnalyzerOptions _options = options.Value;
    private readonly TimeZoneInfo _timeZone = options.Value.GetTimeZone();

    // Last minute each exchange showed absorption on each side, alerted or not.
    private readonly Dictionary<(string Exchange, AbsorptionSide Side), DateTimeOffset> _lastSeen = [];

    // Last alert per side; repeats within the cooldown are suppressed, except to report a confirming exchange.
    private readonly Dictionary<AbsorptionSide, (DateTimeOffset Minute, HashSet<string> Exchanges)> _lastAlert = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!zones.IsLoaded)
            await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            await WaitForNextMinuteAsync(ct);
            try
            {
                await CheckLastMinuteAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Live absorption check failed");
            }
        }
    }

    private async Task CheckLastMinuteAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var minute = Floor(now) - Minute;   // the minute that just closed
        var needed = _options.LookbackMinutes + 2 * _options.MaxWindowMinutes + 1;

        var checks = new Dictionary<string, AbsorptionCheck?>();
        IReadOnlyList<Candle>? binanceCandles = null;
        try
        {
            binanceCandles = await binance.GetClosedCandlesAsync(_options.Symbol, minute - needed * Minute, minute + Minute, ct);
            checks[Binance] = CheckMinute(binanceCandles, minute, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning("Binance candles failed: {Message}", ex.Message);
        }
        try
        {
            var candles = await perp.GetClosedCandlesAsync(_options.Symbol, minute - needed * Minute, minute + Minute, ct);
            checks[Perp] = CheckMinute(candles, minute, _options.PerpMinAbsorbedVolume);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning("Binance perp candles failed: {Message}", ex.Message);
        }
        try
        {
            await coinbase.UpdateAsync(now, needed, ct);
            checks[CoinbaseCandles.Exchange] = CheckMinute(coinbase.Candles, minute, _options.CoinbaseMinAbsorbedVolume);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogWarning("Coinbase trades failed: {Message}", ex.Message);
        }

        foreach (var (exchange, check) in checks)
        {
            if (check?.Side is { } side)
                _lastSeen[(exchange, side)] = minute;
        }

        var watched = zones.GetAll();
        var gap = CoinbaseGap(binanceCandles, coinbase.Candles, minute);
        foreach (var side in Enum.GetValues<AbsorptionSide>())
        {
            var atZone = checks
                .Where(kv => kv.Value?.Side == side && TouchedZones(kv.Value, watched).Any())
                .Select(kv => kv.Key)
                .ToList();
            if (atZone.Count == 0)
                continue;

            // Absorption on the same side within the multi-minute window counts as the same event.
            var showing = Exchanges
                .Where(e => _lastSeen.TryGetValue((e, side), out var seen) && seen > minute - _options.MaxWindowMinutes * Minute)
                .ToHashSet();

            if (_lastAlert.TryGetValue(side, out var last) && minute < last.Minute + _options.CooldownMinutes * Minute)
            {
                var confirming = atZone.Where(e => !last.Exchanges.Contains(e)).ToList();
                if (confirming.Count == 0)
                    continue;
                last.Exchanges.UnionWith(confirming);
                var confirmation = FormatConfirmation(side, last.Minute, confirming, last.Exchanges, checks);
                alertLog.Record(new SentAlert
                {
                    Kind = AlertKind.Confirmation,
                    Side = side,
                    Minute = minute,
                    ConfirmsMinute = last.Minute,
                    Price = checks[confirming[0]]!.Candle.Close,
                    Exchanges = string.Join(", ", confirming),
                    SpotAndPerps = IsSpotAndPerps(last.Exchanges),
                    Zones = string.Join("; ", DescribeZones(checks, side, watched)),
                    Text = confirmation,
                    Details = AlertLog.DescribeChecks(checks),
                    CoinbaseGap = gap?.Gap,
                    CoinbaseGapUsual = gap?.Usual,
                    SentAt = DateTimeOffset.UtcNow,
                    Delivered = await SendAsync(confirmation, ct),
                });
                continue;
            }

            // Coinbase alone doesn't start an alert: its usual volume is small, so a few BTC looks like a spike.
            // It still counts when Binance or the perp shows the same absorption, and can confirm an alert.
            if (atZone.All(e => e == CoinbaseCandles.Exchange))
            {
                logger.LogInformation("Coinbase-only {Side} absorption at {Minute:HH:mm}, not alerted", side, minute);
                continue;
            }

            _lastAlert[side] = (minute, showing);
            var alert = FormatAlert(side, showing, checks, watched) + FormatGap(gap);
            alertLog.Record(new SentAlert
            {
                Kind = AlertKind.Absorption,
                Side = side,
                Minute = minute,
                Price = checks[atZone[0]]!.Candle.Close,
                Exchanges = string.Join(", ", Exchanges.Where(showing.Contains)),
                SpotAndPerps = IsSpotAndPerps(showing),
                Zones = string.Join("; ", DescribeZones(checks, side, watched)),
                Text = alert,
                Details = AlertLog.DescribeChecks(checks),
                CoinbaseGap = gap?.Gap,
                CoinbaseGapUsual = gap?.Usual,
                SentAt = DateTimeOffset.UtcNow,
                Delivered = await SendAsync(alert, ct),
            });
        }
    }

    // Coinbase (USD) close minus Binance (USDT) close for the minute, and its median over the past hour.
    // It's normally a little negative, so it's compared with its own usual level rather than with zero.
    private (decimal Gap, decimal Usual)? CoinbaseGap(IReadOnlyList<Candle>? binanceCandles, IReadOnlyList<Candle> coinbaseCandles, DateTimeOffset minute)
    {
        if (binanceCandles is null)
            return null;
        var coinbaseClose = coinbaseCandles.DistinctBy(c => c.OpenTime).ToDictionary(c => c.OpenTime, c => c.Close);
        var gaps = binanceCandles
            .Where(c => c.OpenTime > minute - _options.LookbackMinutes * Minute && c.OpenTime <= minute && coinbaseClose.ContainsKey(c.OpenTime))
            .Select(c => (c.OpenTime, Gap: coinbaseClose[c.OpenTime] - c.Close))
            .ToList();
        if (gaps.Count == 0 || gaps[^1].OpenTime != minute)
            return null;
        var sorted = gaps.Select(g => g.Gap).Order().ToList();
        return (gaps[^1].Gap, sorted[sorted.Count / 2]);
    }

    private static string FormatGap((decimal Gap, decimal Usual)? gap)
    {
        if (gap is not { } g)
            return "";
        var diff = g.Gap - g.Usual;
        var verdict = diff >= CoinbaseGapNotable ? "US buyers stronger than usual"
            : diff <= -CoinbaseGapNotable ? "US spot weaker than usual"
            : "in line with usual";
        return $"\nCoinbase vs Binance: {g.Gap:+#,0;-#,0;0} (usual {g.Usual:+#,0;-#,0;0} past hour) · {verdict}";
    }

    private static bool IsSpotAndPerps(HashSet<string> showing) => showing.Contains(Perp) && showing.Any(e => e != Perp);

    // The check for the window ending at 'minute', or null if that candle isn't available yet.
    private AbsorptionCheck? CheckMinute(IReadOnlyList<Candle> candles, DateTimeOffset minute, decimal? minAbsorbedVolume) =>
        candles.Count > 0 && candles[^1].OpenTime == minute ? detector.Check(candles, candles.Count - 1, minAbsorbedVolume) : null;

    private static IEnumerable<WatchedZone> TouchedZones(AbsorptionCheck check, IReadOnlyList<WatchedZone> watched) =>
        watched.Where(z => check.Low <= z.High && check.High >= z.Low);

    private string FormatAlert(AbsorptionSide side, HashSet<string> showing, Dictionary<string, AbsorptionCheck?> checks, IReadOnlyList<WatchedZone> watched)
    {
        var label = checks.Values.First(c => c?.Side == side)!.Label;
        var touched = DescribeZones(checks, side, watched).Select(z => $"{z} zone");

        return $"🧲 {label} · {Where(showing)}\nAt {string.Join(", ", touched)}\n" +
               string.Join("\n", Exchanges.Select(e => $"{e}: {DescribeCheck(checks.GetValueOrDefault(e), side)}"));
    }

    // "support 77,000–77,400", "macro resistance 84,000–85,000": the zones touched by this side's absorption.
    private static IEnumerable<string> DescribeZones(Dictionary<string, AbsorptionCheck?> checks, AbsorptionSide side, IReadOnlyList<WatchedZone> watched) =>
        checks.Values
            .Where(c => c?.Side == side)
            .SelectMany(c => TouchedZones(c!, watched))
            .DistinctBy(z => z.Id)
            .Select(z => $"{(z.Profile == ZoneProfile.Macro ? "macro " : "")}{(z.Role == ZoneRole.Resistance ? "resistance" : "support")} {z.Low:N0}–{z.High:N0}");

    private string FormatConfirmation(AbsorptionSide side, DateTimeOffset alertMinute, List<string> confirming,
        HashSet<string> allShowing, Dictionary<string, AbsorptionCheck?> checks)
    {
        var label = side == AbsorptionSide.Seller ? "seller absorption" : "buyer absorption";
        var at = TimeZoneInfo.ConvertTime(alertMinute, _timeZone);
        return $"✓ {string.Join(" + ", confirming)} confirms the {label} from {at:HH:mm} (now {Where(allShowing)})\n" +
               string.Join("\n", confirming.Select(e => $"{e}: {DescribeCheck(checks[e], side)}"));
    }

    // "spot + perps (Binance, Coinbase, Binance perp)", "spot only (Coinbase)" or "perps only ⚠ ...".
    private static string Where(HashSet<string> showing)
    {
        var spot = showing.Any(e => e != Perp);
        var perps = showing.Contains(Perp);
        var kind = spot && perps ? "spot + perps" : spot ? "spot only" : "perps only ⚠ often liquidations, spot not confirming";
        return $"{kind} ({string.Join(", ", Exchanges.Where(showing.Contains))})";
    }

    private string DescribeCheck(AbsorptionCheck? check, AbsorptionSide side) => check switch
    {
        null => "no data for this minute",
        { Side: var s } when s == side => check.Describe(_timeZone),
        _ => $"no absorption (sell {check.SellRatio:0.0}x, buy {check.BuyRatio:0.0}x usual, range {check.RangeRatio:0.00}x usual)",
    };

    private Task<bool> SendAsync(string text, CancellationToken ct)
    {
        logger.LogWarning("Absorption alert: {Text}", text.Replace('\n', ' '));
        return notifier.SendAsync(text, ct);
    }

    // A few seconds after each minute, once the minute's candle has closed on Binance.
    private static Task WaitForNextMinuteAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.Delay(Floor(now) + Minute + TimeSpan.FromSeconds(3) - now, ct);
    }

    private static DateTimeOffset Floor(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % Minute.Ticks, TimeSpan.Zero);
}
