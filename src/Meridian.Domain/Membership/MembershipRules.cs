namespace Meridian.Domain.Membership;

public static class MembershipRules
{
    /// <summary>
    /// Returns every pair of overlapping periods (same country, at least one shared day), as indexes into
    /// <paramref name="periods"/> with the smaller index first. Several non-overlapping periods per country are valid:
    /// a country that left and came back has two rows.
    /// </summary>
    public static IReadOnlyList<(int First, int Second)> FindOverlaps(IReadOnlyList<MembershipPeriod> periods)
    {
        var overlaps = new List<(int, int)>();
        foreach (var group in periods.Select((p, i) => (Period: p, Index: i)).GroupBy(x => x.Period.CountryCode))
        {
            var items = group.ToList();
            for (var i = 0; i < items.Count; i++)
                for (var j = i + 1; j < items.Count; j++)
                    if (items[i].Period.Overlaps(items[j].Period))
                        overlaps.Add((items[i].Index, items[j].Index));
        }
        return overlaps;
    }
}
