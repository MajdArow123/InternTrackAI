using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace InternTrackAI.Services;

/// <summary>
/// The dedupe key for a practice question: <see cref="Of"/> turns two ways of asking the same thing
/// into the same hash, so the unique index on <c>(UserId, PromptHash)</c> can reject the second one.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this catches: argument reordering.</b> "Explain the difference between a mutex and a
/// semaphore" and "Explain the difference between a semaphore and a mutex" are the same question in a
/// different order, and sorting the tokens makes them the same key. A model asked repeatedly for more
/// questions on one topic produces this constantly, and a plain string comparison passes every one.
/// </para>
/// <para>
/// <b>What it does not catch: paraphrase.</b> "Explain the difference between a process and a thread"
/// and "How does a thread differ from a process" normalise to <c>and between difference process
/// thread</c> and <c>differ does from process thread</c> — the shared idea is carried by different
/// words, and no amount of sorting reconciles that without stemming and a stopword list broad enough
/// to start colliding genuinely different questions. This is a deliberate boundary, not an oversight:
/// <b>paraphrase is layer 1's job</b>, the topic exclusion list passed into the generator, which
/// prevents a near-duplicate from being written at all rather than catching it afterwards. Hashing is
/// the backstop that makes "no duplicates" enforceable by an index; steering is what makes it rare.
/// </para>
/// <para>
/// The normalisation is still deliberately lossy — it will occasionally collapse two different
/// questions built from the same words — because on a practice page a missed near-duplicate is more
/// annoying than an occasional missing question, and the generator over-requests to absorb the loss.
/// </para>
/// <para>
/// Pure and deterministic: the same prompt always hashes the same, on any machine and any provider.
/// It is not a security hash and nothing depends on it being hard to reverse; SHA-256 is here because
/// it is collision-free in practice, not because it is cryptographic.
/// </para>
/// </remarks>
public static class QuestionHash
{
    /// <summary>
    /// Words that carry no meaning in a question's identity — pure question framing. Exactly the list
    /// the spec fixed, kept deliberately short: every word added here makes two more questions look
    /// alike, and words like "between", "difference" and "time" carry real meaning.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "what", "how", "why", "the", "a", "an", "is", "are", "can", "you", "your",
        "explain", "describe", "tell", "me", "about", "please"
    };

    private static readonly Regex NonAlphanumeric = new(@"[^a-z0-9\s]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The normalised token string a hash is taken of. Public so a test can assert on something
    /// readable — a failing hash comparison tells you nothing on its own.
    /// </summary>
    public static string Normalize(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return string.Empty;

        var cleaned = NonAlphanumeric.Replace(prompt.ToLowerInvariant(), " ");

        var tokens = Whitespace.Split(cleaned)
            .Where(t => t.Length > 0 && !Stopwords.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)   // the step that catches a reordered rephrasing
            .ToList();

        return string.Join(' ', tokens);
    }

    /// <summary>Hex SHA-256 of <see cref="Normalize"/>. Empty in, empty out — a blank prompt has no identity.</summary>
    public static string Of(string? prompt)
    {
        var normalised = Normalize(prompt);
        if (normalised.Length == 0) return string.Empty;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised))).ToLowerInvariant();
    }
}
