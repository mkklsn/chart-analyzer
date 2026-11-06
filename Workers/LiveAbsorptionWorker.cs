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
///
/// Each alert is recorded per zone it touched, and followed for AbsorptionHoldMinutes: it holds unless a Binance spot
/// close goes past its level. Alerts end with the zone's scoreboard, and a failed absorption gets its own message.
/// </summary>
public sealed class LiveAbsorptionWorker(
    BinanceClient binance,
    BinancePerpClient perp,
    CoinbaseCandles coinbase,
    AbsorptionDetector detector,
    ZoneStore zones,
    ZoneMonitor zoneMonitor,
    AbsorptionTally tally,
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
    private readonly TimeSpan _hold = TimeSpan.FromMinutes(options.Value.AbsorptionHoldMinutes);

    // Last minute each exchange showed absorption on each side, alerted or not.
    private readonly Dictionary<(string Exchange, AbsorptionSide Side), DateTimeOffset> _lastSeen = [];

    // Last alert per side and its zone events; repeats within the cooldown are suppressed, except to report a confirming exchange.
    private readonly Dictionary<AbsorptionSide, (DateTimeOffset Minute, HashSet<string> Exchanges, List<Guid> Events)> _lastAlert = [];

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
        if (binanceCandles is not null)
            await ResolvePendingAsync(binanceCandles, watched, ct);

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
                tally.AddExchanges(last.Events, confirming, perpsOnly: !last.Exchanges.Any(e => e != Perp));
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

            var touched = checks.Values
                .Where(c => c?.Side == side)
                .SelectMany(c => TouchedZones(c!, watched))
                .DistinctBy(z => (z.Low, z.High))
                .ToList();
            var events = touched.Select(z => NewEvent(z, side, minute, showing, checks)).ToList();
            foreach (var e in events)
                tally.Add(e);
            _lastAlert[side] = (minute, showing, events.Select(e => e.Id).ToList());

            var alert = FormatAlert(side, showing, checks, watched) + FormatGap(gap);
            foreach (var zone in touched)
                alert += "\n\n" + await zoneMonitor.ScoreboardAsync(zone, ct);
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

    // The absorption at one zone, to follow until it holds or fails. Its level is the extreme of the absorption
    // windows (all markets showing it, plus Binance spot's window), so it fails only on a close past all of them.
    private ZoneAbsorption NewEvent(WatchedZone zone, AbsorptionSide side, DateTimeOffset minute, HashSet<string> showing,
        Dictionary<string, AbsorptionCheck?> checks)
    {
        var binance = checks.GetValueOrDefault(Binance);
        var windows = checks.Values.Where(c => c?.Side == side).Append(binance).OfType<AbsorptionCheck>().ToList();
        var main = binance ?? windows[0];
        return new ZoneAbsorption
        {
            Id = Guid.NewGuid(),
            ZoneLow = zone.Low,
            ZoneHigh = zone.High,
            Side = side,
            Minute = minute,
            Price = main.Candle.Close,
            Level = side == AbsorptionSide.Seller ? windows.Min(c => c.Low) : windows.Max(c => c.High),
            SpotVolume = binance?.Side == side ? (side == AbsorptionSide.Seller ? binance.SellVolume : binance.BuyVolume) : 0,
            Exchanges = string.Join(", ", Exchanges.Where(showing.Contains)),
            PerpsOnly = !showing.Any(e => e != Perp),
            Outcome = AbsorptionOutcome.Pending,
        };
    }

    // Decides pending absorptions from Binance spot closes, and sends a message for each one that failed.
    // Events from before a restart (older than the candles fetched every minute) get their own fetch.
    private async Task ResolvePendingAsync(IReadOnlyList<Candle> recent, IReadOnlyList<WatchedZone> watched, CancellationToken ct)
    {
        foreach (var pending in tally.Pending())
        {
            var candles = recent;
            if (recent.Count == 0 || recent[0].OpenTime > pending.Minute + Minute)
            {
                try
                {
                    candles = await binance.GetClosedCandlesAsync(_options.Symbol, pending.Minute + Minute, pending.Minute + _hold + Minute, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    logger.LogWarning("Binance candles for an earlier absorption failed: {Message}", ex.Message);
                    continue;
                }
            }

            if (tally.Resolve(pending.Id, candles, _hold) is not { } decided)
                continue;
            logger.LogInformation("{Side} absorption at {Minute:HH:mm} at zone {Low:N0}-{High:N0}: {Outcome}",
                decided.Side, decided.Minute, decided.ZoneLow, decided.ZoneHigh, decided.Outcome);

            var zone = watched.FirstOrDefault(decided.IsAt);
            if (decided.Outcome != AbsorptionOutcome.Failed || zone is null)
                continue;
            var text = FormatFailure(decided, zone) + "\n\n" + await zoneMonitor.ScoreboardAsync(zone, ct);
            alertLog.Record(new SentAlert
            {
                Kind = AlertKind.AbsorptionFailed,
                Side = decided.Side,
                Minute = decided.ResolvedMinute,
                ConfirmsMinute = decided.Minute,
                Price = decided.ResolvedClose,
                Exchanges = decided.Exchanges,
                SpotAndPerps = !decided.PerpsOnly && decided.Exchanges.Contains(Perp),
                Zones = ZoneName(zone),
                Text = text,
                SentAt = DateTimeOffset.UtcNow,
                Delivered = await SendAsync(text, ct),
            });
        }
    }

    // "✗ Seller absorption from 14:02 failed at support 77,000–77,400: Binance closed 76,950, below its low 76,980 (12 min later)"
    private string FormatFailure(ZoneAbsorption e, WatchedZone zone)
    {
        var seller = e.Side == AbsorptionSide.Seller;
        var at = TimeZoneInfo.ConvertTime(e.Minute, _timeZone);
        var after = (e.ResolvedMinute!.Value - e.Minute).TotalMinutes;
        return $"✗ {(seller ? "Seller absorption (bullish)" : "Buyer absorption (bearish)")} from {at:HH:mm} failed at {ZoneName(zone)}: " +
               $"Binance closed {e.ResolvedClose:N0}, {(seller ? "below its low" : "above its high")} {e.Level:N0} ({after:0} min later). " +
               $"{(seller ? "Sellers ran over the patient buyers" : "Buyers ran over the patient sellers")}.";
    }

    private static string ZoneName(WatchedZone z) => z.Name;

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
            .DistinctBy(z => (z.Low, z.High))
            .Select(ZoneName);

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
