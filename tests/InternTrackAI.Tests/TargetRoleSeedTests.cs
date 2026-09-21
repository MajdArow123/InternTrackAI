using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The shipped role-suggestion seed file. It is data, not code, so nothing stops a bad edit reaching
/// production except these: a key that is not a <see cref="FieldCategory"/> member would silently
/// disappear from the typeahead, and a category with no roles would show a user an empty dropdown with
/// no explanation.
/// </summary>
public class TargetRoleSeedTests
{
    /// <summary>The file as it ships, found next to the test binary via the csproj Content copy.</summary>
    private static string SeedJson()
    {
        var path = Path.Combine(AppContext.BaseDirectory, TargetRoleSeeds.RelativePath);
        Assert.True(File.Exists(path), $"{TargetRoleSeeds.RelativePath} was not copied to the output directory");
        return File.ReadAllText(path);
    }

    private static IReadOnlyDictionary<FieldCategory, IReadOnlyList<string>> Seeds() => TargetRoleSeeds.Parse(SeedJson());

    [Fact]
    public void Every_field_category_has_suggestions()
    {
        var seeds = Seeds();

        var missing = Enum.GetValues<FieldCategory>().Where(c => !seeds.ContainsKey(c)).ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_seeded_list_is_non_empty_and_free_of_duplicates()
    {
        foreach (var (category, roles) in Seeds())
        {
            Assert.NotEmpty(roles);
            Assert.Equal(roles.Count, ProfileTags.Dedupe(roles).Count);
            Assert.All(roles, r => Assert.False(string.IsNullOrWhiteSpace(r), $"{category} has a blank role"));
        }
    }

    [Fact]
    public void Non_technology_categories_do_not_borrow_the_technology_list()
    {
        var seeds = Seeds();

        // The bug this whole feature exists to fix: a nursing user seeing "Backend Developer Intern".
        Assert.Contains("Nursing Student Placement", seeds[FieldCategory.Healthcare]);
        Assert.DoesNotContain(seeds[FieldCategory.Healthcare], r => r.Contains("Developer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(seeds[FieldCategory.Trades], r => r.Contains("Software", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(seeds[FieldCategory.Finance], r => r.Contains("DevOps", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_files_own_comment_key_is_ignored_rather_than_breaking_the_parse()
    {
        // The file documents itself with a "_comment" string. A key that isn't a category is skipped.
        Assert.Contains("_comment", SeedJson());
        Assert.NotEmpty(Seeds());
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"Technology\": \"not an array\"}")]
    [InlineData("{\"Nonsense\": [\"Role\"]}")]
    [InlineData("{\"Technology\": []}")]
    public void Malformed_or_unusable_content_yields_an_empty_map_rather_than_a_wrong_one(string json)
    {
        // Parse may throw on invalid JSON; the service catches that and logs. What must never happen is
        // a map that looks populated but isn't, so assert emptiness wherever parsing does succeed.
        try
        {
            Assert.Empty(TargetRoleSeeds.Parse(json));
        }
        catch (System.Text.Json.JsonException)
        {
            // Acceptable: TargetRoleSeeds' constructor turns this into a warning and an empty map.
        }
    }
}
