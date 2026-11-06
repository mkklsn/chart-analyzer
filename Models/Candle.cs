namespace ChartAnalyzer.Models;

/// <summary>One 1-minute candle.</summary>
/// <param name="TakerBuyVolume">Part of the volume where the impatient side (taker) was a buyer.</param>
public sealed record Candle(
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume,
    decimal TakerBuyVolume)
{
    /// <summary>Part of the volume where the impatient side (taker) was a seller.</summary>
    public decimal SellVolume => Volume - TakerBuyVolume;

    /// <summary>How far the price moved within the minute.</summary>
    public decimal Range => High - Low;
}
