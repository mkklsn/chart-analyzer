using ChartAnalyzer.Data;
using ChartAnalyzer.Models;
using Microsoft.EntityFrameworkCore;

namespace ChartAnalyzer.Coinbase;

/// <summary>
/// Reads the stored Coinbase candles for zone analysis: 1-minute for local zones, combined into 1-hour for macro zones.
/// Keeps them in memory and only reads new rows on later calls. Never throws: if the database can't be read,
/// zone analysis goes ahead without Coinbase.
/// </summary>
public sealed class CoinbaseHistory(IDbContextFactory<ChartDbContext> dbFactory, ILogger<CoinbaseHistory> logger)
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    // Enough for the longest analyses (local: 30 days + 2 days of history; macro: 180 days + 31 days).
    private static readonly TimeSpan KeepMinutes = TimeSpan.FromDays(33);
    private static readonly TimeSpan KeepHours = TimeSpan.FromDays(212);

    // A slow database shouldn't hold up the hourly zone check.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<Candle> _minutes = [];
    private readonly List<Candle> _hours = [];   // complete hours
    private readonly List<Candle> _currentHour = [];

    /// <summary>Stored candles from <paramref name="from"/> at the given interval (1 minute or 1 hour), oldest first.</summary>
    public async Task<IReadOnlyList<Candle>> GetAsync(DateTimeOffset from, TimeSpan interval, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await LoadNewAsync(ct);
            return (interval == Hour ? _hours : _minutes).Where(c => c.OpenTime >= from).ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Stored Coinbase candles couldn't be read, zone analysis continues without Coinbase: {Message}", ex.Message);
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task LoadNewAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ReadTimeout);

        var now = DateTimeOffset.UtcNow;
        var after = _minutes.Count > 0 ? _minutes[^1].OpenTime : now - KeepHours;
        await using var db = await dbFactory.CreateDbContextAsync(timeout.Token);
        var rows = await db.ExchangeCandles.AsNoTracking()
            .Where(c => c.Exchange == CoinbaseCandles.Exchange && c.OpenTime > after)
            .OrderBy(c => c.OpenTime)
            .ToListAsync(timeout.Token);

        foreach (var r in rows)
        {
            var candle = new Candle(r.OpenTime, r.Open, r.High, r.Low, r.Close, r.Volume, r.TakerBuyVolume);
            if (_currentHour.Count > 0 && Floor(candle.OpenTime) != Floor(_currentHour[0].OpenTime))
                CloseHour();
            _currentHour.Add(candle);
            if (candle.OpenTime >= now - KeepMinutes)
                _minutes.Add(candle);
        }

        // The last minute of an hour closes it without waiting for the next hour's first minute.
        if (_currentHour.Count > 0 && _currentHour[^1].OpenTime.Minute == 59)
            CloseHour();

        _minutes.RemoveAll(c => c.OpenTime < now - KeepMinutes);
        _hours.RemoveAll(c => c.OpenTime < now - KeepHours);
    }

    private void CloseHour()
    {
        var m = _currentHour;
        _hours.Add(new Candle(Floor(m[0].OpenTime), m[0].Open, m.Max(c => c.High), m.Min(c => c.Low), m[^1].Close,
            m.Sum(c => c.Volume), m.Sum(c => c.TakerBuyVolume)));
        _currentHour.Clear();
    }

    private static DateTimeOffset Floor(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % Hour.Ticks, TimeSpan.Zero);
}
