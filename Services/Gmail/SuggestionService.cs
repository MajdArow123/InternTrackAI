using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services.Gmail;

/// <summary>What the Accept / Dismiss endpoints hand back to the page so it can patch itself without a reload.</summary>
public sealed record SuggestionOutcome(
    int Id,
    int ApplicationId,
    SuggestionState State,
    ApplicationStatus ApplicationStatus,
    DateTime? InterviewAtUtc,
    int PendingCount,
    string Message);

/// <summary>
/// Reads and resolves <see cref="StatusSuggestion"/> rows for the dashboard card, the drawer, the
/// board dot and the navbar badge. Every method is owner-scoped: a suggestion that is not the
/// caller's (or is no longer pending) is simply "not found". Accepting applies the proposed status,
/// copies the interview time when there is one, and leaves a note on the application so the change
/// is traceable in the timeline.
/// </summary>
public class SuggestionService
{
    public const string NotePrefix = "Status updated from email: ";

    private readonly ApplicationDbContext _db;

    public SuggestionService(ApplicationDbContext db) => _db = db;

    public Task<int> PendingCountAsync(string userId) =>
        _db.StatusSuggestions.CountAsync(s => s.UserId == userId && s.Status == SuggestionState.Pending);

    /// <summary>Pending suggestions with their application, newest email first.</summary>
    public Task<List<StatusSuggestion>> PendingAsync(string userId) =>
        _db.StatusSuggestions.AsNoTracking()
            .Include(s => s.Application)
            .Where(s => s.UserId == userId && s.Status == SuggestionState.Pending && s.Application != null)
            .OrderByDescending(s => s.EmailDate ?? s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

    /// <summary>Pending suggestions grouped by application id (list rows, board cards, drawer).</summary>
    public async Task<Dictionary<int, List<StatusSuggestion>>> PendingByApplicationAsync(string userId) =>
        (await PendingAsync(userId)).GroupBy(s => s.ApplicationId).ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>Applies the suggestion. Null when it is not the caller's or not pending.</summary>
    public async Task<SuggestionOutcome?> AcceptAsync(string userId, int id)
    {
        var s = await _db.StatusSuggestions.Include(x => x.Application)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.Status == SuggestionState.Pending);
        if (s?.Application is null || s.Application.UserId != userId) return null;

        var app = s.Application;
        app.Status = s.SuggestedStatus;
        if (s.InterviewAt.HasValue && s.SuggestedStatus == ApplicationStatus.Interview)
            app.InterviewAt = s.InterviewAt;

        s.Status = SuggestionState.Accepted;
        _db.ApplicationNotes.Add(new ApplicationNote
        {
            JobApplicationId = app.Id,
            UserId           = userId,
            Text             = NoteText(s.EmailSubject),
            CreatedAt        = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        return new SuggestionOutcome(s.Id, app.Id, SuggestionState.Accepted, app.Status, app.InterviewAt,
            await PendingCountAsync(userId), $"{app.CompanyName} moved to {app.Status}.");
    }

    /// <summary>Records the dismissal; the application is untouched. Null when not the caller's or not pending.</summary>
    public async Task<SuggestionOutcome?> DismissAsync(string userId, int id)
    {
        var s = await _db.StatusSuggestions.Include(x => x.Application)
            .FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId && x.Status == SuggestionState.Pending);
        if (s?.Application is null || s.Application.UserId != userId) return null;

        s.Status = SuggestionState.Dismissed;
        await _db.SaveChangesAsync();

        return new SuggestionOutcome(s.Id, s.ApplicationId, SuggestionState.Dismissed, s.Application.Status, s.Application.InterviewAt,
            await PendingCountAsync(userId), "Suggestion dismissed.");
    }

    /// <summary>"Status updated from email: Next steps for your application" (subject cut to fit the note column).</summary>
    public static string NoteText(string subject)
    {
        var subj = string.IsNullOrWhiteSpace(subject) ? "(no subject)" : subject.Trim();
        var max  = 2000 - NotePrefix.Length;
        return NotePrefix + (subj.Length > max ? subj[..max] : subj);
    }

    /// <summary>Compact shape for data-* attributes and the drawer (no email body exists to leak; the summary is the AI's sentence).</summary>
    public static object ToClient(StatusSuggestion s, UserClock clock) => new
    {
        id              = s.Id,
        applicationId   = s.ApplicationId,
        status          = (int)s.SuggestedStatus,
        statusName      = s.SuggestedStatus.ToString(),
        confidence      = (int)Math.Round(s.Confidence * 100),
        summary         = s.Summary,
        subject         = s.EmailSubject,
        from            = s.EmailFrom,
        date            = clock.LocalDate(s.EmailDate),
        interviewAt     = clock.LocalDateTime(s.InterviewAt)
    };
}
