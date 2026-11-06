using System.Text;
using ChartAnalyzer.Models;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Analysis;

public enum SignKind { Good, Warning, Info }

/// <param name="Key">Stable identifier, so the live app alerts on each sign only once.</param>
/// <param name="Alert">Whether this sign is worth an immediate alert in the live app.</param>
public sealed record Sign(SignKind Kind, string Key, string Text, bool Alert = false)
{
    public string Symbol => Kind switch { SignKind.Good => "✓", SignKind.Warning => "⚠", _ => "·" };
}

public sealed record ZoneAnalysis(
    PriceZone Zone,
    ZoneSettings Settings,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<ZoneReport.DayStats> Days,
    IReadOnlyList<ZoneReport.TestStats> Tests,
    IReadOnlyList<ZoneReport.Climax> Climaxes,
    IReadOnlyList<ZoneReport.Bar> BeyondBars,
    IReadOnlyList<Sign> Signs,
    decimal? LastPrice,
    IReadOnlyList<MarketView>? Markets = null)
{
    public bool IsSupport => Zone.Role != ZoneRole.Resistance;

    /// <summary>Markets analyzed besides the main one (Binance spot).</summary>
    public IReadOnlyList<MarketView> OtherMarkets => Markets ?? [];
}

/// <summary>Candles from another market (Coinbase spot, Binance perp) at the same interval as the main ones.</summary>
/// <param name="MinAbsorbedVolume">The market's own absorption threshold; null uses the default.</param>
public sealed record MarketCandles(string Name, bool IsPerp, IReadOnlyList<Candle> Candles, decimal? MinAbsorbedVolume = null);

/// <summary>The zone analysis on another market.</summary>
/// <param name="DataSince">Set when the market's data starts after the analysis start (Coinbase is only stored from Nov 1 2026).</param>
public sealed record MarketView(string Name, bool IsPerp, ZoneAnalysis Analysis, DateTimeOffset? DataSince);

/// <summary>
/// Multi-day analysis of a support or resistance zone: is it being accumulated or distributed?
/// Local zones read 1-minute candles (hourly climaxes, 4-hour closes, 3-day effort window);
/// macro zones read 1-hour candles (daily climaxes, daily closes, 2-week effort window). See <see cref="ZoneSettings"/>.
///
/// For a support zone (resistance is mirrored):
/// - The defended edge is the bottom. Sellers attack it; big players defend it with patient buy orders,
///   which shows as seller absorption there.
/// - Each visit to the defended edge is a "test". Selling that gets weaker test after test suggests sellers are running out.
/// - A selling climax (a candle of extreme volume making a new low, then bought back hard) often marks big buyers stepping in.
/// - Lots of net buying over days with little price progress means patient sellers absorbed it: a warning.
///
/// Days are UTC days, matching the daily close on crypto charts.
///
/// Binance spot is the main market: tests, closes past the zone and effort vs result use its candles.
/// Other markets (Coinbase spot, Binance perp) add a per-market breakdown of net buying, absorption and climaxes,
/// shown per market rather than added up: perps trade ~8x spot volume, and the point is where they disagree.
/// </summary>
public sealed class ZoneReport(AbsorptionDetector detector, IOptions<AnalyzerOptions> options)
{
    // A test is a visit to the 25% of the zone nearest the defended edge (or beyond it).
    private const decimal EdgeBandShare = 0.25m;

    private static readonly int[] OutcomeDays = [1, 3, 7];

    // Climax: volume >= 2.5x the usual (median), a new extreme in the zone, and a close in the top 40% of the
    // candle's range (bottom 40% for resistance).
    private const decimal ClimaxMinVolumeRatio = 2.5m;
    private const decimal ClimaxMinCloseLocation = 0.6m;

    private readonly AnalyzerOptions _options = options.Value;
    private readonly TimeZoneInfo _timeZone = options.Value.GetTimeZone();

    /// <param name="candles">Candles at the profile's interval covering [from, to), plus <see cref="ZoneSettings.History"/> before.</param>
    /// <param name="otherMarkets">Candles from other markets for the per-market breakdown.</param>
    /// <param name="minAbsorbedVolume">Absorption threshold for these candles' market; null uses the default.</param>
    public ZoneAnalysis Analyze(IReadOnlyList<Candle> candles, PriceZone zone, DateTimeOffset from, DateTimeOffset to,
        IReadOnlyList<MarketCandles>? otherMarkets = null, decimal? minAbsorbedVolume = null)
    {
        var settings = ZoneSettings.For(zone.Profile);
        var support = zone.Role != ZoneRole.Resistance;
        var band = zone.Height * EdgeBandShare;
        var interval = settings.CandleInterval;

        bool InZone(decimal low, decimal high) => low <= zone.High && high >= zone.Low;

        // Near the defended edge, or past it by at most one zone height (further away isn't a test of this zone).
        bool AtDefendedEdge(decimal low, decimal high) => support
            ? low <= zone.Low + band && high >= zone.Low - zone.Height
            : high >= zone.High - band && low <= zone.High + zone.Height;
        bool AtFarEdge(decimal low, decimal high) => support
            ? high >= zone.High - band && low <= zone.High + band
            : low <= zone.Low + band && high >= zone.Low - band;

        // A climax counts when its extreme is inside the zone, or past the defended edge by at most one zone height.
        bool ClimaxInZone(Bar b) => support
            ? b.Low <= zone.High && b.Low >= zone.Low - zone.Height
            : b.High >= zone.Low && b.High <= zone.High + zone.Height;

        decimal AttackVolume(Candle c) => support ? c.SellVolume : c.TakerBuyVolume;

        var days = new SortedDictionary<DateTime, DayStats>();
        var tests = new List<TestStats>();
        var cooldown = new SignalCooldown(_options.CooldownMinutes);
        decimal? lastPrice = null;

        for (var i = 0; i < candles.Count; i++)
        {
            var c = candles[i];
            if (c.OpenTime < from || c.OpenTime >= to)
                continue;
            lastPrice = c.Close;

            var date = c.OpenTime.UtcDateTime.Date;
            if (!days.TryGetValue(date, out var day))
                days[date] = day = new DayStats { Date = date, Open = c.Open, Low = c.Low, High = c.High };
            day.Low = Math.Min(day.Low, c.Low);
            day.High = Math.Max(day.High, c.High);
            day.Close = c.Close;
            day.TotalVolume += c.Volume;
            day.TotalNetBuying += c.TakerBuyVolume - c.SellVolume;

            if (InZone(c.Low, c.High))
            {
                day.TimeInZone += interval;
                day.Volume += c.Volume;
                day.NetBuying += c.TakerBuyVolume - c.SellVolume;
            }

            TestStats? test = null;
            if (AtDefendedEdge(c.Low, c.High))
            {
                test = tests.LastOrDefault();
                if (test is null || c.OpenTime - test.LastTime > settings.TestGap)
                    tests.Add(test = new TestStats { Start = c.OpenTime, Extreme = support ? c.Low : c.High });
                test.LastTime = c.OpenTime;
                test.Duration += interval;
                test.Attack += AttackVolume(c);
                test.NetBuying += c.TakerBuyVolume - c.SellVolume;
                test.Extreme = support ? Math.Min(test.Extreme, c.Low) : Math.Max(test.Extreme, c.High);
                test.LastClose = c.Close;
            }

            if (settings.DetectAbsorption && detector.Check(candles, i, minAbsorbedVolume) is { } check && cooldown.IsNewSignal(check))
            {
                var defense = check.Side == (support ? AbsorptionSide.Seller : AbsorptionSide.Buyer);
                var absorbed = check.Side == AbsorptionSide.Seller ? check.SellVolume : check.BuyVolume;
                if (defense && AtDefendedEdge(check.Low, check.High))
                {
                    day.DefenseCount++;
                    day.DefenseVolume += absorbed;
                    if (test is not null)
                        test.DefenseCount++;
                }
                else if (!defense && AtFarEdge(check.Low, check.High))
                {
                    day.WarningCount++;
                    day.WarningVolume += absorbed;
                }
            }
        }

        var climaxBars = Aggregate(candles, settings.ClimaxPeriod, interval);
        var climaxes = FindClimaxes(climaxBars, settings, from, to, support, ClimaxInZone);

        // Complete candles (at the profile's close period) in the range that closed past the defended edge.
        var beyondBars = Aggregate(candles, settings.BeyondPeriod, interval)
            .Where(b => b.Start >= from && b.Start + settings.BeyondPeriod <= to && b.IsComplete
                        && (support ? b.Close < zone.Low : b.Close > zone.High))
            .ToList();

        var dayList = days.Values.ToList();
        var signs = BuildSigns(zone, settings, dayList, tests, climaxes, beyondBars, to, support);

        var markets = (otherMarkets ?? [])
            .Where(m => m.Candles.Any(c => c.OpenTime >= from && c.OpenTime < to))
            .Select(m => new MarketView(m.Name, m.IsPerp, Analyze(m.Candles, zone, from, to, null, m.MinAbsorbedVolume),
                m.Candles[0].OpenTime > from ? m.Candles[0].OpenTime : null))
            .ToList();
        if (markets.Count > 0)
            AddMarketSigns(signs, settings, dayList, climaxes, markets, support, to);

        return new ZoneAnalysis(zone, settings, from, to, dayList, tests, climaxes, beyondBars, signs, lastPrice, markets);
    }

    /// <summary>Combines candles into bars of the given period, aligned to UTC (hours, 4-hour blocks, days).</summary>
    internal static List<Bar> Aggregate(IReadOnlyList<Candle> candles, TimeSpan period, TimeSpan interval)
    {
        var expected = (int)(period / interval);
        return candles
            .GroupBy(c => new DateTimeOffset(c.OpenTime.UtcTicks - c.OpenTime.UtcTicks % period.Ticks, TimeSpan.Zero))
            .Select(g => new Bar(g.Key, g.First().Open, g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close,
                g.Sum(c => c.Volume), g.Sum(c => c.TakerBuyVolume - c.SellVolume), g.Count() >= expected))
            .ToList();
    }

    /// <summary>Selling climaxes (support) or buying climaxes (resistance) in [from, to).</summary>
    private static List<Climax> FindClimaxes(List<Bar> bars, ZoneSettings settings, DateTimeOffset from, DateTimeOffset to,
        bool support, Func<Bar, bool> inZone)
    {
        var climaxes = new List<Climax>();
        for (var i = settings.ClimaxVolumeLookback; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (bar.Start < from || bar.Start >= to || !bar.IsComplete || bar.High == bar.Low || !inZone(bar))
                continue;

            var previous = bars.Skip(i - settings.ClimaxVolumeLookback).Take(settings.ClimaxVolumeLookback).ToList();
            var usualVolume = previous.Select(p => p.Volume).Order().ElementAt(previous.Count / 2);
            var volumeRatio = usualVolume == 0 ? 0 : bar.Volume / usualVolume;

            var recent = previous.TakeLast(settings.ClimaxExtremeLookback).ToList();
            var newExtreme = support ? bar.Low < recent.Min(p => p.Low) : bar.High > recent.Max(p => p.High);

            // 1 = closed at the high, 0 = at the low. A selling climax closes high, a buying climax low.
            var closeLocation = (bar.Close - bar.Low) / (bar.High - bar.Low);
            var rejected = support ? closeLocation >= ClimaxMinCloseLocation : closeLocation <= 1 - ClimaxMinCloseLocation;

            if (volumeRatio >= ClimaxMinVolumeRatio && newExtreme && rejected)
            {
                var extreme = support ? bar.Low : bar.High;
                var dayLater = bar.Start + settings.ClimaxPeriod + TimeSpan.FromDays(1);
                var later = bars.LastOrDefault(b => b.Start + settings.ClimaxPeriod <= dayLater);
                var close24h = later is not null && later.Start + settings.ClimaxPeriod == dayLater ? later.Close : (decimal?)null;
                climaxes.Add(new Climax(bar, settings.ClimaxPeriod, volumeRatio, extreme, RoundNumberNote(extreme), close24h));
            }
        }
        return climaxes;
    }

    private List<Sign> BuildSigns(PriceZone zone, ZoneSettings settings, List<DayStats> days, List<TestStats> tests,
        List<Climax> climaxes, List<Bar> beyondBars, DateTimeOffset to, bool support)
    {
        var signs = new List<Sign>();
        var attacker = support ? "Selling" : "Buying";
        var edge = support ? zone.Low : zone.High;

        signs.AddRange(climaxes.Select(c => ClimaxSign(c, support)));

        // Is the attacking side running out? Compare the last test with the earlier ones (per hour, as tests differ in length).
        if (tests.Count >= 3)
        {
            var last = tests[^1].AttackPerHour;
            var earlier = tests.SkipLast(1).Average(t => t.AttackPerHour);
            var ratio = earlier == 0 ? 1 : last / earlier;
            var detail = $"last test {last:N0}/h vs {earlier:N0}/h on earlier tests";
            signs.Add(ratio switch
            {
                < 0.7m => new Sign(SignKind.Good, "tests-weaker", $"{attacker} on tests is getting weaker: {detail}"),
                > 1.3m => new Sign(SignKind.Warning, "tests-stronger", $"{attacker} on tests is getting stronger: {detail}"),
                _ => new Sign(SignKind.Info, "tests-same", $"{attacker} on tests is about the same: {detail}"),
            });
        }
        else
        {
            signs.Add(new Sign(SignKind.Info, "tests-few", tests.Count == 0
                ? "No tests of the edge yet"
                : $"Only {tests.Count} test(s) of the edge, too few to see a trend"));
        }

        var breaks = tests.Where(t => support ? t.Extreme < edge : t.Extreme > edge).ToList();
        if (breaks.Count > 0)
        {
            var failed = breaks.Count(t => !Recovered(t, support, edge));
            signs.Add(failed == 0
                ? new Sign(SignKind.Good, "breaks-recovered", $"Price went {(support ? "below" : "above")} the edge {breaks.Count} time(s) and recovered every time")
                : new Sign(SignKind.Warning, "breaks-failed", $"Price went {(support ? "below" : "above")} the edge {breaks.Count} time(s), {failed} without recovering"));
        }

        // The most recent closed candle (4-hour or daily) closed past the defended edge.
        var lastBeyond = beyondBars.LastOrDefault();
        if (lastBeyond is not null && lastBeyond.Start + settings.BeyondPeriod * 2 > to)
            signs.Add(BeyondSign(settings, support, [lastBeyond]));

        var recent = days.TakeLast(settings.EffortDays).ToList();
        if (recent.Count > 0)
        {
            signs.Add(new Sign(SignKind.Info, "zone-net", $"Net buying in the zone, last {Days(recent.Count)}: {recent.Sum(d => d.NetBuying):+#,0;-#,0;0} BTC"));
            signs.Add(EffortSign(recent, $"{to.UtcDateTime:yyyy-MM-dd}", support, settings.EffortDays));

            var defenseCount = recent.Sum(d => d.DefenseCount);
            if (defenseCount > 0)
                signs.Add(new Sign(SignKind.Good, "defense",
                    $"Last {Days(recent.Count)}: {Events(defenseCount, recent.Sum(d => d.DefenseVolume))} of absorption at the " +
                    $"{(support ? "bottom" : "top")}, patient {(support ? "buyers" : "sellers")} defending"));

            // Informational only: in the zones tested so far this appeared before holds and breaks alike.
            var warningCount = recent.Sum(d => d.WarningCount);
            if (warningCount > 0)
                signs.Add(new Sign(SignKind.Info, "far-edge",
                    $"Last {Days(recent.Count)}: {Events(warningCount, recent.Sum(d => d.WarningVolume))} of absorption at the " +
                    $"{(support ? "top" : "bottom")}: heavy {(support ? "buying" : "selling")} not moving the price"));
        }
        return signs;
    }

    // 'also' names other markets with a climax on the same candle.
    private Sign ClimaxSign(Climax c, bool support, IReadOnlyList<string>? also = null) => new(SignKind.Good, $"climax:{c.Bar.Start:yyyy-MM-ddTHH}",
        $"{(support ? "Selling" : "Buying")} climax {(c.Period >= TimeSpan.FromDays(1) ? "day" : "hour")} {When(c.Bar.Start, c.Period)}: " +
        $"{c.VolumeRatio:0.0}x usual volume, {(support ? "low" : "high")} {c.Extreme:N0}{c.RoundNumberNote}, then pushed back hard to {c.Bar.Close:N0}" +
        (also is { Count: > 0 } ? $" · also on {string.Join(" + ", also)}" : ""),
        Alert: true);

    // A climax on other markets only. Perps-only usually means a liquidation flush rather than real capitulation.
    // 'alert' is false for climaxes found after the fact (e.g. on the first run after this was added), so they aren't sent late.
    private Sign OtherMarketClimaxSign(Climax c, bool support, IReadOnlyList<string> names, bool perpsOnly, bool alert = true) => new(
        perpsOnly ? SignKind.Info : SignKind.Good,
        $"climax:{c.Bar.Start:yyyy-MM-ddTHH}:{string.Join("+", names)}",
        $"{(support ? "Selling" : "Buying")} climax on {string.Join(" + ", names)} only, {(c.Period >= TimeSpan.FromDays(1) ? "day" : "hour")} " +
        $"{When(c.Bar.Start, c.Period)}: {c.VolumeRatio:0.0}x usual volume, {(support ? "low" : "high")} {c.Extreme:N0}{c.RoundNumberNote}, " +
        $"pushed back to {c.Bar.Close:N0}" +
        (perpsOnly ? ". ⚠ Likely liquidations: Binance spot volume wasn't extreme" : ""),
        Alert: alert);

    /// <summary>
    /// Adds the other markets to the signs: net buying per market and whether spot and perps agree,
    /// absorption per market, and climaxes that happened on other markets.
    /// </summary>
    private void AddMarketSigns(List<Sign> signs, ZoneSettings settings, List<DayStats> days, List<Climax> climaxes,
        List<MarketView> markets, bool support, DateTimeOffset to)
    {
        var recentDates = days.TakeLast(settings.EffortDays).Select(d => d.Date).ToHashSet();
        var all = new List<(string Name, bool IsPerp, List<DayStats> Days, DateTimeOffset? Since)> { ("Binance", false, days, null) };
        all.AddRange(markets.Select(m => (m.Name, m.IsPerp, m.Analysis.Days.ToList(), m.DataSince)));
        all = all.Select(m => (m.Name, m.IsPerp, m.Days.Where(d => recentDates.Contains(d.Date)).ToList(), m.Since)).ToList();
        string Since(DateTimeOffset? since) => since is { } s ? $", data since {Local(s):ddd HH:mm}" : "";
        string Net(decimal value) => $"{value:+#,0;-#,0;0}";

        if (recentDates.Count > 0)
        {
            var period = $"last {Days(recentDates.Count)}";
            var spot = all.Where(m => !m.IsPerp).ToList();
            var perps = all.Where(m => m.IsPerp).ToList();
            var spotNet = spot.Sum(m => m.Days.Sum(d => d.NetBuying));
            var perpNet = perps.Sum(m => m.Days.Sum(d => d.NetBuying));
            var text = $"Net buying in the zone, {period}: spot {Net(spotNet)} BTC (" +
                       string.Join(", ", spot.Select(m => $"{m.Name} {Net(m.Days.Sum(d => d.NetBuying))}{Since(m.Since)}")) + ")" +
                       (perps.Count > 0 ? $" · perps {Net(perpNet)} BTC" : "");
            Replace(signs, "zone-net", new Sign(SignKind.Info, "zone-net", text));

            // Spot buying into perp selling is bullish: good news for a support, bad news for a resistance.
            if (perps.Count > 0 && spotNet != 0 && perpNet != 0)
            {
                var bullish = support ? SignKind.Good : SignKind.Warning;
                var bearish = support ? SignKind.Warning : SignKind.Good;
                var sign = (spotNet > 0, perpNet > 0) switch
                {
                    (true, false) => new Sign(bullish, "spot-perp", "Spot buying while perps sell: spot buyers are taking the leveraged selling"),
                    (false, true) => new Sign(bearish, "spot-perp", "Perps buying while spot sells: the buying is leveraged, spot holders are selling into it"),
                    (true, true) => new Sign(SignKind.Info, "spot-perp", "Spot and perps both buying"),
                    _ => new Sign(SignKind.Info, "spot-perp", "Spot and perps both selling"),
                };
                signs.Insert(signs.FindIndex(s => s.Key == "zone-net") + 1, sign);
            }

            if (settings.DetectAbsorption)
            {
                var defense = all.Select(m => (m.Name, m.IsPerp, Count: m.Days.Sum(d => d.DefenseCount), Volume: m.Days.Sum(d => d.DefenseVolume))).ToList();
                if (defense.Any(d => d.Count > 0))
                {
                    var spotDefense = defense.Any(d => !d.IsPerp && d.Count > 0);
                    var sign = new Sign(spotDefense ? SignKind.Good : SignKind.Info, "defense",
                        $"Absorption at the {(support ? "bottom" : "top")}, {period}: " +
                        string.Join(" · ", defense.Select(d => $"{d.Name} {Events(d.Count, d.Volume)}")) +
                        (spotDefense ? $", patient {(support ? "buyers" : "sellers")} defending" : ". Perps only: often liquidations"));
                    if (!Replace(signs, "defense", sign))
                    {
                        var farEdge = signs.FindIndex(s => s.Key == "far-edge");
                        signs.Insert(farEdge < 0 ? signs.Count : farEdge, sign);
                    }
                }
            }
        }

        // Climaxes: name the other markets on the main market's climaxes, and add the ones that happened elsewhere only.
        var extra = new List<Sign>();
        foreach (var group in OtherClimaxes(markets))
        {
            var names = group.Select(x => x.Name).ToList();
            var main = climaxes.FirstOrDefault(c => c.Bar.Start == group.Key);
            if (main is not null)
                Replace(signs, ClimaxSign(main, support).Key, ClimaxSign(main, support, names));
            else
            {
                // Only alert on a climax that just finished: the live check runs every hour (macro: every day).
                var c = group.First().Climax;
                var fresh = c.Bar.Start + c.Period * 2 >= to;
                extra.Add(OtherMarketClimaxSign(c, support, names, group.All(x => x.IsPerp), fresh));
            }
        }
        signs.InsertRange(climaxes.Count, extra);
    }

    private static List<IGrouping<DateTimeOffset, (string Name, bool IsPerp, Climax Climax)>> OtherClimaxes(IReadOnlyList<MarketView> markets) =>
        markets
            .SelectMany(m => m.Analysis.Climaxes.Select(c => (m.Name, m.IsPerp, Climax: c)))
            .GroupBy(x => x.Climax.Bar.Start)
            .ToList();

    private static bool Replace(List<Sign> signs, string key, Sign sign)
    {
        var index = signs.FindIndex(s => s.Key == key);
        if (index < 0)
            return false;
        signs[index] = sign;
        return true;
    }

    // Price closed past the defended edge on these candles (all on the same day). Alerted once per day.
    private Sign BeyondSign(ZoneSettings settings, bool support, List<Bar> bars)
    {
        var first = bars[0];
        var candle = settings.BeyondPeriod >= TimeSpan.FromDays(1) ? "daily" : $"{settings.BeyondPeriod.TotalHours:0}-hour";
        var extreme = support ? bars.Min(b => b.Close) : bars.Max(b => b.Close);
        var text = bars.Count == 1
            ? $"A {candle} candle closed {(support ? "below" : "above")} the zone: {first.Close:N0} ({When(first.Start, settings.BeyondPeriod)})"
            : $"{Capitalize(candle)} candles closed {(support ? "below" : "above")} the zone {bars.Count} times, {(support ? "lowest" : "highest")} close {extreme:N0}";
        return new Sign(SignKind.Warning, $"beyond:{first.Start.UtcDateTime:yyyy-MM-dd}", text, Alert: true);
    }

    /// <summary>
    /// Effort vs result over the given days, at all prices: did heavy one-sided aggression move the price?
    /// A lot of buying with little progress means patient sellers absorbed it (and vice versa).
    /// ✓/⚠ are from the zone's point of view: patient sellers help a resistance hold but threaten a support.
    /// </summary>
    private static Sign EffortSign(List<DayStats> recent, string dateKey, bool support, int requiredDays)
    {
        var totalNet = recent.Sum(d => d.TotalNetBuying);
        var share = recent.Sum(d => d.TotalVolume) is var volume and > 0 ? totalNet / volume : 0;
        var open = recent[0].Open;
        var close = recent[^1].Close;
        var change = (close - open) / open;
        var effort = $"Last {Days(recent.Count)}: net buying {totalNet:+#,0;-#,0;0} BTC ({share:+0%;-0%} of volume), " +
                     $"price {Percent(close, open)} ({open:N0} → {close:N0})";

        // A shorter window than the profile's is too noisy to judge.
        if (recent.Count < requiredDays)
            return new Sign(SignKind.Info, "effort", effort);

        return share switch
        {
            >= 0.10m when change < 0.02m => new Sign(support ? SignKind.Warning : SignKind.Good, $"effort-buying:{dateKey}",
                $"{effort}. Lots of buying, little progress: patient sellers absorbing", Alert: true),
            <= -0.10m when change > -0.02m => new Sign(support ? SignKind.Good : SignKind.Warning, $"effort-selling:{dateKey}",
                $"{effort}. Lots of selling, little damage: patient buyers absorbing", Alert: true),
            _ => new Sign(SignKind.Info, "effort", effort),
        };
    }

    /// <summary>
    /// The alerts the live app would have sent over the range, in order: climaxes, days where the trailing
    /// effort-vs-result condition held, and days with a candle closing past the zone.
    /// Days are evaluated at their close, which is how macro zones are checked live; local zones are checked hourly.
    /// </summary>
    public List<(DateTimeOffset Time, Sign Sign)> Timeline(ZoneAnalysis a)
    {
        var events = new List<(DateTimeOffset, Sign)>();
        events.AddRange(a.Climaxes.Select(c => (c.Bar.Start + c.Period, ClimaxSign(c, a.IsSupport,
            OtherClimaxes(a.OtherMarkets).FirstOrDefault(g => g.Key == c.Bar.Start)?.Select(x => x.Name).ToList()))));
        events.AddRange(OtherClimaxes(a.OtherMarkets)
            .Where(g => a.Climaxes.All(c => c.Bar.Start != g.Key))
            .Select(g => (g.Key + g.First().Climax.Period,
                OtherMarketClimaxSign(g.First().Climax, a.IsSupport, g.Select(x => x.Name).ToList(), g.All(x => x.IsPerp)))));

        var window = a.Settings.EffortDays;
        for (var i = 0; i < a.Days.Count; i++)
        {
            var recent = a.Days.Skip(Math.Max(0, i - window + 1)).Take(Math.Min(window, i + 1)).ToList();
            var day = a.Days[i];
            if (EffortSign(recent, $"{day.Date:yyyy-MM-dd}", a.IsSupport, window) is { Alert: true } effort)
                events.Add((new DateTimeOffset(day.Date, TimeSpan.Zero).AddDays(1), effort));
        }

        foreach (var group in a.BeyondBars.GroupBy(b => b.Start.UtcDateTime.Date))
        {
            var bars = group.ToList();
            events.Add((bars[0].Start + a.Settings.BeyondPeriod, BeyondSign(a.Settings, a.IsSupport, bars)));
        }
        return events.OrderBy(e => e.Item1).ToList();
    }

    /// <summary>The full report for the console, including what happened after the range (if the candles cover it).</summary>
    public string FormatFull(ZoneAnalysis a, IReadOnlyList<Candle> candles)
    {
        var support = a.IsSupport;
        var sb = new StringBuilder();
        sb.AppendLine($"{a.Zone.Profile} {(support ? "support" : "resistance")} zone {a.Zone.Low:N0}-{a.Zone.High:N0}, " +
                      $"{Local(a.From):yyyy-MM-dd} to {Local(a.To):yyyy-MM-dd} ({_options.Symbol}, Binance spot; days below are UTC days)");

        var defense = support ? "patient buyers at bottom" : "patient sellers at top";
        var warning = support ? "patient sellers at top" : "patient buyers at bottom";
        sb.AppendLine(a.OtherMarkets.Count > 0 ? "Binance spot, per day:" : "Per day:");
        sb.AppendLine($"  {"Day",-10} {"In zone",7} {"Volume",8} {"Net buy",8} {"Low",8} {"High",8} {"Close",8}" +
                      (a.Settings.DetectAbsorption ? $"  Absorption: {defense} / {warning}" : ""));
        foreach (var d in a.Days)
        {
            sb.AppendLine($"  {d.Date:ddd MM-dd} {d.TimeInZone.TotalHours,6:0.0}h {d.Volume,8:N0} {d.NetBuying,8:+#,0;-#,0;0} {d.Low,8:N0} {d.High,8:N0} {d.Close,8:N0}" +
                          (a.Settings.DetectAbsorption ? $"  {Events(d.DefenseCount, d.DefenseVolume)} / {Events(d.WarningCount, d.WarningVolume)}" : ""));
        }

        foreach (var m in a.OtherMarkets)
        {
            sb.AppendLine($"{m.Name}, per day (in the zone){(m.DataSince is { } since ? $", data since {Local(since):ddd MM-dd HH:mm}" : "")}:");
            foreach (var d in m.Analysis.Days)
                sb.AppendLine($"  {d.Date:ddd MM-dd} {d.TimeInZone.TotalHours,6:0.0}h {d.Volume,8:N0} {d.NetBuying,8:+#,0;-#,0;0}" +
                              (a.Settings.DetectAbsorption ? $"  absorption {Events(d.DefenseCount, d.DefenseVolume)} / {Events(d.WarningCount, d.WarningVolume)}" : ""));
        }

        var edge = support ? a.Zone.Low : a.Zone.High;
        var band = a.Zone.Height * EdgeBandShare;
        sb.AppendLine($"Tests of the {(support ? "bottom" : "top")} edge (price at or {(support ? $"below {edge + band:N0}" : $"above {edge - band:N0}")}):");
        if (a.Tests.Count == 0)
            sb.AppendLine("  none");
        for (var n = 0; n < a.Tests.Count; n++)
        {
            var t = a.Tests[n];
            var beyond = support ? edge - t.Extreme : t.Extreme - edge;
            var extreme = beyond > 0
                ? $"{(support ? "low" : "high")} {t.Extreme:N0} ({beyond:N0} {(support ? "below" : "above")}, {(Recovered(t, support, edge) ? "recovered" : "did NOT recover")})"
                : $"{(support ? "low" : "high")} {t.Extreme:N0}";
            sb.AppendLine($"  #{n + 1,-2} {Local(t.Start):ddd MM-dd HH:mm} {t.Duration.TotalHours,5:0.0}h  {extreme}, " +
                          $"{(support ? "selling" : "buying")} {t.AttackPerHour:N0}/h, net buy {t.NetBuying:+#,0;-#,0;0}" +
                          (a.Settings.DetectAbsorption ? $", absorption {t.DefenseCount}" : ""));
        }

        sb.AppendLine($"{(support ? "Selling" : "Buying")} climaxes ({(a.Settings.ClimaxPeriod >= TimeSpan.FromDays(1) ? "daily" : "hourly")} candles):");
        if (a.Climaxes.Count == 0)
            sb.AppendLine("  none");
        foreach (var c in a.Climaxes)
        {
            var b = c.Bar;
            var wick = support ? b.Close - b.Low : b.High - b.Close;
            var next = c.Close24hLater is { } later ? $", 24h later {later:N0} ({Percent(later, b.Close)})" : "";
            sb.AppendLine($"  {When(b.Start, c.Period)}  {(support ? "low" : "high")} {c.Extreme:N0}{c.RoundNumberNote}, close {b.Close:N0} " +
                          $"({wick:N0} off the {(support ? "low" : "high")}), volume {b.Volume:N0} ({c.VolumeRatio:0.0}x usual), " +
                          $"net buy {b.NetBuying:+#,0;-#,0;0}{next}");
        }

        sb.AppendLine("Signs at the end of the range:");
        foreach (var s in a.Signs)
            sb.AppendLine($"  {s.Symbol} {s.Text}");

        sb.AppendLine("Alerts the live app would have sent:");
        var timeline = Timeline(a);
        if (timeline.Count == 0)
            sb.AppendLine("  none");
        foreach (var (time, sign) in timeline)
            sb.AppendLine($"  {Local(time):ddd yyyy-MM-dd HH:mm}  {sign.Symbol} {sign.Text}");

        AppendOutcome(sb, candles, a.To);
        return sb.ToString().TrimEnd();
    }

    /// <summary>A short version for Telegram.</summary>
    public string FormatSummary(ZoneAnalysis a)
    {
        var support = a.IsSupport;
        var sb = new StringBuilder();
        var profile = a.Zone.Profile == ZoneProfile.Macro ? " (macro)" : "";
        sb.AppendLine($"{(support ? "🟩 Support" : "🟥 Resistance")}{profile} {a.Zone.Low:N0}–{a.Zone.High:N0} (since {Local(a.From):ddd MM-dd})");
        if (a.LastPrice is { } price)
        {
            var position = price < a.Zone.Low ? "below the zone" : price > a.Zone.High ? "above the zone" : "in the zone";
            sb.AppendLine($"BTC {price:N0} ({position})");
        }

        sb.AppendLine();
        if (a.OtherMarkets.Count > 0)
            sb.AppendLine("Binance spot:");
        foreach (var d in a.Days.TakeLast(a.Settings.SummaryDays))
        {
            sb.AppendLine($"{d.Date:ddd MM-dd}: {d.TimeInZone.TotalHours:0.#}h in zone, net buy {d.NetBuying:+#,0;-#,0;0}, " +
                          $"low {d.Low:N0}, close {d.Close:N0}" +
                          (a.Settings.DetectAbsorption ? $", absorption {(support ? "bottom" : "top")} {Events(d.DefenseCount, d.DefenseVolume)}" : ""));
        }
        sb.AppendLine($"Tests of the {(support ? "bottom" : "top")} edge: {a.Tests.Count}, climaxes: {a.Climaxes.Count}");

        sb.AppendLine();
        foreach (var s in a.Signs)
            sb.AppendLine($"{s.Symbol} {s.Text}");
        return sb.ToString().TrimEnd();
    }

    private static void AppendOutcome(StringBuilder sb, IReadOnlyList<Candle> candles, DateTimeOffset to)
    {
        var start = candles.LastOrDefault(c => c.OpenTime < to);
        if (start is null || candles[^1].OpenTime < to)
            return;

        sb.AppendLine($"What happened next (from {start.Close:N0}):");
        var parts = OutcomeDays.Select(d =>
        {
            var later = candles.LastOrDefault(c => c.OpenTime < to.AddDays(d));
            return later is not null && later.OpenTime >= to.AddDays(d).AddHours(-1)
                ? $"+{d}d {later.Close:N0} ({Percent(later.Close, start.Close)})"
                : $"+{d}d not yet";
        });
        sb.AppendLine("  " + string.Join(" | ", parts));

        var after = candles.Where(c => c.OpenTime >= to && c.OpenTime < to.AddDays(OutcomeDays[^1])).ToList();
        if (after.Count > 0)
        {
            var low = after.Min(c => c.Low);
            var high = after.Max(c => c.High);
            sb.AppendLine($"  Within {OutcomeDays[^1]} days: high {high:N0} ({Percent(high, start.Close)}), low {low:N0} ({Percent(low, start.Close)})");
        }
    }

    // Big players like round numbers: note when the extreme is within 0.15% of a multiple of 1,000.
    private static string RoundNumberNote(decimal price)
    {
        var round = Math.Round(price / 1000) * 1000;
        return Math.Abs(price - round) <= round * 0.0015m ? $" (round number {round:N0})" : "";
    }

    // A test that went past the edge "recovered" if its last candle closed back on the right side.
    private static bool Recovered(TestStats t, bool support, decimal edge) => support ? t.LastClose >= edge : t.LastClose <= edge;

    // Daily candles are shown as a date (UTC), shorter ones with their local start time.
    private string When(DateTimeOffset start, TimeSpan period) =>
        period >= TimeSpan.FromDays(1) ? $"{start.UtcDateTime:ddd MM-dd}" : $"{Local(start):ddd MM-dd HH:mm}";

    private DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, _timeZone);
    private static string Days(int count) => count == 1 ? "day" : $"{count} days";
    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
    private static string Events(int count, decimal volume) => count == 0 ? "-" : $"{count} ({volume:N0} BTC)";
    private static string Percent(decimal value, decimal from) => $"{(value - from) / from:+0.0%;-0.0%}";

    /// <summary>A candle combined from smaller ones; incomplete when candles are missing or the period hasn't ended.</summary>
    public sealed record Bar(DateTimeOffset Start, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal NetBuying, bool IsComplete);

    public sealed record Climax(Bar Bar, TimeSpan Period, decimal VolumeRatio, decimal Extreme, string RoundNumberNote, decimal? Close24hLater);

    public sealed class DayStats
    {
        public DateTime Date;
        public TimeSpan TimeInZone;
        public decimal Volume, NetBuying;                                     // in the zone
        public decimal Open, Low, High, Close, TotalVolume, TotalNetBuying;   // whole day
        public int DefenseCount, WarningCount;
        public decimal DefenseVolume, WarningVolume;
    }

    public sealed class TestStats
    {
        public DateTimeOffset Start, LastTime;
        public TimeSpan Duration;
        public int DefenseCount;
        public decimal Attack, NetBuying, Extreme, LastClose;
        public decimal AttackPerHour => Duration <= TimeSpan.Zero ? 0 : Attack / (decimal)Duration.TotalHours;
    }
}
