namespace ChartAnalyzer.Data;

/// <summary>
/// A stored 1-minute candle for an exchange whose own history lacks taker buy volume (Coinbase).
/// Built live from trades, so it's kept for analysis and tuning later. One row per exchange per minute.
/// </summary>
public sealed class ExchangeCandle
{
    public string Exchange { get; set; } = "";
    public DateTimeOffset OpenTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public decimal TakerBuyVolume { get; set; }
}
