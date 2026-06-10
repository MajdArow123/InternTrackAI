using InternTrackAI.Models;

namespace InternTrackAI.Models.ViewModels;

public class CoverLetterGeneratorViewModel
{
    public List<JobApplication> Applications { get; set; } = new();
    public List<GeneratedCoverLetter> SavedLetters { get; set; } = new();
    public int? SelectedApplicationId { get; set; }
    public bool HasActiveResume { get; set; }
}
