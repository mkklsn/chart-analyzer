using System.Text;
using ChartAnalyzer.Models;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Analysis;

/// <summary>A high-volume node: a price where a lot traded, so a lot of positions were opened there.</summary>
/// <param name="Strength">Volume at the peak, as a multiple of the median bin.</param>
public sealed record VolumeNode(PriceRange Range, decimal Peak, decimal Strength);

/// <summary>The point of control of a completed week, and whether price has traded there since.</summary>
public sealed record WeeklyPoc(DateTime WeekStart, decimal Price, bool Naked);

/// <summary>One absorption signal. Price is the window low for seller absorption (buyers defending), the high for buyer absorption.</summary>
public sealed record AbsorptionEvent(DateTimeOffset Time, AbsorptionSide Side, decimal Price, decimal Volume);

/// <summary>
/// A price where absorption was unusually dense for the volume traded there: where large passive orders defended a price.
/// </summary>
/// <param name="Intensity">Absorbed volume per traded volume at the peak, as a multiple of the whole range's rate.</param>
public sealed record AbsorptionCluster(PriceRange Range, IReadOnlyList<AbsorptionEvent> Events, decimal Intensity)
{
    public int SellerCount => Events.Count(e => e.Side == AbsorptionSide.Seller);
    public int BuyerCount => Events.Count(e => e.Side == AbsorptionSide.Buyer);
    public decimal SellerVolume => Events.Where(e => e.Side == AbsorptionSide.Seller).Sum(e => e.Volume);
    public decimal BuyerVolume => Events.Where(e => e.Side == AbsorptionSide.Buyer).Sum(e => e.Volume);
    public decimal Volume => Events.Sum(e => e.Volume);
    public int Days => Events.Select(e => e.Time.UtcDateTime.Date).Distinct().Count();
    public DateTimeOffset Last => Events.Max(e => e.Time);
}

/// <summary>A candidate level: the factors that agree on about the same price.</summary>
/// <param name="ProfileMarks">POC, value area high or low of the whole range, when inside the level.</param>
public sealed record Level(
    PriceRange Range,
    IReadOnlyList<VolumeNode> Nodes,
    IReadOnlyList<AbsorptionCluster> Clusters,
    IReadOnlyList<WeeklyPoc> NakedPocs,
    IReadOnlyList<string> ProfileMarks,
    decimal? RoundNumber)
{
    /// <summary>How many independent kinds of factor agree.</summary>
    public int Score => (Nodes.Count > 0 ? 1 : 0) + (Clusters.Count > 0 ? 1 : 0) + (NakedPocs.Count > 0 ? 1 : 0)
                        + (ProfileMarks.Count > 0 ? 1 : 0) + (RoundNumber is null ? 0 : 1);
}

public sealed record LevelScan(
    DateTimeOffset From,
    DateTimeOffset To,
    decimal BinSize,
    decimal LastPrice,
    decimal Poc,
    PriceRange ValueArea,
    IReadOnlyList<VolumeNode> Nodes,
    IReadOnlyList<PriceRange> ThinAreas,
    IReadOnlyList<WeeklyPoc> WeeklyPocs,
    IReadOnlyList<AbsorptionEvent> Absorption,
    IReadOnlyList<AbsorptionCluster> Clusters,
    IReadOnlyList<Level> Levels);

/// <summary>
/// Finds candidate support and resistance levels from 1-minute candles, by confluence of:
/// - High-volume nodes of the volume profile: prices where a lot of positions were opened, which their owners tend to defend.
/// - Absorption hotspots (from <see cref="AbsorptionDetector"/>): prices where large passive orders actually defended.
///   Absorption happens wherever price spends time (often 20+ signals a day), so a hotspot is where absorbed volume
///   is high relative to the volume traded there, not just where there was some.
/// - Naked weekly POCs (the most-traded price of a week, not revisited since), the range's POC and value area edges,
///   and round numbers.
/// Low-volume nodes ("thin areas") are listed separately: price tends to move through them quickly.
/// </summary>
public sealed class LevelScanner(AbsorptionDetector detector, IOptions<AnalyzerOptions> options)
{
    // Volume profile, in bins: smoothed over 5 bins, a node peak is the highest within 5 bins either side.
    private const int SmoothRadius = 2;
    private const int PeakRadius = 5;

    // A peak (node or hotspot) spans the bins at 85% of its height or more, at most PeakRadius bins either side.
    private const decimal PeakExtent = 0.85m;

    // A high-volume node peaks at 1.3x the median bin or more.
    private const decimal NodeMinStrength = 1.3m;

    // A thin area is a run of bins under half the median, between busier prices.
    private const decimal ThinMaxStrength = 0.5m;

    // A hotspot absorbs (on one side) at least 1.4x the range's rate, with 3+ signals on 2+ days. On Sep-Oct 2026
    // the strongest one-sided hotspot was 2.2x (both sides together never passed 1.7x).
    private const decimal HotspotMinIntensity = 1.4m;
    private const int HotspotMinEvents = 3;
    private const int HotspotMinDays = 2;

    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FourHours = TimeSpan.FromHours(4);

    private readonly AnalyzerOptions _options = options.Value;
    private readonly TimeZoneInfo _timeZone = options.Value.GetTimeZone();

    /// <param name="candles">1-minute candles covering [from, to), plus the absorption lookback before.</param>
    /// <param name="binSize">Price bin size; null picks about 0.1% of the price.</param>
    public LevelScan Scan(IReadOnlyList<Candle> candles, DateTimeOffset from, DateTimeOffset to, decimal? binSize = null)
    {
        var inRange = candles.Where(c => c.OpenTime >= from && c.OpenTime < to).ToList();
        if (inRange.Count == 0)
            throw new InvalidOperationException("No candles in the range.");

        var lastPrice = inRange[^1].Close;
        var bin = binSize ?? NiceStep(lastPrice * 0.001m);
        var profile = VolumeProfile.Build(inRange, bin);
        var smoothed = profile.Smoothed(SmoothRadius);
        var median = Median(smoothed);

        var nodes = FindNodes(profile, smoothed, median);
        var thin = FindThinAreas(profile, smoothed, median);
        var weekly = WeeklyPocs(inRange, bin);
        var absorption = FindAbsorption(candles, from, to);
        var clusters = FindHotspots(profile, absorption);

        var poc = profile.Bin(profile.PocIndex).Mid;
        var valueArea = profile.ValueArea();
        var levels = BuildLevels(nodes, clusters, weekly, poc, valueArea, bin, NiceStep(lastPrice * 0.01m));
        return new LevelScan(from, to, bin, lastPrice, poc, valueArea, nodes, thin, weekly, absorption, clusters, levels);
    }

    private static List<VolumeNode> FindNodes(VolumeProfile profile, decimal[] s, decimal median) =>
        median == 0
            ? []
            : FindPeaks(s, median * NodeMinStrength)
                .Select(p => new VolumeNode(new PriceRange(profile.Bin(p.Low).Low, profile.Bin(p.High).High), profile.Bin(p.Peak).Mid, s[p.Peak] / median))
                .ToList();

    /// <summary>
    /// Local maxima of at least minHeight (the highest within PeakRadius bins), each with the bins around it
    /// at PeakExtent of its height or more.
    /// </summary>
    private static List<(int Peak, int Low, int High)> FindPeaks(decimal[] s, decimal minHeight)
    {
        var peaks = new List<(int Peak, int Low, int High)>();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] < minHeight || s[i] == 0)
                continue;
            // Strictly higher than the bins to the left, so a flat top counts once.
            var peak = true;
            for (var j = Math.Max(0, i - PeakRadius); j <= Math.Min(s.Length - 1, i + PeakRadius) && peak; j++)
                peak = j == i || (j < i ? s[i] > s[j] : s[i] >= s[j]);
            if (!peak)
                continue;

            int low = i, high = i;
            while (low > Math.Max(0, i - PeakRadius) && s[low - 1] >= s[i] * PeakExtent)
                low--;
            while (high < Math.Min(s.Length - 1, i + PeakRadius) && s[high + 1] >= s[i] * PeakExtent)
                high++;
            peaks.Add((i, low, high));
        }
        return peaks;
    }

    private static List<PriceRange> FindThinAreas(VolumeProfile profile, decimal[] s, decimal median)
    {
        var areas = new List<PriceRange>();
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] >= median * ThinMaxStrength)
            {
                i++;
                continue;
            }
            var start = i;
            while (i < s.Length && s[i] < median * ThinMaxStrength)
                i++;
            // The tails of the profile are thin too, but that's just where price barely went.
            if (start > 0 && i < s.Length)
                areas.Add(new PriceRange(profile.Bin(start).Low, profile.Bin(i - 1).High));
        }
        return areas;
    }

    // POC of each complete UTC week (Monday to Sunday) in the range, and whether price has traded there since.
    private static List<WeeklyPoc> WeeklyPocs(List<Candle> candles, decimal bin)
    {
        static DateTime WeekStart(DateTimeOffset t)
        {
            var date = t.UtcDateTime.Date;
            return date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        }

        var end = candles[^1].OpenTime + OneMinute;
        var result = new List<WeeklyPoc>();
        foreach (var week in candles.GroupBy(c => WeekStart(c.OpenTime)))
        {
            var weekEnd = new DateTimeOffset(week.Key.AddDays(7), TimeSpan.Zero);
            if (weekEnd > end || week.First().OpenTime > new DateTimeOffset(week.Key, TimeSpan.Zero))
                continue;   // incomplete week
            var profile = VolumeProfile.Build(week.ToList(), bin);
            var poc = profile.Bin(profile.PocIndex).Mid;
            var naked = !candles.Any(c => c.OpenTime >= weekEnd && c.Low <= poc && c.High >= poc);
            result.Add(new WeeklyPoc(week.Key, poc, naked));
        }
        return result;
    }

    private List<AbsorptionEvent> FindAbsorption(IReadOnlyList<Candle> candles, DateTimeOffset from, DateTimeOffset to)
    {
        var cooldown = new SignalCooldown(_options.CooldownMinutes);
        var events = new List<AbsorptionEvent>();
        for (var i = 0; i < candles.Count; i++)
        {
            if (candles[i].OpenTime < from || candles[i].OpenTime >= to)
                continue;
            if (detector.Check(candles, i) is not { } check || !cooldown.IsNewSignal(check))
                continue;
            var seller = check.Side == AbsorptionSide.Seller;
            events.Add(new AbsorptionEvent(check.Candle.OpenTime, check.Side!.Value,
                seller ? check.Low : check.High, seller ? check.SellVolume : check.BuyVolume));
        }
        return events;
    }

    /// <summary>
    /// Hotspots per side: seller absorption (buyers defending, support-like) and buyer absorption (resistance-like).
    /// Both sides together are spread almost evenly over the traded volume, so only one-sided absorption stands out.
    /// </summary>
    private static List<AbsorptionCluster> FindHotspots(VolumeProfile profile, List<AbsorptionEvent> events) =>
        [.. SideHotspots(profile, events.Where(e => e.Side == AbsorptionSide.Seller).ToList()).Concat(
            SideHotspots(profile, events.Where(e => e.Side == AbsorptionSide.Buyer).ToList()))];

    /// <summary>
    /// Prices where absorbed volume is high relative to the volume traded there: absorbed and traded volume
    /// per bin, both smoothed, divided, and compared with the rate over the whole range.
    /// </summary>
    private static IEnumerable<AbsorptionCluster> SideHotspots(VolumeProfile profile, IReadOnlyList<AbsorptionEvent> events)
    {
        if (events.Count == 0 || profile.IsEmpty)
            return [];

        int BinOf(decimal price) => Math.Clamp((int)((price - profile.Origin) / profile.BinSize), 0, profile.Volumes.Count - 1);
        var absorbed = new decimal[profile.Volumes.Count];
        foreach (var e in events)
            absorbed[BinOf(e.Price)] += e.Volume;

        var smoothAbsorbed = VolumeProfile.Smooth(absorbed, SmoothRadius);
        var smoothVolume = profile.Smoothed(SmoothRadius);
        var overall = events.Sum(e => e.Volume) / profile.Total;
        var intensity = smoothAbsorbed.Select((a, i) => smoothVolume[i] == 0 ? 0 : a / smoothVolume[i] / overall).ToArray();

        return FindPeaks(intensity, HotspotMinIntensity)
            .Select(p => new AbsorptionCluster(
                new PriceRange(profile.Bin(p.Low).Low, profile.Bin(p.High).High),
                events.Where(e => BinOf(e.Price) >= p.Low && BinOf(e.Price) <= p.High).ToList(),
                intensity[p.Peak]))
            .Where(c => c.Events.Count >= HotspotMinEvents && c.Days >= HotspotMinDays);
    }

    private static List<Level> BuildLevels(List<VolumeNode> nodes, List<AbsorptionCluster> clusters, List<WeeklyPoc> weekly,
        decimal poc, PriceRange valueArea, decimal bin, decimal roundStep)
    {
        // Overlapping nodes and hotspots make one level.
        var ranges = nodes.Select(n => n.Range).Concat(clusters.Select(c => c.Range)).OrderBy(r => r.Low).ToList();
        var merged = new List<PriceRange>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && merged[^1].Overlaps(r))
                merged[^1] = new PriceRange(merged[^1].Low, Math.Max(merged[^1].High, r.High));
            else
                merged.Add(r);
        }

        return merged.Select(r =>
        {
            var marks = new List<string>();
            if (r.Contains(poc, bin)) marks.Add($"POC {poc:N0}");
            if (r.Contains(valueArea.High, bin)) marks.Add($"value area high {valueArea.High:N0}");
            if (r.Contains(valueArea.Low, bin)) marks.Add($"value area low {valueArea.Low:N0}");
            var round = Math.Ceiling((r.Low - bin) / roundStep) * roundStep;
            return new Level(r,
                nodes.Where(n => n.Range.Overlaps(r)).ToList(),
                clusters.Where(c => c.Range.Overlaps(r)).ToList(),
                weekly.Where(w => w.Naked && r.Contains(w.Price, bin)).ToList(),
                marks,
                round <= r.High + bin ? round : null);
        }).ToList();
    }

    public string Format(LevelScan scan, IReadOnlyList<Candle> candles)
    {
        var sb = new StringBuilder();
        var days = (scan.To - scan.From).TotalDays;
        sb.AppendLine($"Levels for {_options.Symbol}, Binance spot, {Local(scan.From):yyyy-MM-dd} to {Local(scan.To):yyyy-MM-dd} " +
                      $"({days:0.#} days of 1-minute candles, price bins of {scan.BinSize:N0})");
        sb.AppendLine($"Volume profile: POC {scan.Poc:N0}, value area {scan.ValueArea} (70% of the volume)");

        var after = candles.Where(c => c.OpenTime >= scan.To).ToList();
        var afterDays = after.Count == 0 ? 0 : (after[^1].OpenTime + OneMinute - scan.To).TotalDays;

        sb.AppendLine();
        sb.AppendLine("Levels, highest first. Score = how many kinds of factor agree (volume node, absorption, naked weekly POC, POC/value area, round number):");
        var pricePrinted = false;
        foreach (var level in scan.Levels.OrderByDescending(l => l.Range.Low))
        {
            if (!pricePrinted && level.Range.High < scan.LastPrice)
            {
                sb.AppendLine($"   ── price {scan.LastPrice:N0} at the end of the range ──");
                pricePrinted = true;
            }
            var role = level.Range.Low > scan.LastPrice ? "resistance" : level.Range.High < scan.LastPrice ? "support" : "price is here";
            sb.AppendLine($"  [{level.Score}] {level.Range,-17} {role,-13} {string.Join(" · ", Factors(level))}");
            if (after.Count > 0 && role != "price is here")
                sb.AppendLine($"        next: {NextMove(level.Range, after, role == "support", afterDays)}");
        }
        if (!pricePrinted)
            sb.AppendLine($"   ── price {scan.LastPrice:N0} at the end of the range ──");

        sb.AppendLine();
        sb.AppendLine("Thin areas (low volume: price tends to move through these quickly):");
        if (scan.ThinAreas.Count == 0)
            sb.AppendLine("  none");
        foreach (var area in scan.ThinAreas.OrderByDescending(a => a.Low))
            sb.AppendLine($"  {area}");

        sb.AppendLine();
        sb.AppendLine("Weekly POCs (most-traded price of each complete UTC week):");
        if (scan.WeeklyPocs.Count == 0)
            sb.AppendLine("  none (the range has no complete week)");
        foreach (var w in scan.WeeklyPocs)
            sb.AppendLine($"  week of {w.WeekStart:MM-dd}: {w.Price:N0}{(w.Naked ? "  naked (not traded since)" : "")}");

        sb.AppendLine();
        sb.AppendLine($"Absorption: {scan.Absorption.Count} signals ({scan.Absorption.Sum(e => e.Volume):N0} BTC). " +
                      $"Hotspots (absorbed per traded volume at least {HotspotMinIntensity:0.#}x the range's rate):");
        if (scan.Clusters.Count == 0)
            sb.AppendLine("  none");
        foreach (var c in scan.Clusters.OrderByDescending(c => c.Intensity))
            sb.AppendLine($"  {c.Range,-17} {Describe(c)}");

        return sb.ToString().TrimEnd();
    }

    private IEnumerable<string> Factors(Level level)
    {
        foreach (var n in level.Nodes)
            yield return $"volume node {n.Strength:0.0}x median at {n.Peak:N0}";
        foreach (var c in level.Clusters)
            yield return $"absorption hotspot {Describe(c)}";
        foreach (var w in level.NakedPocs)
            yield return $"naked POC of week {w.WeekStart:MM-dd} at {w.Price:N0}";
        foreach (var m in level.ProfileMarks)
            yield return m;
        if (level.RoundNumber is { } round)
            yield return $"round number {round:N0}";
    }

    private string Describe(AbsorptionCluster c)
    {
        var sides = new List<string>();
        if (c.SellerCount > 0)
            sides.Add($"sellers absorbed {c.SellerCount}× ({c.SellerVolume:N0} BTC)");
        if (c.BuyerCount > 0)
            sides.Add($"buyers absorbed {c.BuyerCount}× ({c.BuyerVolume:N0} BTC)");
        return $"{c.Intensity:0.0}x: {string.Join(", ", sides)} on {(c.Days == 1 ? "1 day" : $"{c.Days} days")}, last {Local(c.Last):ddd MM-dd HH:mm}";
    }

    /// <summary>
    /// What price did at the level after the range: the first touch, how far it moved away within 24 hours,
    /// how far it wicked through, and whether a 4-hour candle closed through the far side.
    /// </summary>
    private string NextMove(PriceRange level, List<Candle> after, bool support, double afterDays)
    {
        var touch = after.FindIndex(c => support ? c.Low <= level.High : c.High >= level.Low);
        if (touch < 0)
            return $"not reached in the next {afterDays:0.#} days";

        var touchTime = after[touch].OpenTime;
        var day = after.Skip(touch).TakeWhile(c => c.OpenTime < touchTime.AddDays(1)).ToList();
        var edge = support ? level.High : level.Low;
        var reaction = support ? (day.Max(c => c.High) - edge) / edge : (edge - day.Min(c => c.Low)) / edge;
        var through = support ? level.Low - day.Min(c => c.Low) : day.Max(c => c.High) - level.High;

        var broke = ZoneReport.Aggregate(after, FourHours, OneMinute)
            .FirstOrDefault(b => b.IsComplete && b.Start + FourHours > touchTime && (support ? b.Close < level.Low : b.Close > level.High));

        var text = $"touched {Local(touchTime):ddd MM-dd HH:mm}, then {(support ? "up" : "down")} {reaction:0.0%} within 24h";
        if (through > 0)
            text += $", wicked {through:N0} through";
        text += broke is not null
            ? $"; broke: 4h close {broke.Close:N0} {(support ? "below" : "above")} it at {Local(broke.Start + FourHours):ddd MM-dd HH:mm}"
            : "; no 4h close through it";
        return text;
    }

    /// <summary>The nearest of 1, 2, 5, 10, 20, 50... to the value.</summary>
    private static decimal NiceStep(decimal value)
    {
        var magnitude = (decimal)Math.Pow(10, Math.Floor(Math.Log10((double)value)));
        return new[] { 1m, 2m, 5m, 10m }.Select(m => m * magnitude).MinBy(step => Math.Abs(step - value));
    }

    private static decimal Median(decimal[] values)
    {
        if (values.Length == 0)
            return 0;
        var sorted = values.Order().ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    private DateTimeOffset Local(DateTimeOffset time) => TimeZoneInfo.ConvertTime(time, _timeZone);
}
