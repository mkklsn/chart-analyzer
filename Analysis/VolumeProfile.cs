using ChartAnalyzer.Models;

namespace ChartAnalyzer.Analysis;

/// <summary>A price range, e.g. a high-volume node or a cluster of absorption.</summary>
public readonly record struct PriceRange(decimal Low, decimal High)
{
    public decimal Mid => (Low + High) / 2;

    public bool Overlaps(PriceRange other, decimal tolerance = 0) => Low <= other.High + tolerance && High >= other.Low - tolerance;

    public bool Contains(decimal price, decimal tolerance = 0) => price >= Low - tolerance && price <= High + tolerance;

    public override string ToString() => $"{Low:N0}–{High:N0}";
}

/// <summary>
/// Volume traded at each price over a range of candles, in bins of equal size.
/// Each candle's volume is spread evenly over its high-low range: on 1-minute candles that's only a few bins,
/// so the result is close to a real (trade-by-trade) volume profile.
/// </summary>
public sealed class VolumeProfile
{
    private readonly decimal[] _volumes;

    private VolumeProfile(decimal origin, decimal binSize, decimal[] volumes)
    {
        Origin = origin;
        BinSize = binSize;
        _volumes = volumes;
    }

    /// <summary>Low of the first bin.</summary>
    public decimal Origin { get; }

    public decimal BinSize { get; }

    public IReadOnlyList<decimal> Volumes => _volumes;

    public bool IsEmpty => _volumes.Length == 0;

    public decimal Total => _volumes.Sum();

    public static VolumeProfile Build(IReadOnlyList<Candle> candles, decimal binSize)
    {
        if (candles.Count == 0)
            return new VolumeProfile(0, binSize, []);

        var origin = Math.Floor(candles.Min(c => c.Low) / binSize) * binSize;
        var volumes = new decimal[(int)((candles.Max(c => c.High) - origin) / binSize) + 1];
        foreach (var c in candles)
        {
            var first = (int)((c.Low - origin) / binSize);
            var last = (int)((c.High - origin) / binSize);
            if (first == last)
            {
                volumes[first] += c.Volume;
                continue;
            }
            for (var b = first; b <= last; b++)
            {
                var binLow = origin + b * binSize;
                var overlap = Math.Min(c.High, binLow + binSize) - Math.Max(c.Low, binLow);
                volumes[b] += c.Volume * overlap / c.Range;
            }
        }
        return new VolumeProfile(origin, binSize, volumes);
    }

    public PriceRange Bin(int index) => new(Origin + index * BinSize, Origin + (index + 1) * BinSize);

    /// <summary>The bin with the most volume (point of control).</summary>
    public int PocIndex
    {
        get
        {
            var best = 0;
            for (var i = 1; i < _volumes.Length; i++)
            {
                if (_volumes[i] > _volumes[best])
                    best = i;
            }
            return best;
        }
    }

    /// <summary>The price range around the POC that holds this share of the volume (usually 70%).</summary>
    public PriceRange ValueArea(decimal share = 0.7m)
    {
        int low = PocIndex, high = low;
        var target = Total * share;
        var sum = _volumes[low];
        while (sum < target && (low > 0 || high < _volumes.Length - 1))
        {
            var below = low > 0 ? _volumes[low - 1] : -1;
            var above = high < _volumes.Length - 1 ? _volumes[high + 1] : -1;
            if (above >= below)
                sum += _volumes[++high];
            else
                sum += _volumes[--low];
        }
        return new PriceRange(Bin(low).Low, Bin(high).High);
    }

    /// <summary>Moving average over 2 * radius + 1 bins, so single noisy bins don't count as nodes.</summary>
    public decimal[] Smoothed(int radius) => Smooth(_volumes, radius);

    public static decimal[] Smooth(decimal[] values, int radius)
    {
        var result = new decimal[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            int from = Math.Max(0, i - radius), to = Math.Min(values.Length - 1, i + radius);
            decimal sum = 0;
            for (var j = from; j <= to; j++)
                sum += values[j];
            result[i] = sum / (to - from + 1);
        }
        return result;
    }
}
