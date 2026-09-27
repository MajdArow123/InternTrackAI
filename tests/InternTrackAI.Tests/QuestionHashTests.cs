using InternTrackAI.Services;

namespace InternTrackAI.Tests;

/// <summary>
/// The dedupe key. The property that matters is that two ways of asking the same question collapse to
/// one hash — a plain string comparison does not, which is why a model can be asked for "more questions
/// on threads" forever and keep producing the same one.
/// </summary>
public class QuestionHashTests
{
    private static void Same(string a, string b) =>
        Assert.True(QuestionHash.Of(a) == QuestionHash.Of(b),
            $"expected the same hash:\n  {a}\n    -> {QuestionHash.Normalize(a)}\n  {b}\n    -> {QuestionHash.Normalize(b)}");

    private static void Different(string a, string b) =>
        Assert.True(QuestionHash.Of(a) != QuestionHash.Of(b),
            $"expected different hashes but both normalised to \"{QuestionHash.Normalize(a)}\"");

    [Theory]
    // The case that motivates sorting the tokens at all: same words, different order.
    [InlineData("Explain the difference between a mutex and a semaphore",
                "Explain the difference between a semaphore and a mutex")]
    [InlineData("What is the difference between TCP and UDP",
                "What is the difference between UDP and TCP?")]
    [InlineData("Compare arrays and linked lists", "Compare linked lists and arrays")]
    public void Reordered_arguments_hash_the_same(string a, string b) => Same(a, b);

    [Fact]
    public void Paraphrase_is_deliberately_not_caught_here()
    {
        // The spec offered this pair as a token-sorting example, but it is not one: "difference
        // between X and Y" and "Y differ from X" share an idea, not words. Catching it would need
        // stemming plus a stopword list wide enough to start colliding real questions. Layer 1 —
        // the topic exclusion list handed to the generator — is what stops a paraphrase being
        // written at all. This test exists so nobody "fixes" the hash to chase it.
        Different("Explain the difference between a process and a thread",
                  "How does a thread differ from a process?");
    }

    [Theory]
    [InlineData("What is a hash map?", "Explain a hash map")]
    [InlineData("Tell me about a time you missed a deadline", "Describe a time you missed a deadline")]
    [InlineData("Can you explain connection pooling?", "What is connection pooling")]
    public void Question_framing_is_stripped(string a, string b) => Same(a, b);

    [Theory]
    [InlineData("What is a hash map?", "what is a HASH MAP")]
    [InlineData("Explain  SQL   joins", "Explain SQL joins")]
    [InlineData("What is a hash map?", "What is a hash map")]
    public void Case_spacing_and_punctuation_do_not_matter(string a, string b) => Same(a, b);

    [Theory]
    [InlineData("Explain SQL joins", "Explain SQL indexes")]
    [InlineData("What is a hash map?", "What is a linked list?")]
    [InlineData("Describe a conflict with a teammate", "Describe a conflict with a manager")]
    public void Genuinely_different_questions_do_not_collide(string a, string b) => Different(a, b);

    [Fact]
    public void A_blank_prompt_has_no_hash()
    {
        // Empty means "no identity" — the caller drops it rather than storing a row that would then
        // block every other empty-ish prompt through the unique index.
        Assert.Equal("", QuestionHash.Of(""));
        Assert.Equal("", QuestionHash.Of("   "));
        Assert.Equal("", QuestionHash.Of(null));
        Assert.Equal("", QuestionHash.Of("?!  ..."));   // punctuation only
    }

    [Fact]
    public void The_hash_is_stable_across_runs()
    {
        // It is stored in a column and compared against later rows, so it can never depend on
        // process state, culture or hash-code randomisation.
        Assert.Equal(QuestionHash.Of("What is a hash map?"), QuestionHash.Of("What is a hash map?"));
        Assert.Equal(64, QuestionHash.Of("What is a hash map?").Length);   // hex SHA-256
    }

    [Fact]
    public void Normalize_shows_its_working()
    {
        // Public precisely so a failure above is readable rather than two hex strings.
        Assert.Equal("and between difference process thread",
            QuestionHash.Normalize("Explain the difference between a process and a thread"));
    }

    [Fact]
    public void Stopword_stripping_is_deliberately_conservative()
    {
        // Every word added to the stopword list makes two more questions look alike. "between",
        // "difference" and "time" carry meaning and must survive.
        var normalised = QuestionHash.Normalize("What is the difference between a mutex and a semaphore?");

        Assert.Contains("difference", normalised);
        Assert.Contains("between", normalised);
        Assert.Contains("mutex", normalised);
        Assert.Contains("semaphore", normalised);
    }

    // ── Near-duplicates (texts verbatim from docs/measurements/2026-09-26-generator-consolidation.md) ──

    private const string CiCdTechnical = "Can you explain the trade-offs between using GitHub Actions and Azure DevOps for CI/CD pipelines in a cloud environment?";
    private const string CiCdCompany = "Can you explain the trade-offs between using Azure DevOps and GitHub Actions for CI/CD pipelines in the context of a cloud environment?";

    [Fact]
    public void The_measured_cross_category_duplicate_is_a_near_duplicate_but_not_a_hash_match()
    {
        // The case the hash was never going to catch: the words were reordered AND "in the context of" added.
        Assert.NotEqual(QuestionHash.Of(CiCdTechnical), QuestionHash.Of(CiCdCompany));
        var similarity = QuestionHash.Similarity(QuestionHash.Words(CiCdTechnical), QuestionHash.Words(CiCdCompany));
        Assert.True(similarity >= QuestionHash.NearDuplicateThreshold, $"similarity {similarity:0.00}");
        Assert.True(QuestionHash.IsNearDuplicate(QuestionHash.Words(CiCdCompany), new[] { QuestionHash.Words(CiCdTechnical) }));
    }

    [Theory]
    // 0.60 — the highest-scoring pair that is not the same question, allowed through on purpose (CLAUDE.md §12).
    [InlineData("Tell me about a time you faced a significant challenge while working on a team project. How did you handle it?",
                "Describe a time when you faced a significant challenge while working on a project. How did you approach the problem?")]
    // 0.55 — genuinely different questions that share their framing.
    [InlineData("Tell me about a time you had to debug a performance issue in an application. What steps did you take to identify and resolve the problem?",
                "Give an example of a time you had to troubleshoot a production issue. What steps did you take to identify and resolve the problem?")]
    public void Questions_below_the_measured_gap_are_not_near_duplicates(string a, string b)
    {
        var similarity = QuestionHash.Similarity(QuestionHash.Words(a), QuestionHash.Words(b));
        Assert.True(similarity < QuestionHash.NearDuplicateThreshold, $"similarity {similarity:0.00}");
        Assert.False(QuestionHash.IsNearDuplicate(QuestionHash.Words(a), new[] { QuestionHash.Words(b) }));
    }

    [Fact]
    public void A_blank_question_is_never_a_near_duplicate_of_anything() =>
        Assert.False(QuestionHash.IsNearDuplicate(QuestionHash.Words("   "), new[] { QuestionHash.Words("") }));
}
