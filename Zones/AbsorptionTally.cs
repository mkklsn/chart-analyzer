using ChartAnalyzer.Analysis;
using ChartAnalyzer.Data;
using ChartAnalyzer.Models;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace ChartAnalyzer.Zones;

/// <summary>
/// The absorption events at your zones, kept in memory for the scoreboards. Loaded from the database at startup;
/// changes are saved in the background. Callers get copies, so they never see an event change under them.
/// </summary>
public sealed class AbsorptionTally(DbWriteQueue queue)
{
    /// <summary>Events older than this aren't loaded at startup.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(90);

    private readonly Lock _lock = new();
    private readonly List<ZoneAbsorption> _events = [];

    public void Load(IEnumerable<ZoneAbsorption> events)
    {
        lock (_lock)
        {
            _events.Clear();
            _events.AddRange(events.OrderBy(e => e.Minute));
        }
    }

    public void Add(ZoneAbsorption e)
    {
        var stored = e.Copy();
        lock (_lock)
            _events.Add(e.Copy());
        queue.Enqueue($"record {e.Side} absorption at {e.ZoneLow}-{e.ZoneHigh}", async (db, ct) =>
        {
            db.ZoneAbsorptions.Add(stored);
            await db.SaveChangesAsync(ct);
        });
    }

    /// <summary>Adds markets that confirmed these events after the alert.</summary>
    public void AddExchanges(IEnumerable<Guid> ids, IEnumerable<string> exchanges, bool perpsOnly)
    {
        var added = exchanges.ToList();
        lock (_lock)
        {
            foreach (var e in _events.Where(e => ids.Contains(e.Id)))
            {
                var all = e.Exchanges.Split(", ", StringSplitOptions.RemoveEmptyEntries).Union(added).ToList();
                e.Exchanges = string.Join(", ", all);
                e.PerpsOnly = perpsOnly;
                Save(e);
            }
        }
    }

    public IReadOnlyList<ZoneAbsorption> Pending()
    {
        lock (_lock)
            return _events.Where(e => e.Outcome == AbsorptionOutcome.Pending).Select(e => e.Copy()).ToList();
    }

    /// <summary>Decides the event from the candles after it; returns a copy of it if it was decided now.</summary>
    public ZoneAbsorption? Resolve(Guid id, IReadOnlyList<Candle> candles, TimeSpan hold)
    {
        lock (_lock)
        {
            var e = _events.FirstOrDefault(x => x.Id == id);
            if (e is null || !AbsorptionScore.Resolve(e, candles, hold))
                return null;
            Save(e);
            return e.Copy();
        }
    }

    /// <summary>The zone's events since it was added, oldest first.</summary>
    public IReadOnlyList<ZoneAbsorption> ForZone(WatchedZone zone)
    {
        lock (_lock)
            return _events.Where(e => e.IsAt(zone) && e.Minute >= zone.CreatedAt).Select(e => e.Copy()).ToList();
    }

    /// <summary>
    /// Events rebuilt from earlier absorption alerts (and their confirmations), all pending:
    /// the live worker decides them from Binance candles.
    /// </summary>
    public static List<ZoneAbsorption> FromAlerts(IReadOnlyList<SentAlert> alerts, IReadOnlyList<WatchedZone> zones)
    {
        var events = new List<ZoneAbsorption>();
        foreach (var a in alerts.Where(a => a.Kind == AlertKind.Absorption && a.Side is not null && a.Minute is not null))
        {
            var side = a.Side!.Value;
            List<StoredCheck> checks;
            try
            {
                checks = JsonSerializer.Deserialize<List<StoredCheck>>(a.Details ?? "[]", Json) ?? [];
            }
            catch (JsonException)
            {
                continue;
            }
            var binance = checks.FirstOrDefault(c => c.Exchange == "Binance" && c.Low is not null);
            var windows = checks.Where(c => c.Side == side && c.Low is not null).Append(binance).OfType<StoredCheck>().ToList();
            if (windows.Count == 0)
                continue;

            var exchanges = (a.Exchanges ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries)
                .Concat(alerts
                    .Where(c => c.Kind == AlertKind.Confirmation && c.Side == side && c.ConfirmsMinute == a.Minute)
                    .SelectMany(c => (c.Exchanges ?? "").Split(", ", StringSplitOptions.RemoveEmptyEntries)))
                .Distinct()
                .ToList();
            var names = (a.Zones ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries);
            foreach (var zone in zones.Where(z => names.Contains(z.Name)))
            {
                events.Add(new ZoneAbsorption
                {
                    Id = Guid.NewGuid(),
                    ZoneLow = zone.Low,
                    ZoneHigh = zone.High,
                    Side = side,
                    Minute = a.Minute!.Value,
                    Price = a.Price ?? windows[0].Close ?? 0,
                    Level = side == AbsorptionSide.Seller ? windows.Min(c => c.Low!.Value) : windows.Max(c => c.High!.Value),
                    SpotVolume = binance?.Side == side ? (side == AbsorptionSide.Seller ? binance.SellVolume : binance.BuyVolume) ?? 0 : 0,
                    Exchanges = string.Join(", ", exchanges),
                    PerpsOnly = exchanges.All(e => e == "Binance perp"),
                    Outcome = AbsorptionOutcome.Pending,
                });
            }
        }
        return events;
    }

    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    // The per-exchange numbers stored with each alert (see AlertLog.DescribeChecks).
    private sealed record StoredCheck(string Exchange, AbsorptionSide? Side, decimal? Close, decimal? Low, decimal? High,
        decimal? SellVolume, decimal? BuyVolume);

    private void Save(ZoneAbsorption e)
    {
        var (id, exchanges, perpsOnly, outcome, minute, close) = (e.Id, e.Exchanges, e.PerpsOnly, e.Outcome, e.ResolvedMinute, e.ResolvedClose);
        queue.Enqueue($"update absorption {id}", (db, ct) => db.ZoneAbsorptions.Where(x => x.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Exchanges, exchanges)
            .SetProperty(x => x.PerpsOnly, perpsOnly)
            .SetProperty(x => x.Outcome, outcome)
            .SetProperty(x => x.ResolvedMinute, minute)
            .SetProperty(x => x.ResolvedClose, close), ct));
    }
}
