using ChartAnalyzer.Data;

namespace ChartAnalyzer.Zones;

/// <summary>
/// Your watched zones, kept in memory. Loaded from the database at startup; changes are saved in the background.
/// </summary>
public sealed class ZoneStore
{
    private readonly Lock _lock = new();
    private readonly List<WatchedZone> _zones = [];

    public bool IsLoaded { get; private set; }

    public void Load(IEnumerable<WatchedZone> zones)
    {
        lock (_lock)
        {
            _zones.Clear();
            _zones.AddRange(zones);
            IsLoaded = true;
        }
    }

    /// <summary>False if a zone with the same prices is already watched.</summary>
    public bool Add(WatchedZone zone)
    {
        lock (_lock)
        {
            if (_zones.Any(z => z.Matches(zone.Low, zone.High)))
                return false;
            _zones.Add(zone);
            return true;
        }
    }

    public WatchedZone? Remove(decimal low, decimal high)
    {
        lock (_lock)
        {
            var zone = _zones.FirstOrDefault(z => z.Matches(low, high));
            if (zone is not null)
                _zones.Remove(zone);
            return zone;
        }
    }

    public IReadOnlyList<WatchedZone> GetAll()
    {
        lock (_lock)
            return _zones.ToList();
    }
}
