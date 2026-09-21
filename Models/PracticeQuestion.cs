using System.ComponentModel.DataAnnotations.Schema;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models;

/// <summary>
/// One interview practice question, and the user's attempt at it. <b>The app's only question store.</b>
/// </summary>
/// <remarks>
/// <para>
/// Replaces <c>InterviewPrepSession</c>, which held a whole question set as one JSON blob per
/// application and overwrote it on every regenerate. A row per question is what makes the rest of the
/// practice engine possible: you cannot deduplicate, filter by difficulty, or remember an answer to a
/// blob. Questions generated from a posting carry <see cref="ApplicationId"/>; the ones generated from
/// the practice page on its own leave it null.
/// </para>
/// <para>
/// <see cref="UserAnswer"/>, <see cref="AiFeedback"/>, <see cref="Score"/> and
/// <see cref="AnsweredAt"/> are written by answer submission (Phase 4) and are null until then. The
/// columns exist now so that phase needs no migration.
/// </para>
/// </remarks>
public class PracticeQuestion
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    /// <summary>The question as asked.</summary>
    public string Prompt { get; set; } = string.Empty;

    public PracticeDifficulty Difficulty { get; set; } = PracticeDifficulty.Medium;
    public QuestionCategory Category { get; set; } = QuestionCategory.Technical;

    /// <summary>
    /// What the question is about — "hash maps", "handling a missed deadline". Empty until the Step 2
    /// generator supplies it. It is the input to the strongest of the three dedupe layers: the next
    /// generation is told which topics are already covered, which prevents far more repetition than
    /// comparing finished questions ever could.
    /// </summary>
    public string Topic { get; set; } = string.Empty;

    /// <summary>
    /// Normalised dedupe key from <see cref="Services.QuestionHash.Of"/>, unique per user. This column
    /// plus its unique index is the <em>hard</em> guarantee against duplicates — prompt steering is
    /// advisory, an index is not.
    /// </summary>
    public string PromptHash { get; set; } = string.Empty;

    /// <summary>
    /// The application this was generated for, or null for a general practice question. Cascade-deletes
    /// with the application, which is what the prep sessions it replaces did.
    /// </summary>
    public int? ApplicationId { get; set; }

    [ForeignKey(nameof(ApplicationId))]
    public JobApplication? Application { get; set; }

    /// <summary>
    /// How to approach the question. Today this is the one-or-two-sentence tip the prep prompt has
    /// always produced; the spec's richer "ideal answer bullets, revealed after answering" is Phase 4's
    /// use of the same column.
    /// </summary>
    public string? ModelHint { get; set; }

    // ── Phase 4 ──
    public string? UserAnswer { get; set; }
    public string? AiFeedback { get; set; }

    /// <summary>1–5, set when answered.</summary>
    public int? Score { get; set; }

    public DateTime? AnsweredAt { get; set; }

    /// <summary>
    /// The last <see cref="Services.AttemptHistory.Keep"/> superseded attempts, newest first, as JSON —
    /// null until the question has been answered a second time. The <em>current</em> attempt lives in the
    /// four columns above; this is only what it replaced.
    /// </summary>
    /// <remarks>
    /// A nullable text column rather than an attempts table, deliberately: retry history is read only as
    /// part of the question it belongs to and never queried across rows, and a nullable string is the one
    /// migration shape free of all three things that have actually broken on PostgreSQL in this repo
    /// (identity columns, DateTime, bool). Capped for the same reason parsed resume drafts are pruned to
    /// three — an unbounded list in a column is an unbounded column.
    /// </remarks>
    public string? PriorAttemptsJson { get; set; }

    /// <summary>
    /// How long the answer took, first keystroke to submit, in seconds. Null when not reported.
    /// </summary>
    /// <remarks>
    /// <b>Client-supplied and therefore advisory.</b> The browser measures it and the server clamps it
    /// to a sane range (see <c>PracticeAnswerService</c>); nothing depends on it being truthful, and it
    /// is shown as a muted label rather than as a result. Real interviews are timed, so the number is
    /// useful feedback on its own — but it is not a score and must never become one.
    /// </remarks>
    public int? AnsweredInSeconds { get; set; }

    public bool IsSaved { get; set; }
    public DateTime CreatedAt { get; set; }
}
