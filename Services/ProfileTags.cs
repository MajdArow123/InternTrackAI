using System.Text.Json;
using System.Text.RegularExpressions;

namespace InternTrackAI.Services;

/// <summary>
/// The one place that decides when two profile tags (skills, target roles) are "the same":
/// trimmed, internal runs of whitespace collapsed, compared case-insensitively. Used by the
/// save endpoints, by the resume auto-fill merge, and mirrored by the client-side tag input
/// (wwwroot/js/tag-input.js) so a duplicate can't get in from either side.
/// </summary>
public static class ProfileTags
{
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Trims and collapses internal whitespace; the stored form of every tag.</summary>
    public static string Normalize(string? tag) => tag is null ? string.Empty : Spaces.Replace(tag.Trim(), " ");

    /// <summary>True when the two tags are equal after <see cref="Normalize"/>, ignoring case.</summary>
    public static bool Same(string? a, string? b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drops blanks and case-insensitive duplicates, keeping the first-seen casing and order
    /// (so "React" then "react" stores a single "React").
    /// </summary>
    public static List<string> Dedupe(IEnumerable<string?>? tags)
    {
        var result = new List<string>();
        if (tags is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in tags)
        {
            var tag = Normalize(raw);
            if (tag.Length == 0 || !seen.Add(tag)) continue;
            result.Add(tag);
        }
        return result;
    }

    /// <summary>
    /// Adds the <paramref name="incoming"/> tags that aren't already present (case-insensitive) to the
    /// end of <paramref name="existing"/>; never removes anything. Returns the merged list and the
    /// tags that were actually added.
    /// </summary>
    public static (List<string> Merged, List<string> Added) Merge(IEnumerable<string?>? existing, IEnumerable<string?>? incoming)
    {
        var merged = Dedupe(existing);
        var before = merged.Count;
        var seen = new HashSet<string>(merged, StringComparer.OrdinalIgnoreCase);
        foreach (var raw in incoming ?? Array.Empty<string>())
        {
            var tag = Normalize(raw);
            if (tag.Length == 0 || !seen.Add(tag)) continue;
            merged.Add(tag);
        }
        return (merged, merged.Skip(before).ToList());
    }

    /// <summary>Deserializes a stored JSON string array, tolerating null/malformed input.</summary>
    public static List<string> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Serializes a tag list for storage; an empty list stores as null.</summary>
    public static string? ToJson(IEnumerable<string>? tags)
    {
        var list = Dedupe(tags);
        return list.Count == 0 ? null : JsonSerializer.Serialize(list);
    }
}
