namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Backs the Interview Prep page for a single application: the application, and its practice
/// questions ready to render.
/// </summary>
/// <remarks>
/// <see cref="Questions"/> are <see cref="PracticeQuestion"/> rows filtered to this application —
/// the same store the practice page reads, not a separate per-application blob. The page no longer
/// has a "session": regenerating adds questions rather than replacing a set, so the only thing that
/// was ever on the session object and is still needed is the newest question's timestamp.
/// </remarks>
public class InterviewPrepViewModel
{
    public JobApplication Application { get; set; } = null!;
    public List<PracticeQuestion> Questions { get; set; } = new();

    /// <summary>When the newest question here was generated, or null when there are none.</summary>
    public DateTime? GeneratedAt => Questions.Count == 0 ? null : Questions.Max(q => q.CreatedAt);
}
