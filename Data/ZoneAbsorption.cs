using ChartAnalyzer.Analysis;

namespace ChartAnalyzer.Data;

public enum AbsorptionOutcome
{
    /// <summary>Still inside the hold window, price hasn't closed past the level.</summary>
    Pending,

    /// <summary>Price didn't close past the level within the hold window: the absorbing side won.</summary>
    Held,

    /// <summary>Price closed past the level within the hold window: the absorbing side got run over.</summary>
    Failed,
}

/// <summary>
/// One absorption alert at one watched zone, and whether it held. Used for the per-zone scoreboard.
/// An alert touching two zones makes two events.
/// </summary>
public sealed class ZoneAbsorption
{
    public Guid Id { get; set; }

    /// <summary>The zone, by its prices (unique among watched zones).</summary>
    public decimal ZoneLow { get; set; }
    public decimal ZoneHigh { get; set; }

    public AbsorptionSide Side { get; set; }

    /// <summary>The 1-minute candle the alert was about (UTC open time).</summary>
    public DateTimeOffset Minute { get; set; }

    /// <summary>Binance spot close of that minute (or the alerting market's, if Binance had no data).</summary>
    public decimal Price { get; set; }

    /// <summary>
    /// The price the absorbing side must hold: the low of the absorption window for seller absorption,
    /// the high for buyer absorption. A Binance spot close past it means it failed.
    /// </summary>
    public decimal Level { get; set; }

    /// <summary>
    /// Absorbed volume (sell volume for seller absorption, buy volume for buyer absorption) on each market that showed it,
    /// including markets that confirmed it later; 0 for a market that didn't. Kept per market: the perp trades ~8x spot.
    /// </summary>
    public decimal BinanceVolume { get; set; }
    public decimal CoinbaseVolume { get; set; }
    public decimal PerpVolume { get; set; }

    /// <summary>Adds a market's absorbed volume (by its exchange name).</summary>
    public void AddVolume(string exchange, decimal volume)
    {
        switch (exchange)
        {
            case "Binance": BinanceVolume = volume; break;
            case "Coinbase": CoinbaseVolume = volume; break;
            case "Binance perp": PerpVolume = volume; break;
        }
    }

    /// <summary>Markets that showed it, including later confirmations, e.g. "Binance, Binance perp".</summary>
    public string Exchanges { get; set; } = "";

    /// <summary>Only the perp showed it: often liquidations rather than real buying or selling.</summary>
    public bool PerpsOnly { get; set; }

    public AbsorptionOutcome Outcome { get; set; }

    /// <summary>When it held or failed (the minute of the deciding candle).</summary>
    public DateTimeOffset? ResolvedMinute { get; set; }

    /// <summary>The close that decided it.</summary>
    public decimal? ResolvedClose { get; set; }

    public bool IsAt(WatchedZone zone) => ZoneLow == zone.Low && ZoneHigh == zone.High;

    public ZoneAbsorption Copy() => (ZoneAbsorption)MemberwiseClone();
}
