using System.Text.Json;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>
/// The target-role suggestions behind the profile page's role combobox, keyed by
/// <see cref="FieldCategory"/>. Replaces the hardcoded software-engineering array that used to sit
/// in <c>wwwroot/js/profile.js</c>, which gave a nursing or accounting user a list of jobs they will
/// never apply to.
/// </summary>
/// <remarks>
/// <para>
/// The list lives in <c>Data/Seeds/target-roles.json</c> rather than a C# literal so it can be
/// edited without a rebuild; the file is a csproj <c>Content</c> item copied to the output
/// directory, and <c>dotnet publish</c> carries it into the Docker image.
/// </para>
/// <para>
/// Registered as a singleton and read once at construction. A missing or malformed file leaves the
/// map empty and logs a warning — the app must still start, and an empty map degrades to exactly
/// the "no suggestions, free text still works" state an unset category already produces. The seed
/// list is a convenience, never a restriction.
/// </para>
/// </remarks>
public class TargetRoleSeeds
{
    public const string RelativePath = "Data/Seeds/target-roles.json";

    private readonly IReadOnlyDictionary<FieldCategory, IReadOnlyList<string>> _byCategory;

    public TargetRoleSeeds(IWebHostEnvironment env, ILogger<TargetRoleSeeds> logger)
        : this(Path.Combine(env.ContentRootPath, RelativePath), logger) { }

    public TargetRoleSeeds(string path, ILogger<TargetRoleSeeds> logger)
    {
        try
        {
            _byCategory = Parse(File.ReadAllText(path));
            logger.LogInformation("Target-role seeds loaded for {Count} field categories.", _byCategory.Count);
        }
        catch (Exception ex)
        {
            // Never fatal: suggestions are a convenience and free text always works without them.
            logger.LogWarning(ex, "Could not load target-role seeds from {Path}; role suggestions will be empty.", path);
            _byCategory = new Dictionary<FieldCategory, IReadOnlyList<string>>();
        }
    }

    /// <summary>
    /// Parses the seed JSON. Keys that aren't <see cref="FieldCategory"/> members are skipped rather
    /// than throwing — including the file's own <c>_comment</c> key, which is how the file documents
    /// itself without needing a schema.
    /// </summary>
    public static IReadOnlyDictionary<FieldCategory, IReadOnlyList<string>> Parse(string json)
    {
        var map = new Dictionary<FieldCategory, IReadOnlyList<string>>();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) continue;
            if (!Enum.TryParse<FieldCategory>(property.Name, ignoreCase: true, out var category)) continue;
            if (!Enum.IsDefined(category)) continue;

            var roles = ProfileTags.Dedupe(property.Value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()));

            if (roles.Count > 0) map[category] = roles;
        }

        return map;
    }

    /// <summary>Suggestions for one category; empty when the category is unknown or unseeded.</summary>
    public IReadOnlyList<string> For(FieldCategory category) =>
        _byCategory.TryGetValue(category, out var roles) ? roles : Array.Empty<string>();

    /// <summary>
    /// The whole map, keyed by category name, for the JSON island the profile page renders. The view
    /// ships every category rather than just the current one so the combobox can follow the field
    /// dropdown without a page reload.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> All() =>
        _byCategory.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value);
}
