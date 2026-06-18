namespace InternTrackAI.Models.ViewModels;

/// <summary>
/// Backs the Interview Prep page for a single application: the application itself, the
/// most recently generated session (if any), and the deserialized question list ready
/// for the view to render (avoids the view having to parse <see cref="InterviewPrepSession.QuestionsJson"/> itself).
/// </summary>
public class InterviewPrepViewModel
{
    public JobApplication Application { get; set; } = null!;
    public InterviewPrepSession? Session { get; set; }
    public List<InterviewQuestion> Questions { get; set; } = new();
}
