using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>The single definition of "same tag" shared by the save endpoints and the resume auto-fill merge.</summary>
public class ProfileTagsTests
{
    [Theory]
    [InlineData("  React ", "React")]
    [InlineData("Type   Script", "Type Script")]
    [InlineData("\tC# \n", "C#")]
    [InlineData(null, "")]
    public void Normalize_trims_and_collapses_whitespace(string? input, string expected) =>
        Assert.Equal(expected, ProfileTags.Normalize(input));

    [Fact]
    public void Same_ignores_case_and_spacing()
    {
        Assert.True(ProfileTags.Same("React", "react"));
        Assert.True(ProfileTags.Same(" Data  Science ", "data science"));
        Assert.False(ProfileTags.Same("React", "React Native"));
    }

    [Fact]
    public void Dedupe_keeps_first_seen_casing_and_order_and_drops_blanks()
    {
        var result = ProfileTags.Dedupe(new[] { "React", "react", " SQL", "", null, "sql", "C#", "REACT" });
        Assert.Equal(new[] { "React", "SQL", "C#" }, result);
    }

    [Fact]
    public void Merge_adds_only_unseen_tags_and_never_removes()
    {
        var (merged, added) = ProfileTags.Merge(new[] { "React", "SQL" }, new[] { "react", "C#", "sql ", "Go", "go" });
        Assert.Equal(new[] { "React", "SQL", "C#", "Go" }, merged);
        Assert.Equal(new[] { "C#", "Go" }, added);
    }

    [Fact]
    public void Merge_also_collapses_stored_duplicates()
    {
        var (merged, added) = ProfileTags.Merge(new[] { "React", "react", "SQL" }, Array.Empty<string>());
        Assert.Equal(new[] { "React", "SQL" }, merged);
        Assert.Empty(added);
    }

    [Fact]
    public void Merge_tolerates_null_inputs()
    {
        var (merged, added) = ProfileTags.Merge(null, null);
        Assert.Empty(merged);
        Assert.Empty(added);
    }

    [Fact]
    public void Json_round_trip_dedupes_and_stores_empty_as_null()
    {
        Assert.Null(ProfileTags.ToJson(null));
        Assert.Null(ProfileTags.ToJson(new[] { " ", "" }));
        var json = ProfileTags.ToJson(new[] { "React", "react", "SQL" });
        Assert.Equal(new[] { "React", "SQL" }, ProfileTags.FromJson(json));
        Assert.Empty(ProfileTags.FromJson("not json"));
        Assert.Empty(ProfileTags.FromJson(null));
    }

    [Theory]
    [InlineData(false, 0, 0, "nothing new to add")]
    [InlineData(true,  0, 0, "filled in your name")]
    [InlineData(false, 1, 0, "added 1 skill")]
    [InlineData(false, 6, 2, "added 6 skills and 2 target roles")]
    [InlineData(true,  0, 1, "filled in your name and added 1 target role")]
    public void AutoFill_summary_reads_naturally(bool name, int skills, int roles, string expected)
    {
        var r = new ProfileAutoFillResult(true, null, name,
            Enumerable.Range(0, skills).Select(i => $"s{i}").ToList(),
            Enumerable.Range(0, roles).Select(i => $"r{i}").ToList(),
            null, new(), new());
        Assert.Equal(expected, r.Summary);
    }
}
