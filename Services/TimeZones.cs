using System.Globalization;

namespace InternTrackAI.Services;

/// <summary>One entry of the profile's time zone dropdown.</summary>
public sealed record TimeZoneOption(string Id, string Label);

/// <summary>A dropdown optgroup: the IANA region ("America", "Europe", …) and its zones, sorted by label.</summary>
public sealed record TimeZoneGroup(string Region, IReadOnlyList<TimeZoneOption> Zones);

/// <summary>
/// The catalogue of IANA zones the app accepts. <see cref="Resolve"/> is the single way a stored
/// id becomes a <see cref="TimeZoneInfo"/>: anything unknown (typo, Windows id on a Linux host,
/// zone removed from tzdata) falls back to <see cref="DefaultZoneId"/> rather than throwing, so a
/// bad row can never take a page down.
/// </summary>
public static class TimeZones
{
    public const string DefaultZoneId = "America/Toronto";

    /// <summary>Regions shown in the dropdown; legacy aliases (US/*, Canada/*, Etc/*) are left out.</summary>
    private static readonly string[] Regions =
    {
        "Africa", "America", "Antarctica", "Arctic", "Asia", "Atlantic", "Australia", "Europe", "Indian", "Pacific"
    };

    private static readonly Lazy<IReadOnlyDictionary<string, TimeZoneInfo>> Known = new(LoadKnown);

    /// <summary>True when <paramref name="id"/> is a zone this host knows (case-sensitive, as IANA ids are).</summary>
    public static bool IsValid(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TryFind(id, out _);

    /// <summary>The zone for a stored id, or the default zone when the id is missing or unknown.</summary>
    public static TimeZoneInfo Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TryFind(id, out var tz)) return tz;
        return TryFind(DefaultZoneId, out var fallback) ? fallback : TimeZoneInfo.Utc;
    }

    /// <summary>
    /// Zones grouped by region for the profile dropdown, each labelled "City (UTC±HH:MM)" with the
    /// offset in force at <paramref name="atUtc"/> (so Toronto reads −04:00 in summer, −05:00 in winter).
    /// UTC is its own one-entry group at the top. <paramref name="includeId"/> (the user's current
    /// zone) is appended even if it isn't in the catalogue, so the select can always pre-select it.
    /// </summary>
    public static IReadOnlyList<TimeZoneGroup> Groups(DateTime atUtc, string? includeId = null)
    {
        var groups = new List<TimeZoneGroup> { new("UTC", new[] { new TimeZoneOption("UTC", "UTC (UTC+00:00)") }) };

        var byRegion = Known.Value.Values
            .Where(tz => tz.Id.Contains('/') && Regions.Contains(tz.Id[..tz.Id.IndexOf('/')]))
            .GroupBy(tz => tz.Id[..tz.Id.IndexOf('/')])
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var g in byRegion)
        {
            var zones = g.Select(tz => new TimeZoneOption(tz.Id, Label(tz, atUtc)))
                         .OrderBy(o => o.Label, StringComparer.Ordinal)
                         .ToList();
            groups.Add(new TimeZoneGroup(g.Key, zones));
        }

        if (!string.IsNullOrWhiteSpace(includeId) && includeId != "UTC"
            && !groups.Any(gr => gr.Zones.Any(z => z.Id == includeId)))
        {
            var label = TryFind(includeId, out var tz) ? Label(tz, atUtc) : includeId;
            groups.Add(new TimeZoneGroup("Other", new[] { new TimeZoneOption(includeId, label) }));
        }

        return groups;
    }

    /// <summary>"Toronto (UTC−04:00)", "Sao Paulo (UTC−03:00)", "Argentina / Buenos Aires (UTC−03:00)".</summary>
    public static string Label(TimeZoneInfo tz, DateTime atUtc)
    {
        var city = tz.Id.Contains('/') ? tz.Id[(tz.Id.IndexOf('/') + 1)..] : tz.Id;
        city = city.Replace('/', ' ').Replace(" ", " / ").Replace('_', ' ');
        return $"{city} ({Offset(tz.GetUtcOffset(atUtc))})";
    }

    /// <summary>"UTC−04:00" / "UTC+05:30" / "UTC+00:00".</summary>
    public static string Offset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "−" : "+";
        offset = offset.Duration();
        return string.Create(CultureInfo.InvariantCulture, $"UTC{sign}{offset.Hours:00}:{offset.Minutes:00}");
    }

    private static bool TryFind(string id, out TimeZoneInfo tz)
    {
        if (Known.Value.TryGetValue(id, out tz!)) return true;
        // Not in the enumerated list (some hosts enumerate fewer zones than they can resolve).
        return TimeZoneInfo.TryFindSystemTimeZoneById(id, out tz!);
    }

    private static IReadOnlyDictionary<string, TimeZoneInfo> LoadKnown()
    {
        var dict = new Dictionary<string, TimeZoneInfo>(StringComparer.Ordinal);
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
            dict.TryAdd(tz.Id, tz);
        dict.TryAdd("UTC", TimeZoneInfo.Utc);
        return dict;
    }
}
