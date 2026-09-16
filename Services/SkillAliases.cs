namespace InternTrackAI.Services;

/// <summary>
/// Obvious spellings of the same skill or technology, shared by <see cref="SkillGapService"/> (which merges
/// bars on the dashboard) and <see cref="KeywordCoverageService"/> (which treats them as covered wording).
///
/// Keep it small and unambiguous: a false merge ("React" into "React Native") hides a real gap, which is worse
/// than a duplicate bar. To extend, add every spelling — the canonical one included — pointing at the same
/// display name.
/// </summary>
public static class SkillAliases
{
    /// <summary>Canonical display name → every spelling that means it, canonical included.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> Groups = new Dictionary<string, string[]>
    {
        ["Node.js"]    = new[] { "node", "node.js", "nodejs", "node js" },
        ["PostgreSQL"] = new[] { "postgres", "postgresql" },
        ["CI/CD"]      = new[] { "ci/cd", "cicd", "ci cd", "ci-cd" },
        ["Kubernetes"] = new[] { "kubernetes", "k8s" },
        ["JavaScript"] = new[] { "javascript", "js" },
        ["TypeScript"] = new[] { "typescript", "ts" },
        ["Go"]         = new[] { "go", "golang" },
        ["React"]      = new[] { "react", "react.js", "reactjs" },
        ["C#"]         = new[] { "c#", "csharp" },
        [".NET"]       = new[] { ".net", "dotnet" },
        ["AWS"]        = new[] { "aws", "amazon web services" },
        ["REST APIs"]  = new[] { "rest", "rest api", "rest apis", "restful api", "restful apis" },
    };

    /// <summary>Every spelling, lower-cased, mapped to its canonical display name.</summary>
    public static readonly IReadOnlyDictionary<string, string> Map = Build();

    /// <summary>The canonical display spelling of a term, or the term unchanged when it has no alias group.</summary>
    public static string Canonical(string term)
        => Map.TryGetValue(term.Trim().ToLowerInvariant(), out var canonical) ? canonical : term;

    /// <summary>
    /// Every lower-cased spelling equivalent to <paramref name="term"/>, including the term itself.
    /// A term with no alias group yields just itself, so callers can always iterate the result.
    /// </summary>
    public static IReadOnlyList<string> Spellings(string term)
    {
        var lower = term.Trim().ToLowerInvariant();
        if (Map.TryGetValue(lower, out var canonical) && Groups.TryGetValue(canonical, out var group))
            return group;
        return new[] { lower };
    }

    private static Dictionary<string, string> Build()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, spellings) in Groups)
        {
            map[canonical.ToLowerInvariant()] = canonical;
            foreach (var s in spellings) map[s] = canonical;
        }
        return map;
    }
}
