using System.Text;
using ChartAnalyzer.Data;
using ChartAnalyzer.Models;

namespace ChartAnalyzer.Analysis;

/// <summary>
/// Scores the absorption at a zone: how many of each side, which held, and who's winning.
///
/// An absorption "held" when no Binance spot 1-minute candle closed past its level within the hold window
/// (below the window low for seller absorption, above the window high for buyer absorption).
///
/// Buyers score when seller absorption holds (patient buyers soaked up the selling and kept the price)
/// or when buyer absorption fails (buyers ran over the patient sellers). Sellers score the other way round.
/// </summary>
public static class AbsorptionScore
{
    private const int LatestShown = 8;

    /// <summary>
    /// Decides a pending event from the candles after it. Returns true if it was decided.
    /// The candles must start no later than the minute after the event.
    /// </summary>
    public static bool Resolve(ZoneAbsorption e, IReadOnlyList<Candle> candles, TimeSpan hold)
    {
        if (e.Outcome != AbsorptionOutcome.Pending)
            return false;

        var end = e.Minute + hold;
        Candle? last = null;
        foreach (var c in candles)
        {
            if (c.OpenTime <= e.Minute || c.OpenTime > end)
                continue;
            last = c;
            var failed = e.Side == AbsorptionSide.Seller ? c.Close < e.Level : c.Close > e.Level;
            if (failed)
            {
                e.Outcome = AbsorptionOutcome.Failed;
                e.ResolvedMinute = c.OpenTime;
                e.ResolvedClose = c.Close;
                return true;
            }
        }

        if (last is null || last.OpenTime < end)
            return false;
        e.Outcome = AbsorptionOutcome.Held;
        e.ResolvedMinute = last.OpenTime;
        e.ResolvedClose = last.Close;
        return true;
    }

    /// <summary>
    /// The start of the current visit to the zone: the earliest candle in the zone with no gap longer than
    /// <paramref name="gap"/> between it and now. Null when price hasn't been in the zone within the gap.
    /// </summary>
    public static DateTimeOffset? VisitStart(IReadOnlyList<Candle> candles, decimal low, decimal high, TimeSpan gap, DateTimeOffset now)
    {
        DateTimeOffset? start = null;
        var previous = now;
        for (var i = candles.Count - 1; i >= 0; i--)
        {
            var c = candles[i];
            if (c.Low > high || c.High < low)
                continue;
            if (previous - c.OpenTime > gap)
                break;
            start = previous = c.OpenTime;
        }
        return start;
    }

    public sealed record Tally(int Count, int Held, int Failed, int Pending, int PerpsOnly, decimal SpotVolume)
    {
        public static Tally Of(IEnumerable<ZoneAbsorption> events)
        {
            var list = events.ToList();
            return new Tally(list.Count,
                list.Count(e => e.Outcome == AbsorptionOutcome.Held),
                list.Count(e => e.Outcome == AbsorptionOutcome.Failed),
                list.Count(e => e.Outcome == AbsorptionOutcome.Pending),
                list.Count(e => e.PerpsOnly),
                list.Sum(e => e.SpotVolume));
        }
    }

    /// <summary>Points for buyers and sellers (see the class summary).</summary>
    public static (int Buyers, int Sellers) Points(IEnumerable<ZoneAbsorption> events)
    {
        int buyers = 0, sellers = 0;
        foreach (var e in events)
        {
            if (e.Outcome == AbsorptionOutcome.Pending)
                continue;
            var bullish = (e.Side == AbsorptionSide.Seller) == (e.Outcome == AbsorptionOutcome.Held);
            if (bullish)
                buyers++;
            else
                sellers++;
        }
        return (buyers, sellers);
    }

    /// <summary>The scoreboard for Telegram.</summary>
    /// <param name="events">The zone's events since <paramref name="since"/>, oldest first.</param>
    /// <param name="visitStart">Start of the current visit to the zone, if price is there now.</param>
    public static string Format(WatchedZone zone, IReadOnlyList<ZoneAbsorption> events, DateTimeOffset since,
        DateTimeOffset? visitStart, TimeSpan hold, TimeZoneInfo timeZone)
    {
        string Local(DateTimeOffset t, string format) => TimeZoneInfo.ConvertTime(t, timeZone).ToString(format);

        var sb = new StringBuilder();
        sb.AppendLine($"📊 Absorption at {zone.Name}, since {Local(since, "ddd MM-dd HH:mm")}:");
        if (events.Count == 0)
        {
            sb.Append("none yet");
            return sb.ToString();
        }

        foreach (var side in new[] { AbsorptionSide.Seller, AbsorptionSide.Buyer })
            sb.AppendLine(SideLine(side, Tally.Of(events.Where(e => e.Side == side))));

        var visit = visitStart is { } vs ? events.Where(e => e.Minute >= vs).ToList() : null;
        if (visitStart is { } start)
        {
            var seller = visit!.Count(e => e.Side == AbsorptionSide.Seller);
            var buyer = visit!.Count(e => e.Side == AbsorptionSide.Buyer);
            sb.AppendLine($"This visit (since {Local(start, "ddd HH:mm")}): seller {seller} · buyer {buyer}");
        }

        sb.AppendLine("Latest: " + string.Join(" ", events.TakeLast(LatestShown).Select(Mark)) + " (oldest → newest)");

        // Judge the current visit when it has decided absorption; otherwise everything since the start.
        var visitDecided = visit is not null && visit.Any(e => e.Outcome != AbsorptionOutcome.Pending);
        var (buyers, sellers) = Points(visitDecided ? visit! : events);
        var scope = visitDecided ? "this visit" : "overall";
        sb.Append(buyers == 0 && sellers == 0
            ? $"→ Nothing decided yet (an absorption holds after {hold.TotalMinutes:0} min)"
            : buyers > sellers ? $"→ Buyers winning {scope}, {buyers}–{sellers}"
            : sellers > buyers ? $"→ Sellers winning {scope}, {sellers}–{buyers}"
            : $"→ Even {scope}, {buyers}–{sellers}");
        return sb.ToString();
    }

    // "Seller (bullish) 5: 3 held · 1 failed · 1 pending · 412 BTC on Binance spot · 1 perps only"
    private static string SideLine(AbsorptionSide side, Tally t)
    {
        var name = side == AbsorptionSide.Seller ? "Seller (bullish)" : "Buyer (bearish)";
        if (t.Count == 0)
            return $"{name}: 0";
        var parts = new List<string>();
        if (t.Held > 0) parts.Add($"{t.Held} held");
        if (t.Failed > 0) parts.Add($"{t.Failed} failed");
        if (t.Pending > 0) parts.Add($"{t.Pending} pending");
        if (t.SpotVolume > 0) parts.Add($"{t.SpotVolume:N0} BTC on Binance spot");
        if (t.PerpsOnly > 0) parts.Add($"{t.PerpsOnly} perps only");
        return $"{name} {t.Count}: {string.Join(" · ", parts)}";
    }

    // S✓ = seller absorption held, B✗ = buyer absorption failed, S… = still pending.
    private static string Mark(ZoneAbsorption e) =>
        (e.Side == AbsorptionSide.Seller ? "S" : "B") +
        e.Outcome switch { AbsorptionOutcome.Held => "✓", AbsorptionOutcome.Failed => "✗", _ => "…" };
}
