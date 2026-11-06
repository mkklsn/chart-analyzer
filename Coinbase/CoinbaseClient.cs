using System.Globalization;
using System.Text.Json;

namespace ChartAnalyzer.Coinbase;

/// <summary>One Coinbase trade.</summary>
/// <param name="TakerBuy">True when the impatient side (taker) was the buyer.</param>
public sealed record CoinbaseTrade(long Id, DateTimeOffset Time, decimal Price, decimal Size, bool TakerBuy);

/// <summary>Public market data from the Coinbase Exchange API (no key needed).</summary>
public sealed class CoinbaseClient(HttpClient http)
{
    public const int PageSize = 1000;

    /// <summary>
    /// One page of trades, newest first. Pass <paramref name="olderThan"/> (a trade id) to get the page before it.
    /// </summary>
    public async Task<IReadOnlyList<CoinbaseTrade>> GetTradesAsync(string product, long? olderThan, CancellationToken ct)
    {
        var url = $"products/{product}/trades?limit={PageSize}" + (olderThan is { } id ? $"&after={id}" : "");
        await using var stream = await http.GetStreamAsync(url, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var trades = new List<CoinbaseTrade>();
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            trades.Add(new CoinbaseTrade(
                Id: t.GetProperty("trade_id").GetInt64(),
                Time: DateTimeOffset.Parse(t.GetProperty("time").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                Price: ParseDecimal(t.GetProperty("price")),
                Size: ParseDecimal(t.GetProperty("size")),
                // Coinbase's "side" is the side of the resting (maker) order: "sell" means a buyer took it.
                TakerBuy: t.GetProperty("side").GetString() == "sell"));
        }
        return trades;
    }

    private static decimal ParseDecimal(JsonElement e) =>
        decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture);
}
