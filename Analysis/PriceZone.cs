using System.Globalization;

namespace ChartAnalyzer.Analysis;

public enum ZoneRole { Any, Support, Resistance }

/// <summary>Local zones are judged on hourly/4-hour candles over days; macro zones on daily candles over months.</summary>
public enum ZoneProfile { Local, Macro }

public sealed record PriceZone(decimal Low, decimal High, ZoneRole Role, ZoneProfile Profile = ZoneProfile.Local)
{
    public decimal Height => High - Low;

    /// <summary>Parses "80700-81600" (either order).</summary>
    public static PriceZone Parse(string text, ZoneRole role, ZoneProfile profile = ZoneProfile.Local)
    {
        var parts = text.Replace(",", "").Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !decimal.TryParse(parts[0], CultureInfo.InvariantCulture, out var a)
                              || !decimal.TryParse(parts[1], CultureInfo.InvariantCulture, out var b))
            throw new FormatException($"Invalid zone '{text}'. Use e.g. --support 80700-81600");
        return new PriceZone(Math.Min(a, b), Math.Max(a, b), role, profile);
    }
}

/// <summary>How a zone profile is analyzed.</summary>
/// <param name="CandleInterval">Candles the analysis reads: 1 minute (local) or 1 hour (macro).</param>
/// <param name="ClimaxPeriod">Candle size a climax is judged on.</param>
/// <param name="ClimaxVolumeLookback">Climax volume is compared with the median of this many earlier climax candles.</param>
/// <param name="ClimaxExtremeLookback">A climax must make the lowest low (highest high) of this many earlier climax candles.</param>
/// <param name="BeyondPeriod">Candle size whose close past the zone triggers a warning.</param>
/// <param name="EffortDays">Days in the effort-vs-result window.</param>
/// <param name="TestGap">Price must stay away from the edge this long before the next visit counts as a new test.</param>
/// <param name="MaxDays">The analysis covers at most this many days.</param>
/// <param name="SummaryDays">Days shown in the Telegram summary.</param>
/// <param name="DefaultDays">Days analyzed when no start date is given.</param>
/// <param name="DetectAbsorption">Count minute-level absorption (only possible with 1-minute candles).</param>
public sealed record ZoneSettings(
    TimeSpan CandleInterval,
    TimeSpan ClimaxPeriod,
    int ClimaxVolumeLookback,
    int ClimaxExtremeLookback,
    TimeSpan BeyondPeriod,
    int EffortDays,
    TimeSpan TestGap,
    int MaxDays,
    int SummaryDays,
    int DefaultDays,
    bool DetectAbsorption)
{
    public static readonly ZoneSettings Local = new(
        CandleInterval: TimeSpan.FromMinutes(1),
        ClimaxPeriod: TimeSpan.FromHours(1),
        ClimaxVolumeLookback: 48,
        ClimaxExtremeLookback: 24,
        BeyondPeriod: TimeSpan.FromHours(4),
        EffortDays: 3,
        TestGap: TimeSpan.FromHours(1),
        MaxDays: 30,
        SummaryDays: 3,
        DefaultDays: 3,
        DetectAbsorption: true);

    public static readonly ZoneSettings Macro = new(
        CandleInterval: TimeSpan.FromHours(1),
        ClimaxPeriod: TimeSpan.FromDays(1),
        ClimaxVolumeLookback: 30,
        ClimaxExtremeLookback: 30,
        BeyondPeriod: TimeSpan.FromDays(1),
        EffortDays: 14,
        TestGap: TimeSpan.FromDays(1),
        MaxDays: 180,
        SummaryDays: 7,
        DefaultDays: 60,
        DetectAbsorption: false);

    public static ZoneSettings For(ZoneProfile profile) => profile == ZoneProfile.Macro ? Macro : Local;

    /// <summary>How much data is needed before the start of the analysis (for the climax comparison).</summary>
    public TimeSpan History => ClimaxPeriod * (ClimaxVolumeLookback + 1);
}
