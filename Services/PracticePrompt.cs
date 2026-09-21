using System.Text;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>What the user already has, so the generator can be told not to produce it again.</summary>
/// <param name="Topics">Distinct topics already covered at this difficulty and category, newest first.</param>
/// <param name="Stems">Opening words of recent questions, so the model varies its framing too.</param>
public sealed record PracticeExclusions(IReadOnlyList<string> Topics, IReadOnlyList<string> Stems)
{
    public static readonly PracticeExclusions None = new(Array.Empty<string>(), Array.Empty<string>());
}

/// <summary>
/// The practice-question generator's prompt, built in one place so it can be read, tested and
/// printed without making a model call.
/// </summary>
/// <remarks>
/// <para>
/// <b>Do not expect this prompt to prevent repetition.</b> Two instrumented live runs (2026-09-21)
/// showed the exclusion block below being ignored: the model reused a topic string verbatim while that
/// exact string was in its prompt, and produced the same ORM-vs-raw-SQL question in four of six
/// batches. Dedupe is carried by <see cref="TopicKey"/>, not by this text — see CLAUDE.md §8.
/// </para>
/// <para>
/// <see cref="TopicRule"/> is the part that <em>does</em> work, and it is doing a different job than
/// exclusion. Topic granularity held at every batch across both runs — no "databases"-style labels
/// appeared even at batch six — which is what keeps the stored topics precise enough for
/// <see cref="TopicKey"/> to compare them meaningfully. Keep the worked examples: "be specific" alone
/// reliably produces category names.
/// </para>
/// </remarks>
public static class PracticePrompt
{
    /// <summary>Kept verbatim from the spec: without explicit definitions a model treats difficulty as a tone adjective and returns three near-identical tiers.</summary>
    public static string DifficultyRule(PracticeDifficulty difficulty) => difficulty switch
    {
        PracticeDifficulty.Easy =>
            "EASY — recall and definitions. Answerable in 1-2 sentences by anyone who has completed an " +
            "intro course. No multi-step reasoning.",
        PracticeDifficulty.Hard =>
            "HARD — synthesis, edge cases, and design under constraints. Requires justifying a decision " +
            "against competing constraints or debugging a non-obvious failure.",
        _ =>
            "MEDIUM — application and trade-offs. Requires comparing two approaches or applying a concept " +
            "to a concrete scenario. Typical of a real first-round interview."
    };

    /// <summary>
    /// The topic-granularity rule. Public so a test can assert the examples survive an edit — they are
    /// what make the exclusion list able to exclude anything.
    /// </summary>
    public const string TopicRule =
        "Every question needs a \"topic\": the one specific thing it is about. This is the single most " +
        "important field you produce, because the topics you return become the exclusion list for the " +
        "next batch. A broad topic excludes nothing and the user sees the same question again.\n" +
        "- Too broad, never use: \"databases\", \"algorithms\", \"communication\", \"patient care\", " +
        "\"accounting\", \"teamwork\".\n" +
        "- Right level: \"connection pooling\", \"binary search on a rotated array\", \"telling a client " +
        "about a missed deadline\", \"triage under a mass-casualty protocol\", \"revenue recognition " +
        "timing\", \"disagreeing with a senior colleague's decision\".\n" +
        "- A good test: if you could write ten more different questions on the topic, it is still too broad.\n" +
        "- Two questions in this batch must not share a topic.";

    public static string SystemPrompt =>
        "You are an experienced interviewer in the candidate's field, writing practice questions. The " +
        "candidate may work in ANY field — technology, healthcare, trades, business, education, design, " +
        "law, or anything else. Never assume a technology background, and use the vocabulary of their " +
        "own field.\n\n" +
        "Return ONLY a valid JSON object. No markdown, no code fences, no commentary.";

    /// <summary>
    /// The user message. <paramref name="profileContext"/> is the shared field block (CLAUDE.md §8);
    /// <paramref name="jobDescription"/> is set only when generating for a specific posting.
    /// </summary>
    public static string Build(
        PracticeDifficulty difficulty,
        QuestionCategory category,
        int count,
        PracticeExclusions exclusions,
        string? profileContext = null,
        string? company = null,
        string? role = null,
        string? jobDescription = null)
    {
        var sb = new StringBuilder();

        sb.Append(UserContextBuilder.Prefix(profileContext));

        sb.Append($"Write {count} interview practice questions.\n\n");
        sb.Append("DIFFICULTY: ").Append(DifficultyRule(difficulty)).Append("\n\n");
        sb.Append("CATEGORY: ").Append(CategoryRule(category)).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(company) || !string.IsNullOrWhiteSpace(role))
        {
            sb.Append($"THE ROLE: {role ?? "this role"} at {company ?? "this company"}.\n");
            if (!string.IsNullOrWhiteSpace(jobDescription))
                sb.Append("Derive the questions from the responsibilities and requirements this posting actually lists:\n")
                  .Append(PromptData.Section("job_description", jobDescription)).Append('\n');
            sb.Append('\n');
        }

        sb.Append(TopicRule).Append("\n\n");

        sb.Append("Return this JSON object with NO other text:\n")
          .Append("{\"questions\":[{\"prompt\":\"...\",\"topic\":\"...\",\"modelHint\":[\"...\",\"...\"]}]}\n\n")
          .Append("- prompt: the question as you would ask it out loud.\n")
          .Append("- modelHint: 2-3 short bullets naming what a strong answer covers. Not a model answer.\n")
          .Append($"- Exactly {count} questions, every one at the stated difficulty and category.\n");

        // Dead last, deliberately. In the 2026-09-21 live run the exclusion list sat before the JSON
        // contract and was plainly not being followed — batch 5 reused a topic string verbatim from
        // batch 2 while that exact string was in its prompt. Being the final thing read is the cheapest
        // thing to try before concluding the instruction is simply ignored.
        sb.Append(ExclusionBlock(exclusions));

        return sb.ToString().TrimEnd() + "\n";
    }

    private static string CategoryRule(QuestionCategory category) => category switch
    {
        QuestionCategory.Behavioral =>
            "Behavioral — past experience and judgement. Ask about something the candidate has done, not " +
            "what they would do in the abstract.",
        QuestionCategory.CompanySpecific =>
            "Company-Specific — motivation, fit and what they know about this employer and its work.",
        _ =>
            "Technical — role-specific knowledge in the candidate's own field, whatever that field is: " +
            "clinical questions for a nurse, accounting standards for an accountant, code for a developer. " +
            "Never default to software questions for a non-software role."
    };

    /// <summary>
    /// The exclusion block. Empty when there is nothing covered yet, so a first-time user's prompt is
    /// not padded with headings that say "none".
    /// </summary>
    private static string ExclusionBlock(PracticeExclusions exclusions)
    {
        if (exclusions.Topics.Count == 0 && exclusions.Stems.Count == 0) return "";

        var sb = new StringBuilder();

        if (exclusions.Topics.Count > 0)
        {
            sb.Append("BEFORE YOU ANSWER — these topics are already covered. Do not write a question on any\n")
              .Append("of them, on a reordering of one (\"X vs Y\" and \"Y vs X\" are the same topic), or on one\n")
              .Append("padded with a filler word (\"X\" and \"X techniques\" are the same topic). Rewording is the\n")
              .Append("failure mode here: asking the same thing in different words still counts as a repeat.\n")
              .Append("Check each question you are about to return against this list, and replace any that match.\n")
              .Append(string.Join(", ", exclusions.Topics))
              .Append("\n\n");
        }

        if (exclusions.Stems.Count > 0)
        {
            sb.Append("RECENT QUESTION OPENINGS — vary your framing, do not keep reusing these:\n")
              .Append(string.Join(" | ", exclusions.Stems.Select(s => $"\"{s}…\"")))
              .Append("\n\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// The first few words of a question, for the "vary your framing" list. Enough to show a pattern
    /// ("Explain the difference between…", "Describe a time when…") without pasting whole questions
    /// back into the prompt and paying for them twice.
    /// </summary>
    public static string Stem(string prompt, int words = 5)
    {
        var parts = PromptData.OneLine(prompt).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Take(words));
    }
}
