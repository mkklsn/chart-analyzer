using System.Text;
using ChartAnalyzer.Models;

namespace ChartAnalyzer.Analysis;

public sealed record SignalResult(AbsorptionSide Side, decimal Close, decimal? Level, SignalOutcome Outcome);

/// <summary>
/// How often the price moved up/down after a random minute. Signals are only useful
/// if they beat this: in a rising market "higher after 15 min" happens often anyway.
/// </summary>
public sealed record Baseline(IReadOnlyDictionary<int, double> HigherShare, IReadOnlyDictionary<int, double> LowerShare)
{
    /// <summary>Uses every minute for which <paramref name="include"/> is true as a starting point.</summary>
    public static Baseline Compute(IReadOnlyList<Candle> candles, Func<Candle, bool> include)
    {
        var higher = new Dictionary<int, double>();
        var lower = new Dictionary<int, double>();
        foreach (var minutes in SignalOutcome.Horizons)
        {
            var pairs = Enumerable.Range(0, Math.Max(0, candles.Count - minutes))
                .Where(i => include(candles[i]))
                .Where(i => candles[i + minutes].OpenTime == candles[i].OpenTime.AddMinutes(minutes))
                .Select(i => candles[i + minutes].Close - candles[i].Close)
                .ToList();
            higher[minutes] = pairs.Count == 0 ? 0 : pairs.Count(d => d > 0) / (double)pairs.Count;
            lower[minutes] = pairs.Count == 0 ? 0 : pairs.Count(d => d < 0) / (double)pairs.Count;
        }
        return new Baseline(higher, lower);
    }
}

/// <summary>Summarizes how signals played out. Plain text, used by both the console and Telegram.</summary>
public static class SignalStats
{
    public static string Format(IReadOnlyCollection<SignalResult> results, Baseline? baseline = null)
    {
        var sb = new StringBuilder();
        foreach (var side in new[] { AbsorptionSide.Seller, AbsorptionSide.Buyer })
        {
            var group = results.Where(r => r.Side == side).ToList();
            if (sb.Length > 0)
                sb.AppendLine();
            FormatSide(sb, side, group, baseline);
        }
        return sb.ToString().TrimEnd();
    }

    private static void FormatSide(StringBuilder sb, AbsorptionSide side, List<SignalResult> group, Baseline? baseline)
    {
        var bullish = side == AbsorptionSide.Seller;
        sb.AppendLine(bullish
            ? $"🟢 Seller absorption (bullish): {group.Count} signals"
            : $"🔴 Buyer absorption (bearish): {group.Count} signals");
        if (group.Count == 0)
            return;

        var direction = bullish ? "higher" : "lower";
        sb.AppendLine($"Price {direction} after " + string.Join(" | ", SignalOutcome.Horizons.Select(m =>
        {
            var changes = Changes(group, m);
            var wins = changes.Count(d => bullish ? d > 0 : d < 0);
            return changes.Count == 0 ? $"{m}m: -" : $"{m}m: {wins}/{changes.Count} ({Percent(wins, changes.Count)})";
        })));

        if (baseline is not null)
        {
            var shares = bullish ? baseline.HigherShare : baseline.LowerShare;
            sb.AppendLine($"Random minute, {direction}: " + string.Join(" | ", SignalOutcome.Horizons.Select(m => $"{m}m {shares[m]:P0}")));
        }

        sb.AppendLine("Avg change: " + string.Join(" | ", SignalOutcome.Horizons.Select(m =>
        {
            var changes = Changes(group, m);
            return changes.Count == 0 ? $"{m}m -" : $"{m}m {Signed(changes.Average())}";
        })));

        var withRange = group.Where(r => r.Outcome.High30m is not null && r.Outcome.Low30m is not null).ToList();
        if (withRange.Count > 0)
        {
            var maxUp = withRange.Average(r => r.Outcome.High30m!.Value - r.Close);
            var maxDown = withRange.Average(r => r.Outcome.Low30m!.Value - r.Close);
            sb.AppendLine($"Avg max up / down within 30m: {Signed(maxUp)} / {Signed(maxDown)}");
        }
    }

    // Price change after the given minutes, for signals where it's known.
    private static List<decimal> Changes(List<SignalResult> group, int minutes) =>
        group.Where(r => r.Outcome.CloseAfter(minutes) is not null)
            .Select(r => r.Outcome.CloseAfter(minutes)!.Value - r.Close)
            .ToList();

    private static string Percent(int part, int total) => $"{(double)part / total:P0}";

    private static string Signed(decimal value) => value >= 0 ? $"+{value:N0}" : $"{value:N0}";
}
