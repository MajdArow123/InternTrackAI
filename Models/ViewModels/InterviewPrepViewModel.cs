namespace InternTrackAI.Models.ViewModels;

public class InterviewPrepViewModel
{
    public JobApplication Application { get; set; } = null!;
    public InterviewPrepSession? Session { get; set; }
    public List<InterviewQuestion> Questions { get; set; } = new();
}
