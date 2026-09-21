using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>What one submission produced. <see cref="Feedback"/> is non-null whenever <see cref="Success"/> is true.</summary>
public sealed record AnswerSubmissionResult(bool Success, AnswerFeedback? Feedback, string? Error)
{
    public static AnswerSubmissionResult Failed(string error) => new(false, null, error);
    public static AnswerSubmissionResult Ok(AnswerFeedback feedback) => new(true, feedback, null);
}

/// <summary>
/// Submitting an answer: validate, score, and write the attempt. <b>The only path that writes an answer
/// to a <see cref="PracticeQuestion"/>.</b>
/// </summary>
/// <remarks>
/// <para>
/// Both <c>PracticeController.SubmitAnswer</c> and <c>InterviewPrepController.CritiqueAnswer</c> come
/// through here, which is the point of it existing: two pages offering the same thing is fine, two
/// implementations of what "answering a question" means is not.
/// </para>
/// <para>
/// <b><see cref="MinAnswerChars"/> is enforced here, not in the browser.</b> The client disables its
/// button as a courtesy; this check is what stops three words from costing a model call, and it is the
/// only one a scripted request sees.
/// </para>
/// </remarks>
public class PracticeAnswerService
{
    /// <summary>Below this there is nothing to grade — a scored non-answer is worse than being asked to write more.</summary>
    public const int MinAnswerChars = 40;

    /// <summary>Cap on what reaches the prompt, matching what the critique endpoint has always trimmed to.</summary>
    public const int MaxAnswerChars = 4000;

    /// <summary>
    /// Ceiling on a reported answer time. An hour on one interview question is not a measurement, it is
    /// a tab left open, and the value is client-supplied so it is bounded rather than trusted.
    /// </summary>
    public const int MaxAnsweredSeconds = 3600;

    /// <summary>Reads a client-reported duration into something storable: null unless it is plausible.</summary>
    public static int? ElapsedSeconds(int? reported) =>
        reported is { } s && s > 0 ? Math.Min(s, MaxAnsweredSeconds) : null;

    private readonly ApplicationDbContext _db;
    private readonly AnswerFeedbackService _feedback;
    private readonly IUserContextBuilder _userContext;

    public PracticeAnswerService(ApplicationDbContext db, AnswerFeedbackService feedback, IUserContextBuilder userContext)
    {
        _db = db;
        _feedback = feedback;
        _userContext = userContext;
    }

    /// <summary>
    /// The one user-facing length rule. Returns null when the answer is usable.
    /// </summary>
    public static string? Validate(string? answer)
    {
        var trimmed = (answer ?? "").Trim();

        if (trimmed.Length == 0) return "Type an answer first.";
        if (trimmed.Length < MinAnswerChars)
            return $"Write a bit more first — at least {MinAnswerChars} characters, so there's something to give feedback on.";

        return null;
    }

    /// <summary>Trims and caps an answer for storage and for the prompt.</summary>
    private static string Normalize(string answer)
    {
        var trimmed = answer.Trim();
        return trimmed.Length <= MaxAnswerChars ? trimmed : trimmed[..MaxAnswerChars];
    }

    /// <summary>
    /// Scores the answer and records it against <paramref name="row"/>, which the caller has already
    /// confirmed belongs to <paramref name="userId"/>. The row must be tracked.
    /// </summary>
    /// <remarks>
    /// Nothing is written until the model has answered. A failed call leaves the previous attempt exactly
    /// where it was rather than clearing it in anticipation — a user who retries into a quota error
    /// should not also lose the answer they had.
    /// </remarks>
    public async Task<AnswerSubmissionResult> SubmitAsync(
        string userId, PracticeQuestion row, string answer, int? elapsedSeconds = null, CancellationToken ct = default)
    {
        if (Validate(answer) is { } invalid) return AnswerSubmissionResult.Failed(invalid);

        var normalized = Normalize(answer);
        var (company, role) = await ApplicationContextAsync(row, ct);

        var context = new AnswerContext(
            row.Prompt, normalized, row.Category, row.Difficulty, row.ModelHint, company, role);

        var (ok, feedback, rawJson, error) = await _feedback.EvaluateAsync(
            context, await _userContext.BuildAsync(userId, ct), ct);

        if (!ok || feedback is null) return AnswerSubmissionResult.Failed(error ?? "Could not get feedback. Try again.");

        // The attempt being replaced becomes history — before the columns are overwritten, and only
        // when there is one to keep.
        if (!string.IsNullOrWhiteSpace(row.UserAnswer) && row.AnsweredAt is { } previouslyAnsweredAt)
        {
            row.PriorAttemptsJson = AttemptHistory.Append(
                row.PriorAttemptsJson,
                new PriorAttempt(row.UserAnswer, row.Score ?? AnswerFeedback.MinScore, previouslyAnsweredAt));
        }

        row.UserAnswer        = normalized;
        row.AiFeedback        = rawJson;
        row.Score             = feedback.Score;
        row.AnsweredAt        = DateTime.UtcNow;
        row.AnsweredInSeconds = ElapsedSeconds(elapsedSeconds);

        await _db.SaveChangesAsync(ct);

        return AnswerSubmissionResult.Ok(feedback);
    }

    /// <summary>
    /// Scores an answer with nowhere to store it: a question that no longer matches a stored row, which
    /// a stale page can still submit. The user gets coached rather than an error over bookkeeping.
    /// </summary>
    /// <remarks>
    /// Category and difficulty are unknowable here, so the prompt is told Technical/Medium. That is a
    /// worse grading context than <see cref="SubmitAsync"/> gets, which is the honest reason this is the
    /// fallback and not the main path.
    /// </remarks>
    public async Task<AnswerSubmissionResult> EvaluateWithoutStoringAsync(
        string userId, string question, string answer, string? company, string? role, CancellationToken ct = default)
    {
        if (Validate(answer) is { } invalid) return AnswerSubmissionResult.Failed(invalid);

        var context = new AnswerContext(
            question, Normalize(answer), QuestionCategory.Technical, PracticeDifficulty.Medium, null, company, role);

        var (ok, feedback, _, error) = await _feedback.EvaluateAsync(
            context, await _userContext.BuildAsync(userId, ct), ct);

        return ok && feedback is not null
            ? AnswerSubmissionResult.Ok(feedback)
            : AnswerSubmissionResult.Failed(error ?? "Could not get feedback. Try again.");
    }

    /// <summary>The posting a question was generated from, for the prompt's "practising for X at Y" line.</summary>
    private async Task<(string? Company, string? Role)> ApplicationContextAsync(PracticeQuestion row, CancellationToken ct)
    {
        if (row.ApplicationId is not { } appId) return (null, null);

        var app = await _db.JobApplications.AsNoTracking()
            .Where(a => a.Id == appId)
            .Select(a => new { a.CompanyName, a.RoleTitle })
            .FirstOrDefaultAsync(ct);

        return app is null ? (null, null) : (app.CompanyName, app.RoleTitle);
    }
}
