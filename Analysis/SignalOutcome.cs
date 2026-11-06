using ChartAnalyzer.Models;

namespace ChartAnalyzer.Analysis;

/// <summary>
/// What the price did after a signal: the close 5, 15 and 30 minutes later,
/// and the highest/lowest price within those 30 minutes. Null where candles were missing.
/// </summary>
public sealed record SignalOutcome(decimal? Close5m, decimal? Close15m, decimal? Close30m, decimal? High30m, decimal? Low30m)
{
    public const int WindowMinutes = 30;
    public static readonly int[] Horizons = [5, 15, 30];

    public bool IsEmpty => Close5m is null && Close15m is null && Close30m is null;

    public decimal? CloseAfter(int minutes) => minutes switch
    {
        5 => Close5m,
        15 => Close15m,
        30 => Close30m,
        _ => throw new ArgumentOutOfRangeException(nameof(minutes)),
    };

    /// <summary>Computes the outcome of candles[signalIndex] from the candles that follow it.</summary>
    public static SignalOutcome Compute(IReadOnlyList<Candle> candles, int signalIndex)
    {
        var start = candles[signalIndex].OpenTime;

        // The candle N minutes after the signal, or null if it isn't there (end of data or a gap).
        Candle? CandleAt(int minutes)
        {
            var i = signalIndex + minutes;
            return i < candles.Count && candles[i].OpenTime == start.AddMinutes(minutes) ? candles[i] : null;
        }

        var window = Enumerable.Range(1, WindowMinutes).Select(CandleAt).ToList();
        var complete = window.All(c => c is not null);
        return new SignalOutcome(
            CandleAt(5)?.Close,
            CandleAt(15)?.Close,
            CandleAt(30)?.Close,
            complete ? window.Max(c => c!.High) : null,
            complete ? window.Min(c => c!.Low) : null);
    }
}
