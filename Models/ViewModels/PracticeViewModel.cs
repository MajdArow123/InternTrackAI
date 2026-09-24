using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// One application's questions on the practice page, plus what the header needs to name and link it.
/// </summary>
/// <remarks>
/// <see cref="ApplicationId"/> null is the "General practice" group — questions generated from the
/// practice page itself rather than from a posting. Company and role are carried as strings because
/// the header needs two labels, not a whole <see cref="JobApplication"/>.
/// </remarks>
public sealed record PracticeGroup(
    int? ApplicationId,
    string? Company,
    string? Role,
    List<PracticeQuestion> Questions)
{
    public bool IsGeneral => ApplicationId is null;

    /// <summary>
    /// True when this is the page's only group. The general group then renders no heading: "General
    /// practice" above the only list on the page labels nothing.
    /// </summary>
    public bool IsOnlyGroup { get; init; }

    /// <summary>The drawer deep link the applications page already honours (CLAUDE.md §9).</summary>
    public string? ApplicationUrl => ApplicationId is { } id ? $"/JobApplications#open-{id}" : null;
}

/// <summary>
/// Backs <c>/Practice</c>: the questions currently shown, the controls above them, and the progress card.
/// </summary>
/// <remarks>
/// <see cref="Difficulty"/> and <see cref="Category"/> have a <b>dual role</b> — they filter what is
/// listed <em>and</em> decide what "Get more" generates. That has to be obvious in the page copy or it
/// reads as a bug when changing a filter also changes what the button produces. <see cref="SavedOnly"/>
/// is a plain filter and has no such second job.
/// </remarks>
public class PracticeViewModel
{
    /// <summary>The filtered questions, grouped by the application they were generated for.</summary>
    public List<PracticeGroup> Groups { get; set; } = new();

    /// <summary>Every question the user has, whatever the filters say. Never filtered — it is progress, not a summary of the current view.</summary>
    public PracticeProgress Progress { get; set; } = PracticeProgress.None;

    public PracticeDifficulty? Difficulty { get; set; }
    public QuestionCategory? Category { get; set; }
    public bool SavedOnly { get; set; }

    /// <summary>Hides questions already scored, so what is left to do is what is on screen.</summary>
    public bool HideAnswered { get; set; }

    /// <summary>Every question the user has, ignoring the filters — so an empty filtered list can say which case it is.</summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// The muted example question for a user with no questions yet, chosen by their field. Null once they
    /// have any question, and null if the seed file could not be read. Never stored and never counted.
    /// </summary>
    public PracticeExample? Example { get; set; }

    /// <summary>How many "Clear unanswered" would actually remove, so the button can say a number rather than a promise.</summary>
    public int ClearableCount { get; set; }

    public int ShownCount => Groups.Sum(g => g.Questions.Count);

    public bool HasAnyQuestions => TotalCount > 0;
    public bool FilteredToNothing => HasAnyQuestions && ShownCount == 0;

    /// <summary>True when a filter is on that the empty state should offer to clear.</summary>
    public bool HasActiveFilter => Difficulty is not null || Category is not null || SavedOnly || HideAnswered;

    /// <summary>What "Get more" will produce, given the current filters. Unset falls back to the generator's defaults.</summary>
    public PracticeDifficulty NextDifficulty => Difficulty ?? PracticeDifficulty.Medium;
    public QuestionCategory NextCategory => Category ?? QuestionCategory.Technical;
}
