using ChartAnalyzer.Analysis;

namespace ChartAnalyzer.Data;

/// <summary>A support/resistance zone you asked the app to watch via Telegram (/zone).</summary>
public sealed class WatchedZone
{
    private const int MaxAlertedKeys = 100;

    public int Id { get; set; }
    public decimal Low { get; set; }
    public decimal High { get; set; }
    public ZoneRole Role { get; set; }
    public ZoneProfile Profile { get; set; } = ZoneProfile.Local;

    /// <summary>Start of the analysis window.</summary>
    public DateTimeOffset Since { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Keys of signs already alerted (newline-separated), so each alert is sent once.</summary>
    public string AlertedKeys { get; set; } = "";

    public PriceZone ToPriceZone() => new(Low, High, Role, Profile);

    public bool Matches(decimal low, decimal high) => Low == Math.Min(low, high) && High == Math.Max(low, high);

    /// <summary>Records the key; false if it was already alerted.</summary>
    public bool MarkAlerted(string key)
    {
        var keys = AlertedKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (keys.Contains(key))
            return false;
        keys.Add(key);
        AlertedKeys = string.Join('\n', keys.TakeLast(MaxAlertedKeys));
        return true;
    }
}
