using System.Text.RegularExpressions;

namespace InternTrackAI.Services;

/// <summary>
/// Drops a "strength" that credits the candidate with nothing they actually said.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a mechanism and not just a prompt rule.</b> CLAUDE.md §8 records the lesson twice over: the
/// practice generator's exclusion list was measured to be ignored, and the fix that worked was
/// <see cref="TopicKey"/> — a comparison, not an instruction. The same applies here. The prompt is told
/// not to invent praise; this is what makes it true, in the shape of
/// <see cref="ResumeRewriteService"/>'s <c>InventsOutcome</c> and <c>InventsEmptyNoun</c> guards.
/// </para>
/// <para>
/// <b>The observed failure.</b> A junk answer to a linear-vs-non-linear question — four sentences, no
/// specifics — scored 3/5 and was credited with "acknowledged that both approaches have their
/// advantages and disadvantages". That is praise for a non-statement: it would be true of any answer to
/// any comparison question, including one that said nothing at all. The tell is that the sentence
/// survives with its subject removed.
/// </para>
/// <para>
/// <b>Deliberately narrow.</b> This catches the family of the observed failure — "both approaches have
/// pros and cons", "shows an understanding of the topic", "demonstrates awareness" — and nothing
/// cleverer. A strength naming something the candidate said keeps whatever else it contains, so
/// "You weighed the trade-offs and named the deadline you missed" survives the phrase "trade-offs"
/// entirely, because the guard fires only when the <em>whole</em> strength is filler. Erring toward
/// keeping is the right bias: a dropped real strength is a worse failure than a surviving weak one, and
/// the floor below keeps the card from emptying out either way.
/// </para>
/// </remarks>
public static class EmptyPraise
{
    /// <summary>
    /// Phrases that say nothing about what the candidate said. Each is true of any answer to any
    /// question in its category, which is exactly what makes them worthless as feedback.
    /// </summary>
    public static readonly string[] Platitudes =
    {
        "advantages and disadvantages",
        "pros and cons",
        "benefits and drawbacks",
        "strengths and weaknesses of both",
        "both approaches",
        "both methods",
        "both options",
        "there is no one size fits all",
        "it depends on the context",
        "shows an understanding",
        "shows understanding",
        "demonstrates an understanding",
        "demonstrates understanding",
        "demonstrates awareness",
        "shows awareness",
        "shows familiarity",
        "general understanding",
        "basic understanding",
        "high level understanding",
        "touches on the main points",
        "covers the basics",
        "addresses the question",
        "answers the question",
        "stays on topic",
        "is well structured",
        "clear and concise",
        "easy to follow",
        "good starting point",
        "reasonable attempt",
        "willingness to learn",
        "positive attitude",
    };

    private static readonly Regex NonAlphanumeric = new(@"[^a-z0-9\s]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Words that carry no claim about the answer, so a strength made only of these plus a platitude is
    /// made of nothing. Deliberately excludes anything that could name a subject.
    /// </summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "you", "your", "yours", "the", "a", "an", "and", "or", "but", "that", "this", "these", "those",
        "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did",
        "it", "its", "they", "them", "their", "there", "here", "of", "to", "in", "on", "at", "for",
        "with", "by", "from", "as", "about", "into", "over", "under", "between", "both", "each",
        "some", "any", "all", "well", "good", "nice", "clear", "clearly", "correct", "correctly",
        "right", "fine", "solid", "strong", "nicely", "answer", "answered", "answering", "response",
        "question", "topic", "point", "points", "made", "make", "makes", "given", "give", "gives",
        "show", "shows", "showed", "showing", "mention", "mentions", "mentioned", "mentioning",
        "acknowledge", "acknowledges", "acknowledged", "acknowledging", "note", "notes", "noted",
        "recognise", "recognises", "recognised", "recognize", "recognizes", "recognized",
        "understand", "understands", "understood", "understanding", "aware", "awareness",
        "explain", "explains", "explained", "explaining", "describe", "describes", "described",
        "demonstrate", "demonstrates", "demonstrated", "demonstrating",
        "cover", "covers", "covered", "covering", "touch", "touches", "touched", "touching",
        "identify", "identifies", "identified", "identifying", "consider", "considers", "considered",
        "approach", "approaches", "method", "methods", "option", "options", "way", "ways",
        "advantage", "advantages", "disadvantage", "disadvantages", "pro", "pros", "con", "cons",
        "general", "generally", "basic", "basics", "overall", "attempt", "effort", "trying", "try",
    };

    /// <summary>
    /// True when a strength credits the candidate with nothing. Either it contains a known platitude
    /// and no word of its own, or it is made entirely of filler.
    /// </summary>
    public static bool IsEmpty(string? strength)
    {
        if (string.IsNullOrWhiteSpace(strength)) return true;

        var normalised = Whitespace.Replace(NonAlphanumeric.Replace(strength.ToLowerInvariant(), " "), " ").Trim();
        if (normalised.Length == 0) return true;

        // Everything left once the platitudes are removed. A strength that names something of its own
        // keeps that word here and survives.
        var remainder = normalised;
        foreach (var phrase in Platitudes)
            remainder = remainder.Replace(phrase, " ", StringComparison.Ordinal);

        return Whitespace.Split(remainder)
            .Where(w => w.Length > 0)
            .All(Filler.Contains);
    }

    /// <summary>
    /// Keeps the strengths that say something. <b>Returns an empty list rather than a fallback</b> when
    /// none do: the prompt is told to return no strengths instead of inventing one, and a card that
    /// omits "What worked" is telling the truth about an answer that had nothing working in it.
    /// </summary>
    public static IReadOnlyList<string> Filter(IEnumerable<string> strengths) =>
        strengths.Where(s => !IsEmpty(s)).ToList();
}
