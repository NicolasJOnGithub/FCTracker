namespace FCTracker.Services;

using System;
using System.Collections.Generic;
using System.Linq;

public class ConfigurationFCDataService : IFCDataProvider
{
    private static IEnumerable<FCData> Source => Configuration.Instance?.AllFCData.Values.ToList() ?? [];

    private ICharDataProvider? charData;
    public  ICharDataProvider  CharData() => this.charData ??= new ConfigurationCharDataService();

    public IReadOnlyList<FCData> GetAllFCs() => Source.ToList();

    /// <summary>
    /// FCs to show in Ready Now: those eligible to bid, plus any whose bid is still running or
    /// only just resolved. Winning a plot clears IsEligible, so without the second clause the
    /// FC would vanish from the tab at exactly the moment its result became interesting.
    /// Sorted by world so the view can group on it.
    /// </summary>
    public IReadOnlyList<FCData> GetEligibleFCs() =>
        Source.Where(fc => fc.IsEligible || fc.HasLiveLotteryBid)
              .OrderBy(fc => fc.WorldName)
              .ThenBy(fc => fc.FCName)
              .ToList();

    public IReadOnlyList<FCData> GetUpcomingFCs() =>
        Source.Where(fc => fc is { IsEligible: false, HasHouse: false })
              .OrderBy(fc => fc.EligibilityDate)
              .ToList();

    public IReadOnlyList<FCData> GetOwnedHousingFCs() =>
        Source.Where(fc => fc.HasHouse).ToList();

    public int GetTotalCount()        => Source.Count();
    public int GetReadyCount()        => Source.Count(fc => fc.IsEligible);
    public int GetUpcomingCount()     => Source.Count(fc => fc is { IsEligible: false, HasHouse: false });
    public int GetPending7DayCount()  => Source.Count(fc => fc is { IsEligible: false, HasHouse: false, TimeUntilEligible.TotalDays: <= 7 and > 0 });
    public int GetPending30DayCount() => Source.Count(fc => fc is { IsEligible: false, HasHouse: false, TimeUntilEligible.TotalDays: > 7 and <= 30 });

    public IEnumerable<string> GetRegions() =>
        Source.Select(fc => fc.Region).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(r => r);

    public IEnumerable<string> GetDatacenters() =>
        Source.Select(fc => fc.Datacenter).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(d => d);

    public IEnumerable<string> GetWorlds() =>
        Source.Select(fc => fc.WorldName).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(w => w);

    public Dictionary<string, List<string>> GetDatacentersByRegion() =>
        Source.Where(fc => !string.IsNullOrEmpty(fc.Region) && !string.IsNullOrEmpty(fc.Datacenter))
              .GroupBy(fc => fc.Region)
              .ToDictionary(
                            g => g.Key,
                            g => g.Select(fc => fc.Datacenter).Distinct().OrderBy(d => d).ToList());

    public Dictionary<string, List<string>> GetWorldsByDatacenter() =>
        Source.Where(fc => !string.IsNullOrEmpty(fc.Datacenter) && !string.IsNullOrEmpty(fc.WorldName))
              .GroupBy(fc => fc.Datacenter)
              .ToDictionary(
                            g => g.Key,
                            g => g.Select(fc => fc.WorldName).Distinct().OrderBy(w => w).ToList());

    public int GetFCCountForWorld(string world) =>
        Source.Count(fc => string.Equals(fc.WorldName, world, StringComparison.OrdinalIgnoreCase));

    public int GetFCCountForDatacenter(string datacenter) =>
        Source.Count(fc => string.Equals(fc.Datacenter, datacenter, StringComparison.OrdinalIgnoreCase));

    public int GetFCCountForRegion(string region) =>
        Source.Count(fc => string.Equals(fc.Region, region, StringComparison.OrdinalIgnoreCase));

    public (int Done, int Ready, int Upcoming, int Total) GetStatusCountsForWorld(string world) =>
        ComputeStatusCounts(Source.Where(fc => string.Equals(fc.WorldName, world, StringComparison.OrdinalIgnoreCase)));

    public (int Done, int Ready, int Upcoming, int Total) GetStatusCountsForDatacenter(string datacenter) =>
        ComputeStatusCounts(Source.Where(fc => string.Equals(fc.Datacenter, datacenter, StringComparison.OrdinalIgnoreCase)));

    public (int Done, int Ready, int Upcoming, int Total) GetStatusCountsForRegion(string region) =>
        ComputeStatusCounts(Source.Where(fc => string.Equals(fc.Region, region, StringComparison.OrdinalIgnoreCase)));

    private static (int Done, int Ready, int Upcoming, int Total) ComputeStatusCounts(IEnumerable<FCData> fcs)
    {
        List<FCData> list     = fcs.ToList();
        int          done     = list.Count(fc => fc.HasHouse);
        int          ready    = list.Count(fc => fc.IsEligible);
        int          upcoming = list.Count(fc => fc is { IsEligible: false, HasHouse: false, TimeUntilEligible.TotalDays: <= 7 });
        return (done, ready, upcoming, list.Count);
    }
}