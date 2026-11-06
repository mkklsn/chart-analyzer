namespace ChartAnalyzer;

/// <summary>Bound from the "Analyzer" section in appsettings.json.</summary>
public sealed class AnalyzerOptions
{
    public string BinanceBaseUrl { get; set; } = "https://api.binance.com/";
    public string Symbol { get; set; } = "BTCUSDT";

    public string BinancePerpBaseUrl { get; set; } = "https://fapi.binance.com/";

    /// <summary>
    /// MinAbsorbedVolume for the Binance perp, which trades roughly 8x Binance spot's BTC volume.
    /// 250 was the best of 100/170/250/400 in a backtest of Oct 5 - Nov 2 2026. At 400, seller absorption
    /// turned bearish (heavy perp selling tends to be a liquidation cascade that continues).
    /// </summary>
    public decimal PerpMinAbsorbedVolume { get; set; } = 250m;

    public string CoinbaseBaseUrl { get; set; } = "https://api.exchange.coinbase.com/";
    public string CoinbaseProduct { get; set; } = "BTC-USD";

    /// <summary>
    /// MinAbsorbedVolume for Coinbase, which trades roughly half of Binance's BTC volume.
    /// A starting point: tune it once a few weeks of Coinbase candles are stored.
    /// </summary>
    public decimal CoinbaseMinAbsorbedVolume { get; set; } = 9m;

    /// <summary>How many earlier candles define "usual".</summary>
    public int LookbackMinutes { get; set; } = 60;

    /// <summary>Selling (or buying) volume must be at least this many times the usual (median) amount.</summary>
    public decimal MinVolumeRatio { get; set; } = 3.0m;

    /// <summary>Range must be at most this many times the usual (median) range.</summary>
    public decimal MaxRangeRatio { get; set; } = 1.0m;

    /// <summary>
    /// The absorbed volume must also be at least this much (in BTC for BTCUSDT).
    /// Stops quiet periods, where a few BTC already looks like "3x usual", from triggering.
    /// </summary>
    public decimal MinAbsorbedVolume { get; set; } = 20m;

    /// <summary>Absorption is checked over the last 1, 2, ... up to this many minutes combined.</summary>
    public int MaxWindowMinutes { get; set; } = 3;

    /// <summary>After a signal, further signals on the same side are ignored for this many minutes.</summary>
    public int CooldownMinutes { get; set; } = 5;

    /// <summary>Hour of the day (local time, 0-23) when zone summaries are sent (daily for local zones, Mondays for macro).</summary>
    public int ZoneSummaryHour { get; set; } = 8;

    /// <summary>Time zone for displayed times, e.g. "Europe/London". Empty uses the machine's time zone.</summary>
    public string TimeZone { get; set; } = "";

    public TimeZoneInfo GetTimeZone()
    {
        if (string.IsNullOrEmpty(TimeZone))
            return TimeZoneInfo.Local;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
