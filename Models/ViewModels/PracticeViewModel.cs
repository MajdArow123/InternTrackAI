using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Backs <c>/Practice</c>: the questions currently shown, and the two controls above them.
/// </summary>
/// <remarks>
/// <see cref="Difficulty"/> and <see cref="Category"/> have a <b>dual role</b> — they filter what is
/// listed <em>and</em> decide what "Get more" generates. That has to be obvious in the page copy or it
/// reads as a bug when changing a filter also changes what the button produces.
/// </remarks>
public class PracticeViewModel
{
    public List<PracticeQuestion> Questions { get; set; } = new();

    public PracticeDifficulty? Difficulty { get; set; }
    public QuestionCategory? Category { get; set; }

    /// <summary>Every question the user has, ignoring the filters — so an empty filtered list can say which case it is.</summary>
    public int TotalCount { get; set; }

    public bool HasAnyQuestions => TotalCount > 0;
    public bool FilteredToNothing => HasAnyQuestions && Questions.Count == 0;

    /// <summary>What "Get more" will produce, given the current filters. Unset falls back to the generator's defaults.</summary>
    public PracticeDifficulty NextDifficulty => Difficulty ?? PracticeDifficulty.Medium;
    public QuestionCategory NextCategory => Category ?? QuestionCategory.Technical;
}
