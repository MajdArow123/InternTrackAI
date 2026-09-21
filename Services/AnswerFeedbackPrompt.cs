using System.Text;
using System.Text.RegularExpressions;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>Everything the grader is told about the question being answered.</summary>
/// <param name="Question">The prompt as asked.</param>
/// <param name="Answer">What the candidate typed. <b>Untrusted text.</b></param>
/// <param name="ModelHint">The bullets the generator produced for what a strong answer covers, if any.</param>
public sealed record AnswerContext(
    string Question,
    string Answer,
    QuestionCategory Category,
    PracticeDifficulty Difficulty,
    string? ModelHint = null,
    string? Company = null,
    string? Role = null);

/// <summary>
/// The answer-grading prompt, built in one place so it can be read, tested and printed without making a
/// model call — the same arrangement as <see cref="PracticePrompt"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer box is the app's most inviting injection surface.</b> It is a large free-text field
/// whose contents go straight into a prompt, and the reward for a successful injection is immediate and
/// obvious: a 5. So the answer and the question both travel inside tagged sections with
/// <see cref="PromptData.DataRule"/> in the system prompt, and tag look-alikes are stripped from the
/// text before it goes in — the same treatment <see cref="FollowUpService"/> and
/// <see cref="ResumeRewriteService"/> give their inputs.
/// </para>
/// <para>
/// <b><see cref="ScoringRule"/> is verbatim and deliberate.</b> The lesson from
/// <see cref="PracticePrompt.DifficultyRule"/> applies to scales as well as to difficulty: told only to
/// "score 1-5", a model treats the number as a politeness dial and returns 4 for everything. Each band
/// therefore says what has to be *true* of an answer to earn it, and the rule names the failure
/// explicitly.
/// </para>
/// </remarks>
public static class AnswerFeedbackPrompt
{
    private static readonly string[] DataTags = { "question", "candidate_answer", "strong_answer_covers" };
    private static readonly Regex TagLookAlike = PromptData.TagPattern(DataTags.Append("instruction"));

    private const int MaxQuestionChars = 600;
    private const int MaxAnswerChars = 4000;
    private const int MaxHintChars = 500;

    /// <summary>What each score has to be true of. Public so a test can pin it against a well-meaning edit.</summary>
    public const string ScoringRule =
        "SCORE, 1-5. Score what was actually said, not how hard they tried:\n" +
        "- 1 — does not answer the question, or is wrong on the substance.\n" +
        "- 2 — gestures at the right area but stays vague: no specifics, no example, or a serious gap.\n" +
        "- 3 — correct and relevant, but generic. The kind of answer anyone who had read about the topic " +
        "could give. No concrete detail of their own.\n" +
        "- 4 — correct, specific, and grounded in a real example or real detail. An interviewer would be " +
        "satisfied. Something is still missing or unpolished.\n" +
        "- 5 — complete and specific, covers the trade-offs or the outcome, and would stand out against " +
        "other candidates.\n" +
        "Most real answers are a 2 or a 3. Do not give a 4 for an answer with no specifics in it, and do " +
        "not soften the score to be kind — a score that is always 4 tells the candidate nothing.";

    /// <summary>
    /// The rule that actually moves the score, and the only prompt change here that <b>measured</b> as
    /// working. Kept separate from <see cref="ScoringRule"/> because it is a different kind of
    /// instruction: it names a condition the model can <em>check</em> rather than a standard it must
    /// judge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured 2026-09-21, 6 real calls (`LiveScoringProbe`).</b> A junk answer from the production
    /// smoke test — four fluent sentences, no specifics — scored <b>3/5</b> under the old prompt.
    /// Rewriting the prose around the 2/3 boundary ("find the specific thing that earns a 3", "most
    /// answers are a 2 or a 3") left it at <b>3/5</b>: no movement at all. This rule dropped it to
    /// <b>2/5</b> while a genuinely specific answer stayed at <b>4/5</b> — stricter, not merely harsher,
    /// which is the distinction only a real call can settle.
    /// </para>
    /// <para>
    /// <b>The generalisable lesson, and it refines CLAUDE.md §8.</b> The dedupe runs concluded that
    /// prompts do not steer. This is sharper: prompts asking for <em>judgement</em> ("is this generic?")
    /// do not land, while prompts naming a <em>checkable condition</em> ("is there a number, a date, a
    /// named tool, a situation that happened?") do. Before writing more prose at a model, ask whether
    /// the instruction can be reduced to something it can look for.
    /// </para>
    /// <para>
    /// It is appended <b>last</b>, after the JSON contract, because that is where it was measured. Do not
    /// tidy it into <see cref="ScoringRule"/> without re-running the probe.
    /// </para>
    /// </remarks>
    public const string ConcreteAnchorRule =
        "HARD RULE, apply before anything else. Scan the answer for a CONCRETE ANCHOR: a number, " +
        "a date or duration, a named tool/method/policy/product, a named role or person, or a specific " +
        "situation the candidate says actually happened to them. " +
        "If there is NO concrete anchor, the score is AT MOST 2, no matter how fluent, balanced or " +
        "correct the answer is. State which anchor you found, or that you found none, as the first " +
        "item in missingPoints.";

    public static string SystemPrompt =>
        "You are an experienced interviewer in the candidate's own field, scoring a practice answer. The " +
        "candidate may work in ANY field — technology, healthcare, trades, business, education, design, " +
        "law, or anything else. Judge the answer against the standards of their field, not against " +
        "software interviewing.\n\n" +
        PromptData.DataRule(DataTags, "the score and feedback described here") + "\n\n" +
        "Return ONLY a valid JSON object. No markdown, no code fences, no commentary.";

    public static string Build(AnswerContext ctx, string? profileContext = null)
    {
        var sb = new StringBuilder();

        sb.Append(UserContextBuilder.Prefix(profileContext));

        sb.Append("Score this practice answer and give feedback the candidate can act on.\n\n");

        if (!string.IsNullOrWhiteSpace(ctx.Company) || !string.IsNullOrWhiteSpace(ctx.Role))
        {
            var role    = Clean(ctx.Role, 120);
            var company = Clean(ctx.Company, 120);
            if (role.Length == 0) role = "a role";
            if (company.Length == 0) company = "a company";
            sb.Append("They are practising for ").Append(role).Append(" at ").Append(company).Append(".\n\n");
        }

        sb.Append($"This is a {ctx.Difficulty} {CategoryLabel(ctx.Category)} question.\n\n");

        sb.Append(PromptData.Section("question", Clean(PromptData.OneLine(ctx.Question), MaxQuestionChars))).Append("\n\n");
        sb.Append(PromptData.Section("candidate_answer", Clean(ctx.Answer, MaxAnswerChars))).Append("\n\n");

        if (!string.IsNullOrWhiteSpace(ctx.ModelHint))
            sb.Append("What was intended as a strong answer to this question:\n")
              .Append(PromptData.Section("strong_answer_covers", Clean(PromptData.OneLine(ctx.ModelHint), MaxHintChars)))
              .Append("\n\n");

        if (ctx.Category == QuestionCategory.Behavioral)
            sb.Append("This is a behavioural question, so judge the structure too: a strong answer gives the " +
                      "situation, what they were responsible for, what they actually did, and how it turned " +
                      "out. An answer with no outcome in it is incomplete however well it starts.\n\n");

        sb.Append(ScoringRule).Append("\n\n");

        sb.Append("Return this JSON object with NO other text:\n")
          .Append("{\"score\":3,\"strengths\":[\"...\"],\"improvements\":[\"...\",\"...\"],")
          .Append("\"missingPoints\":[\"...\"],\"revisedOpening\":\"...\"}\n\n")
          .Append("- strengths: 1-3 short sentences naming what genuinely works. If nothing does, return an empty list rather than inventing praise.\n")
          .Append($"- improvements: EXACTLY {AnswerFeedback.ImprovementCount}. Each one a specific change to make, not a restatement of the problem.\n")
          .Append("- missingPoints: up to 3 things a strong answer would have covered and this one did not. Empty if nothing is missing.\n")
          .Append("- revisedOpening: rewrite their FIRST ONE OR TWO SENTENCES as a stronger opening, in their own voice and using their own details. Not a model answer to the whole question.\n")
          .Append("- Address the candidate as \"you\". Plain sentences, no markdown, no bullet characters.\n");

        // Last, after the JSON contract, because that is the position it was measured in.
        sb.Append('\n').Append(ConcreteAnchorRule);

        return sb.ToString();
    }

    private static string CategoryLabel(QuestionCategory category) => category switch
    {
        QuestionCategory.Behavioral      => "behavioural",
        QuestionCategory.CompanySpecific => "company-specific",
        _                                => "technical"
    };

    private static string Clean(string? text, int budget) => PromptData.Clean(text, budget, TagLookAlike);
}
