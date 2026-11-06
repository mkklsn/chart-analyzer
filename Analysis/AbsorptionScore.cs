using System.Text;
using ChartAnalyzer.Data;
using ChartAnalyzer.Models;

namespace ChartAnalyzer.Analysis;

/// <summary>
/// Scores the absorption at a zone: how much of each side, which held, and who's winning.
///
/// An absorption "held" when no Binance spot 1-minute candle closed clearly past its level within the hold window:
/// more than <c>buffer</c> (a fraction of the level) below the window low for seller absorption, or above the window
/// high for buyer absorption. The buffer keeps normal wiggles of a few dollars from counting as a break.
///
/// Buyers score when seller absorption holds (patient buyers soaked up the selling and kept the price)
/// or when buyer absorption fails (buyers ran over the patient sellers). Sellers score the other way round.
/// Points are weighted by the volume absorbed on Binance spot; counts are shown alongside.
/// </summary>
public static class AbsorptionScore
{
    private const int LatestShown = 8;

    // Effort vs result over a visit: net aggression of at least this share of the volume, with price not moving its way.
    private const decimal EffortShare = 0.10m;

    /// <summary>
    /// Decides a pending event from the candles after it. Returns true if it was decided.
    /// The candles must start no later than the minute after the event.
    /// </summary>
    /// <param name="buffer">How far past the level (as a fraction of it) a close must be to count as a break.</param>
    public static bool Resolve(ZoneAbsorption e, IReadOnlyList<Candle> candles, TimeSpan hold, decimal buffer)
    {
        if (e.Outcome != AbsorptionOutcome.Pending)
            return false;

        var end = e.Minute + hold;
        var limit = e.Side == AbsorptionSide.Seller ? e.Level * (1 - buffer) : e.Level * (1 + buffer);
        Candle? last = null;
        foreach (var c in candles)
        {
            if (c.OpenTime <= e.Minute || c.OpenTime > end)
                continue;
            last = c;
            var failed = e.Side == AbsorptionSide.Seller ? c.Close < limit : c.Close > limit;
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

    /// <summary>Aggression on one market over the visit.</summary>
    public sealed record MarketFlow(string Market, decimal NetBuying, decimal Volume);

    /// <summary>Effort vs result over the visit: net buying per market (Binance spot first) and the Binance price move.</summary>
    public sealed record VisitEffort(IReadOnlyList<MarketFlow> Markets, decimal Open, decimal Close)
    {
        public static MarketFlow Flow(string market, IEnumerable<Candle> candles)
        {
            var list = candles.ToList();
            return new MarketFlow(market, list.Sum(c => c.TakerBuyVolume - c.SellVolume), list.Sum(c => c.Volume));
        }
    }

    public sealed record Tally(int Count, int Held, int Failed, int Pending, int PerpsOnly, decimal Binance, decimal Coinbase, decimal Perp)
    {
        public static Tally Of(IEnumerable<ZoneAbsorption> events)
        {
            var list = events.ToList();
            return new Tally(list.Count,
                list.Count(e => e.Outcome == AbsorptionOutcome.Held),
                list.Count(e => e.Outcome == AbsorptionOutcome.Failed),
                list.Count(e => e.Outcome == AbsorptionOutcome.Pending),
                list.Count(e => e.PerpsOnly),
                list.Sum(e => e.BinanceVolume),
                list.Sum(e => e.CoinbaseVolume),
                list.Sum(e => e.PerpVolume));
        }
    }

    /// <summary>Points for buyers and sellers (see the class summary): counts, and Binance spot BTC.</summary>
    public static (int Buyers, int Sellers, decimal BuyersVolume, decimal SellersVolume) Points(IEnumerable<ZoneAbsorption> events)
    {
        int buyers = 0, sellers = 0;
        decimal buyersVolume = 0, sellersVolume = 0;
        foreach (var e in events)
        {
            if (e.Outcome == AbsorptionOutcome.Pending)
                continue;
            var bullish = (e.Side == AbsorptionSide.Seller) == (e.Outcome == AbsorptionOutcome.Held);
            if (bullish)
            {
                buyers++;
                buyersVolume += e.BinanceVolume;
            }
            else
            {
                sellers++;
                sellersVolume += e.BinanceVolume;
            }
        }
        return (buyers, sellers, buyersVolume, sellersVolume);
    }

    /// <summary>The scoreboard for Telegram.</summary>
    /// <param name="events">The zone's events since <paramref name="since"/>, oldest first.</param>
    /// <param name="visitStart">Start of the current visit to the zone, if price is there now.</param>
    /// <param name="effort">Net buying and price move over the current visit, if known.</param>
    public static string Format(WatchedZone zone, IReadOnlyList<ZoneAbsorption> events, DateTimeOffset since,
        DateTimeOffset? visitStart, VisitEffort? effort, TimeSpan hold, TimeZoneInfo timeZone)
    {
        string Local(DateTimeOffset t, string format) => TimeZoneInfo.ConvertTime(t, timeZone).ToString(format);

        var sb = new StringBuilder();
        sb.AppendLine($"📊 Absorption at {zone.Name}, since {Local(since, "ddd MM-dd HH:mm")}:");
        if (events.Count == 0)
            sb.AppendLine("none yet");
        else
        {
            foreach (var side in new[] { AbsorptionSide.Seller, AbsorptionSide.Buyer })
                AppendSide(sb, side, Tally.Of(events.Where(e => e.Side == side)));
        }

        var visit = visitStart is { } vs ? events.Where(e => e.Minute >= vs).ToList() : null;
        if (visitStart is { } start)
        {
            var seller = visit!.Where(e => e.Side == AbsorptionSide.Seller).ToList();
            var buyer = visit!.Where(e => e.Side == AbsorptionSide.Buyer).ToList();
            sb.AppendLine($"This visit (since {Local(start, "ddd HH:mm")}): seller {seller.Count} ({seller.Sum(e => e.BinanceVolume):N0} BTC) · " +
                          $"buyer {buyer.Count} ({buyer.Sum(e => e.BinanceVolume):N0} BTC)");
            if (effort is not null)
                sb.AppendLine(FormatEffort(effort));
        }

        if (events.Count == 0)
            return sb.ToString().TrimEnd();

        sb.AppendLine("Latest: " + string.Join(" ", events.TakeLast(LatestShown).Select(Mark)) + " (oldest → newest)");

        // Judge the current visit when it has decided absorption; otherwise everything since the start.
        var visitDecided = visit is not null && visit.Any(e => e.Outcome != AbsorptionOutcome.Pending);
        var (buyers, sellers, buyersVolume, sellersVolume) = Points(visitDecided ? visit! : events);
        var scope = visitDecided ? "this visit" : "overall";
        sb.Append(Verdict(buyers, sellers, buyersVolume, sellersVolume, scope, hold));
        return sb.ToString();
    }

    // By volume when there's Binance spot volume behind the decided absorption, otherwise by count.
    private static string Verdict(int buyers, int sellers, decimal buyersVolume, decimal sellersVolume, string scope, TimeSpan hold)
    {
        if (buyers == 0 && sellers == 0)
            return $"→ Nothing decided yet (an absorption holds after {hold.TotalMinutes:0} min)";

        var count = $"{buyers}–{sellers} by count";
        if (buyersVolume + sellersVolume == 0)
        {
            return buyers > sellers ? $"→ Buyers winning {scope}, {buyers}–{sellers} by count (no Binance spot volume)"
                : sellers > buyers ? $"→ Sellers winning {scope}, {sellers}–{buyers} by count (no Binance spot volume)"
                : $"→ Even {scope}, {count}";
        }
        return buyersVolume > sellersVolume
            ? $"→ Buyers winning {scope}: {buyersVolume:N0} vs {sellersVolume:N0} BTC (Binance spot), {buyers}–{sellers} by count"
            : sellersVolume > buyersVolume
                ? $"→ Sellers winning {scope}: {sellersVolume:N0} vs {buyersVolume:N0} BTC (Binance spot), {sellers}–{buyers} by count"
                : $"→ Even {scope}: {buyersVolume:N0} BTC each (Binance spot), {count}";
    }

    // "Seller absorption (bullish) 5: 3 held · 1 failed · 1 pending · 1 perps only"
    // "  selling absorbed: Binance 412 · Coinbase 60 · perp 2,900 BTC"
    private static void AppendSide(StringBuilder sb, AbsorptionSide side, Tally t)
    {
        var seller = side == AbsorptionSide.Seller;
        var name = seller ? "Seller absorption (bullish)" : "Buyer absorption (bearish)";
        if (t.Count == 0)
        {
            sb.AppendLine($"{name}: 0");
            return;
        }
        var parts = new List<string>();
        if (t.Held > 0) parts.Add($"{t.Held} held");
        if (t.Failed > 0) parts.Add($"{t.Failed} failed");
        if (t.Pending > 0) parts.Add($"{t.Pending} pending");
        if (t.PerpsOnly > 0) parts.Add($"{t.PerpsOnly} perps only");
        sb.AppendLine($"{name} {t.Count}: {string.Join(" · ", parts)}");
        sb.AppendLine($"  {(seller ? "selling" : "buying")} absorbed: Binance {t.Binance:N0} · Coinbase {t.Coinbase:N0} · perp {t.Perp:N0} BTC");
    }

    // "Net buying this visit: Binance -3,400 (-12% of volume) · Coinbase -200 · Binance perp +1,100 BTC; price +0.1% (75,900 → 75,980).
    //  Lots of selling, price not down: patient buyers absorbing"
    private static string FormatEffort(VisitEffort e)
    {
        var flows = string.Join(" · ", e.Markets.Select((m, i) =>
            $"{m.Market} {m.NetBuying:+#,0;-#,0;0}" + (i == 0 && m.Volume > 0 ? $" ({m.NetBuying / m.Volume:+0%;-0%} of volume)" : "")));
        var change = e.Open == 0 ? 0 : (e.Close - e.Open) / e.Open;
        var text = $"Net buying this visit: {flows} BTC; price {change:+0.0%;-0.0%;0.0%} ({e.Open:N0} → {e.Close:N0})";

        var main = e.Markets.FirstOrDefault();
        var share = main is { Volume: > 0 } ? main.NetBuying / main.Volume : 0;
        return share >= EffortShare && change <= 0 ? text + ". Lots of buying, price not up: patient sellers absorbing"
            : share <= -EffortShare && change >= 0 ? text + ". Lots of selling, price not down: patient buyers absorbing"
            : text;
    }

    // S✓ = seller absorption held, B✗ = buyer absorption failed, S… = still pending.
    private static string Mark(ZoneAbsorption e) =>
        (e.Side == AbsorptionSide.Seller ? "S" : "B") +
        e.Outcome switch { AbsorptionOutcome.Held => "✓", AbsorptionOutcome.Failed => "✗", _ => "…" };
}
