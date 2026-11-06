using System.Globalization;
using System.Text.Json;
using ChartAnalyzer.Models;

namespace ChartAnalyzer.Binance;

/// <summary>Binance spot market data.</summary>
public class BinanceClient(HttpClient http)
{
    /// <summary>Shown in logs and alerts.</summary>
    public virtual string Market => "Binance";

    protected virtual string KlinesPath => "api/v3/klines";

    /// <summary>
    /// Fetches all closed candles with open time in [from, to), oldest first. The candle still in progress is left out.
    /// Binance returns at most 1000 per request, so this makes as many requests as needed.
    /// </summary>
    /// <param name="interval">Candle size: 1 minute (default) or 1 hour.</param>
    public async Task<IReadOnlyList<Candle>> GetClosedCandlesAsync(string symbol, DateTimeOffset from, DateTimeOffset to, CancellationToken ct,
        TimeSpan? interval = null)
    {
        var step = interval ?? TimeSpan.FromMinutes(1);
        var name = step == TimeSpan.FromHours(1) ? "1h" : step == TimeSpan.FromMinutes(1) ? "1m"
            : throw new ArgumentOutOfRangeException(nameof(interval), "Only 1-minute and 1-hour candles are supported.");

        var candles = new List<Candle>();
        var start = from;
        while (start < to)
        {
            var end = to.AddMilliseconds(-1);
            var page = await WithRetriesAsync(() => GetKlinesAsync(
                $"{KlinesPath}?symbol={symbol}&interval={name}&limit=1000" +
                $"&startTime={start.ToUnixTimeMilliseconds()}&endTime={end.ToUnixTimeMilliseconds()}", ct), ct);
            if (page.Count == 0)
                break;

            candles.AddRange(page);
            start = page[^1].OpenTime + step;
        }
        return candles;
    }

    // Long downloads make many requests; one dropped connection shouldn't fail the whole range.
    private static async Task<T> WithRetriesAsync<T>(Func<Task<T>> request, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await request();
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    private async Task<IReadOnlyList<Candle>> GetKlinesAsync(string url, CancellationToken ct)
    {
        await using var stream = await http.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // Each kline is an array:
        // [0] open time, [1] open, [2] high, [3] low, [4] close, [5] volume,
        // [6] close time, [7] quote volume, [8] trade count,
        // [9] taker buy base volume, [10] taker buy quote volume, [11] unused
        var now = DateTimeOffset.UtcNow;
        var candles = new List<Candle>();
        foreach (var k in doc.RootElement.EnumerateArray())
        {
            var closeTime = DateTimeOffset.FromUnixTimeMilliseconds(k[6].GetInt64());
            if (closeTime > now)
                continue;

            candles.Add(new Candle(
                OpenTime: DateTimeOffset.FromUnixTimeMilliseconds(k[0].GetInt64()),
                Open: ParseDecimal(k[1]),
                High: ParseDecimal(k[2]),
                Low: ParseDecimal(k[3]),
                Close: ParseDecimal(k[4]),
                Volume: ParseDecimal(k[5]),
                TakerBuyVolume: ParseDecimal(k[9])));
        }
        return candles;
    }

    // Binance sends numbers as strings to avoid floating-point rounding.
    private static decimal ParseDecimal(JsonElement e) =>
        decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
}

/// <summary>
/// Binance USDⓈ-M perpetual futures. Same candle format as spot (including taker buy volume), different host and path.
/// </summary>
public sealed class BinancePerpClient(HttpClient http) : BinanceClient(http)
{
    public override string Market => "Binance perp";

    protected override string KlinesPath => "fapi/v1/klines";
}
