using ChartAnalyzer.Models;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Analysis;

public enum AbsorptionSide
{
    /// <summary>Heavy selling soaked up by patient buyers. Usually seen at support; bullish.</summary>
    Seller,

    /// <summary>Heavy buying soaked up by patient sellers. Usually seen at resistance; bearish.</summary>
    Buyer,
}

/// <summary>The result of checking the window of 1..N minutes that ends with <see cref="Candle"/>.</summary>
/// <param name="WindowMinutes">How many minutes were combined (1 = just this candle).</param>
/// <param name="SellVolume">Selling volume over the window.</param>
/// <param name="BuyVolume">Buying volume over the window.</param>
/// <param name="Low">Lowest price over the window.</param>
/// <param name="High">Highest price over the window.</param>
/// <param name="ReferencePrice">Average close of the hour before the window: where the price came from.</param>
/// <param name="Side">Which side was absorbed, or null when there's no absorption.</param>
public sealed record AbsorptionCheck(
    Candle Candle,
    int WindowMinutes,
    decimal SellVolume,
    decimal BuyVolume,
    decimal Low,
    decimal High,
    decimal SellRatio,
    decimal BuyRatio,
    decimal RangeRatio,
    decimal ReferencePrice,
    AbsorptionSide? Side)
{
    public bool IsAbsorption => Side is not null;

    public decimal Range => High - Low;

    public string Label => Side switch
    {
        AbsorptionSide.Seller => "SELLER ABSORPTION (bullish)",
        AbsorptionSide.Buyer => "BUYER ABSORPTION (bearish)",
        _ => "",
    };

    public string Describe(TimeZoneInfo timeZone) =>
        $"{TimeZoneInfo.ConvertTime(Candle.OpenTime, timeZone):yyyy-MM-dd HH:mm} close {Candle.Close:0.00} | " +
        (WindowMinutes > 1 ? $"last {WindowMinutes} min | " : "") +
        $"sell {SellVolume:0.00} ({SellRatio:0.0}x usual) | buy {BuyVolume:0.00} ({BuyRatio:0.0}x usual) | " +
        $"range {Range:0.00} ({RangeRatio:0.00}x usual)";
}

/// <summary>
/// Flags absorption: selling (or buying) volume much higher than usual while the price range stays small.
/// Looks at the last 1, 2, ... MaxWindowMinutes minutes, since absorption often plays out over a few candles.
/// "Usual" is the median of the previous hour, so one extreme candle doesn't distort it.
/// </summary>
public sealed class AbsorptionDetector(IOptions<AnalyzerOptions> options)
{
    private readonly AnalyzerOptions _options = options.Value;

    /// <summary>
    /// Checks the windows ending at candles[index]. Returns the first window that shows absorption,
    /// otherwise the 1-minute check. Null when there isn't enough history yet.
    /// </summary>
    /// <param name="minAbsorbedVolume">Overrides MinAbsorbedVolume, for exchanges with a different volume level.</param>
    public AbsorptionCheck? Check(IReadOnlyList<Candle> candles, int index, decimal? minAbsorbedVolume = null)
    {
        if (index < _options.LookbackMinutes + 2 * _options.MaxWindowMinutes)
            return null;

        AbsorptionCheck? oneMinute = null;
        for (var minutes = 1; minutes <= _options.MaxWindowMinutes; minutes++)
        {
            var check = CheckWindow(candles, index, minutes, minAbsorbedVolume ?? _options.MinAbsorbedVolume);
            if (check is null)
                continue;
            if (check.IsAbsorption)
                return check;
            oneMinute ??= check;
        }
        return oneMinute;
    }

    private AbsorptionCheck? CheckWindow(IReadOnlyList<Candle> candles, int end, int minutes, decimal minAbsorbed)
    {
        var lookback = _options.LookbackMinutes;
        var current = Window.Of(candles, end, minutes);

        // Same-sized windows ending in the hour before this one.
        var firstEnd = end - minutes - lookback + 1;
        var previous = Enumerable.Range(firstEnd, lookback).Select(e => Window.Of(candles, e, minutes)).ToList();
        var usualSell = Median(previous.Select(w => w.Sell));
        var usualBuy = Median(previous.Select(w => w.Buy));
        var usualRange = Median(previous.Select(w => w.High - w.Low));
        if (usualSell == 0 || usualBuy == 0 || usualRange == 0)
            return null;

        var sellRatio = current.Sell / usualSell;
        var buyRatio = current.Buy / usualBuy;
        var rangeRatio = (current.High - current.Low) / usualRange;

        AbsorptionSide? side = null;
        if (rangeRatio <= _options.MaxRangeRatio)
        {
            // If both sides were heavy, the stronger one wins.
            if (sellRatio >= _options.MinVolumeRatio && current.Sell >= minAbsorbed && sellRatio >= buyRatio)
                side = AbsorptionSide.Seller;
            else if (buyRatio >= _options.MinVolumeRatio && current.Buy >= minAbsorbed)
                side = AbsorptionSide.Buyer;
        }

        var referencePrice = Enumerable.Range(firstEnd, lookback).Average(i => candles[i].Close);
        return new AbsorptionCheck(candles[end], minutes, current.Sell, current.Buy, current.Low, current.High,
            sellRatio, buyRatio, rangeRatio, referencePrice, side);
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    // Combined volume and price range of the candles ending at 'end'.
    private readonly record struct Window(decimal Sell, decimal Buy, decimal Low, decimal High)
    {
        public static Window Of(IReadOnlyList<Candle> candles, int end, int minutes)
        {
            decimal sell = 0, buy = 0, low = decimal.MaxValue, high = decimal.MinValue;
            for (var i = end - minutes + 1; i <= end; i++)
            {
                var c = candles[i];
                sell += c.SellVolume;
                buy += c.TakerBuyVolume;
                low = Math.Min(low, c.Low);
                high = Math.Max(high, c.High);
            }
            return new Window(sell, buy, low, high);
        }
    }
}

/// <summary>
/// Suppresses repeats: one absorption often spans several candles, and the multi-minute
/// windows would otherwise flag each of them.
/// </summary>
public sealed class SignalCooldown(int minutes)
{
    private readonly Dictionary<AbsorptionSide, DateTimeOffset> _last = [];

    /// <summary>True if this is an absorption signal that isn't a repeat of a recent one on the same side.</summary>
    public bool IsNewSignal(AbsorptionCheck check)
    {
        if (check.Side is not { } side)
            return false;
        if (_last.TryGetValue(side, out var last) && check.Candle.OpenTime < last.AddMinutes(minutes))
            return false;
        _last[side] = check.Candle.OpenTime;
        return true;
    }
}
