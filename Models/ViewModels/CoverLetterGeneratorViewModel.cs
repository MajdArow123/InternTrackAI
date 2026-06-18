using InternTrackAI.Models;

namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Backs the Cover Letter generator page: the list of applications to generate a letter
/// for, the user's saved letter history, and whether they have a resume on file (the AI
/// generator needs an active resume as context).
/// </summary>
public class CoverLetterGeneratorViewModel
{
    public List<JobApplication> Applications { get; set; } = new();
    public List<GeneratedCoverLetter> SavedLetters { get; set; } = new();
    public int? SelectedApplicationId { get; set; }
    public bool HasActiveResume { get; set; }
}
