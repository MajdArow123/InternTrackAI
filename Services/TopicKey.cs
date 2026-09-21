using System.Text.RegularExpressions;

namespace InternTrackAI.Services;

/// <summary>
/// Decides whether two practice-question <em>topics</em> are the same subject. Separate from
/// <see cref="QuestionHash"/>, which does the same job for question prompts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not reuse QuestionHash.</b> Its stopword list is tuned for prompts — what, how, why,
/// explain, describe — and topics are noun phrases where none of those appear. The words that make two
/// topics look different while meaning the same thing are connectives and filler nouns: "vs",
/// "between", "for", "strategies", "techniques", "implementation". Running a prompt normaliser over a
/// noun phrase strips nothing and collides nothing.
/// </para>
/// <para>
/// <b>Why token-sorting works here and not on prompts.</b> A live run produced
/// "ORM vs raw SQL" and then "Raw SQL vs ORM" — a genuine reordering of identical words, which is
/// exactly what sorting catches. Prompts repeat by paraphrase, where the words themselves change;
/// topics repeat by reordering and by padding.
/// </para>
/// <para>
/// <b>Equality is not enough.</b> Of the four collisions that run produced, two are equal after
/// normalising ("SQL query optimization" / "…optimization techniques") and two are <em>subsets</em>
/// ("Rate limiting implementation" ⊂ "API rate limiting", "Caching strategies" ⊂ "Caching strategies
/// for database queries"). So <see cref="Collides"/> tests containment in either direction.
/// </para>
/// <para>
/// <b>Known cost, accepted.</b> Subset matching means a short topic blocks its own supersets:
/// once "caching" is stored, "caching invalidation" is rejected even though it is a fair question.
/// That is the same bargain <see cref="QuestionHash"/> makes — on a practice page a repeat is more
/// annoying than a gap, the generator over-requests to absorb the loss, and coming up short is
/// reported as a note rather than an error. The prompt separately forbids topics that broad.
/// </para>
/// </remarks>
public static class TopicKey
{
    /// <summary>
    /// Connectives and filler nouns that pad a topic without changing its subject. Tuned for noun
    /// phrases, and deliberately short — "database", "API" and "SQL" are subjects, not filler, and
    /// adding them here would collapse topics that genuinely differ.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "vs", "versus", "between", "and", "for", "in", "the",
        "strategies", "strategy", "techniques", "technique", "implementation", "implementations"
    };

    private static readonly Regex NonAlphanumeric = new(@"[^a-z0-9\s]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The significant tokens of a topic, sorted. Public so a failing test can show its working.</summary>
    public static string[] Tokens(string? topic)
    {
        if (string.IsNullOrWhiteSpace(topic)) return Array.Empty<string>();

        var cleaned = NonAlphanumeric.Replace(topic.ToLowerInvariant(), " ");

        return Whitespace.Split(cleaned)
            .Where(t => t.Length > 0 && !Stopwords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The sorted token string — a readable key, unlike a hash.</summary>
    public static string Of(string? topic) => string.Join(' ', Tokens(topic));

    /// <summary>
    /// True when two topics are the same subject: identical after normalising, or one's tokens
    /// contained in the other's. A topic with no significant tokens never collides — an empty key
    /// would otherwise be a subset of everything and block the lot.
    /// </summary>
    public static bool Collides(string? a, string? b)
    {
        var left = Tokens(a);
        var right = Tokens(b);
        if (left.Length == 0 || right.Length == 0) return false;

        var (smaller, larger) = left.Length <= right.Length
            ? (left, new HashSet<string>(right, StringComparer.Ordinal))
            : (right, new HashSet<string>(left, StringComparer.Ordinal));

        return smaller.All(larger.Contains);
    }

    /// <summary>True when <paramref name="topic"/> collides with anything already seen.</summary>
    public static bool CollidesWithAny(string? topic, IEnumerable<string> known) =>
        known.Any(k => Collides(topic, k));
}
