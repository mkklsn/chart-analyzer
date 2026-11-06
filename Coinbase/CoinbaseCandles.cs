using ChartAnalyzer.Data;
using ChartAnalyzer.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChartAnalyzer.Coinbase;

/// <summary>
/// Builds 1-minute Coinbase candles (with taker buy volume, which Coinbase's own candles don't have) from its trades.
/// Each update fetches the trades since the last one seen, so a missed update or a short outage leaves no gap.
/// Closed candles are kept in memory for the live check and saved to the database for later tuning.
/// </summary>
public sealed class CoinbaseCandles(CoinbaseClient coinbase, DbWriteQueue dbQueue, IOptions<AnalyzerOptions> options, ILogger<CoinbaseCandles> logger)
{
    public const string Exchange = "Coinbase";

    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>After a longer outage than this, start over instead of paging back through all missed trades.</summary>
    private static readonly TimeSpan MaxCatchUp = TimeSpan.FromHours(2);

    private static readonly TimeSpan Keep = TimeSpan.FromHours(3);

    private readonly string _product = options.Value.CoinbaseProduct;
    private readonly List<Candle> _candles = [];                                  // closed minutes, oldest first, no gaps
    private readonly SortedDictionary<DateTimeOffset, MinuteBuilder> _open = [];  // minutes still receiving trades
    private long? _lastTradeId;

    /// <summary>Closed candles, oldest first.</summary>
    public IReadOnlyList<Candle> Candles => _candles.ToList();

    /// <summary>
    /// Fetches new trades and closes every minute that ended before <paramref name="now"/>.
    /// On the first run (or after a long outage) it rebuilds the last <paramref name="minutesNeeded"/> minutes.
    /// </summary>
    public async Task UpdateAsync(DateTimeOffset now, int minutesNeeded, CancellationToken ct)
    {
        var start = Floor(now) - minutesNeeded * Minute;
        var (trades, continuous) = await FetchNewTradesAsync(now, start, ct);
        if (!continuous)
        {
            if (_lastTradeId is not null)
                logger.LogWarning("Coinbase trades were out of reach for too long; rebuilding the last {Minutes} minutes.", minutesNeeded);
            _candles.Clear();
            _open.Clear();
        }

        foreach (var trade in trades.OrderBy(t => t.Id))
        {
            _lastTradeId = trade.Id;
            var minute = Floor(trade.Time);
            var closedUntil = _candles.Count > 0 ? _candles[^1].OpenTime + Minute : start;
            if (minute < closedUntil)
                continue;   // a straggler for a minute that's already closed (or before the rebuilt range)
            if (!_open.TryGetValue(minute, out var builder))
                _open[minute] = builder = new MinuteBuilder(minute);
            builder.Add(trade);
        }

        var closed = CloseMinutes(Floor(now));
        _candles.RemoveAll(c => c.OpenTime < now - Keep);
        if (closed.Count > 0)
            Save(closed);
        foreach (var c in closed.TakeLast(3))
            logger.LogDebug("Coinbase {Time:HH:mm} O {Open} H {High} L {Low} C {Close} V {Volume:0.########} taker buy {Buy:0.########}",
                c.OpenTime, c.Open, c.High, c.Low, c.Close, c.Volume, c.TakerBuyVolume);
    }

    // Pages back from the newest trade to the last one seen. 'continuous' is false when starting fresh.
    private async Task<(List<CoinbaseTrade> Trades, bool Continuous)> FetchNewTradesAsync(DateTimeOffset now, DateTimeOffset start, CancellationToken ct)
    {
        var limit = _lastTradeId is null ? start : now - MaxCatchUp;
        var trades = new List<CoinbaseTrade>();
        long? olderThan = null;
        while (true)
        {
            var page = await coinbase.GetTradesAsync(_product, olderThan, ct);
            foreach (var trade in page)
            {
                if (_lastTradeId is { } last && trade.Id <= last)
                    return (trades, true);
                if (trade.Time < limit)
                    return (trades, false);
                trades.Add(trade);
            }
            if (page.Count < CoinbaseClient.PageSize)
                return (trades, _lastTradeId is not null && page.Count > 0);

            olderThan = page[^1].Id;
            await Task.Delay(150, ct);   // stays well under Coinbase's public limit of 10 requests a second
        }
    }

    // Turns the open minutes before 'until' into candles. A minute without trades gets a flat candle at the last price.
    private List<Candle> CloseMinutes(DateTimeOffset until)
    {
        var closed = new List<Candle>();
        var next = _candles.Count > 0 ? _candles[^1].OpenTime + Minute : _open.Keys.FirstOrDefault(k => k < until, until);
        for (; next < until; next += Minute)
        {
            Candle candle;
            if (_open.Remove(next, out var builder))
                candle = builder.ToCandle();
            else if (_candles.Count > 0)
                candle = new Candle(next, _candles[^1].Close, _candles[^1].Close, _candles[^1].Close, _candles[^1].Close, 0, 0);
            else
                continue;
            _candles.Add(candle);
            closed.Add(candle);
        }
        return closed;
    }

    // Insert or replace: after a restart the rebuilt minutes overwrite what was stored before.
    private void Save(List<Candle> candles)
    {
        var (from, to) = (candles[0].OpenTime, candles[^1].OpenTime);
        dbQueue.Enqueue($"save {candles.Count} Coinbase candles", async (db, ct) =>
        {
            var stored = await db.ExchangeCandles
                .Where(c => c.Exchange == Exchange && c.OpenTime >= from && c.OpenTime <= to)
                .ToDictionaryAsync(c => c.OpenTime, ct);
            foreach (var c in candles)
            {
                if (!stored.TryGetValue(c.OpenTime, out var row))
                    db.ExchangeCandles.Add(row = new ExchangeCandle { Exchange = Exchange, OpenTime = c.OpenTime });
                row.Open = c.Open;
                row.High = c.High;
                row.Low = c.Low;
                row.Close = c.Close;
                row.Volume = c.Volume;
                row.TakerBuyVolume = c.TakerBuyVolume;
            }
            await db.SaveChangesAsync(ct);
        });
    }

    private static DateTimeOffset Floor(DateTimeOffset t) =>
        new(t.UtcTicks - t.UtcTicks % Minute.Ticks, TimeSpan.Zero);

    private sealed class MinuteBuilder(DateTimeOffset openTime)
    {
        private decimal _open, _high = decimal.MinValue, _low = decimal.MaxValue, _close, _volume, _takerBuy;
        private bool _started;

        public void Add(CoinbaseTrade t)
        {
            if (!_started)
                (_open, _started) = (t.Price, true);
            _high = Math.Max(_high, t.Price);
            _low = Math.Min(_low, t.Price);
            _close = t.Price;
            _volume += t.Size;
            if (t.TakerBuy)
                _takerBuy += t.Size;
        }

        public Candle ToCandle() => new(openTime, _open, _high, _low, _close, _volume, _takerBuy);
    }
}
