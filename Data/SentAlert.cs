using ChartAnalyzer.Analysis;

namespace ChartAnalyzer.Data;

public enum AlertKind
{
    /// <summary>A new absorption at a watched zone.</summary>
    Absorption,

    /// <summary>Another market showing the same absorption shortly after an <see cref="Absorption"/> alert.</summary>
    Confirmation,

    /// <summary>New signs found by the hourly (or daily, for macro) zone check.</summary>
    Zone,
}

/// <summary>
/// An alert sent to Telegram, with the numbers behind it, so alerts can be reviewed against what price did next.
/// One row per message.
/// </summary>
public sealed class SentAlert
{
    public int Id { get; set; }

    public DateTimeOffset SentAt { get; set; }

    public AlertKind Kind { get; set; }

    /// <summary>Seller (bullish) or buyer (bearish) absorption; null for zone alerts.</summary>
    public AbsorptionSide? Side { get; set; }

    /// <summary>The 1-minute candle the alert is about (UTC open time); null for zone alerts.</summary>
    public DateTimeOffset? Minute { get; set; }

    /// <summary>For a confirmation: the minute of the alert it confirms (same <see cref="Side"/>).</summary>
    public DateTimeOffset? ConfirmsMinute { get; set; }

    /// <summary>Close of that minute on the first exchange that showed it.</summary>
    public decimal? Price { get; set; }

    /// <summary>Markets showing it, e.g. "Binance, Binance perp". For a confirmation, the confirming ones.</summary>
    public string? Exchanges { get; set; }

    /// <summary>True when spot and perps both show it; false when only one kind does.</summary>
    public bool? SpotAndPerps { get; set; }

    /// <summary>The watched zones involved, e.g. "support 77,000–77,400".</summary>
    public string? Zones { get; set; }

    /// <summary>Coinbase (USD) close minus Binance (USDT) close for <see cref="Minute"/>.</summary>
    public decimal? CoinbaseGap { get; set; }

    /// <summary>Median of that gap over the hour before, its normal level to compare against.</summary>
    public decimal? CoinbaseGapUsual { get; set; }

    /// <summary>The message exactly as sent.</summary>
    public string Text { get; set; } = "";

    /// <summary>JSON with the numbers per exchange (volumes, ratios, range, window) or the zone signs.</summary>
    public string? Details { get; set; }

    /// <summary>False if Telegram wasn't configured or the send failed.</summary>
    public bool Delivered { get; set; }
}
