using System.Text.Json;
using System.Text.Json.Serialization;
using ChartAnalyzer.Analysis;

namespace ChartAnalyzer.Data;

/// <summary>Records sent alerts in the database, via the write queue so senders never wait for it.</summary>
public sealed class AlertLog(DbWriteQueue queue)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Record(SentAlert alert) =>
        queue.Enqueue($"record {alert.Kind} alert", async (db, ct) =>
        {
            db.Alerts.Add(alert);
            await db.SaveChangesAsync(ct);
        });

    /// <summary>The numbers behind each exchange's check for the minute, as JSON.</summary>
    public static string DescribeChecks(IEnumerable<KeyValuePair<string, AbsorptionCheck?>> checks) =>
        JsonSerializer.Serialize(checks.Select(kv => kv.Value is not { } c
            ? new CheckDetail(kv.Key)
            : new CheckDetail(kv.Key, c.Side, c.Candle.OpenTime, c.WindowMinutes, c.Candle.Open, c.Candle.Close,
                c.Low, c.High, c.SellVolume, c.BuyVolume, c.SellRatio, c.BuyRatio, c.RangeRatio, c.ReferencePrice)), Json);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    // Null fields mean no data for that minute.
    private sealed record CheckDetail(
        string Exchange,
        AbsorptionSide? Side = null,
        DateTimeOffset? Minute = null,
        int? WindowMinutes = null,
        decimal? Open = null,
        decimal? Close = null,
        decimal? Low = null,
        decimal? High = null,
        decimal? SellVolume = null,
        decimal? BuyVolume = null,
        decimal? SellRatio = null,
        decimal? BuyRatio = null,
        decimal? RangeRatio = null,
        decimal? ReferencePrice = null);
}
