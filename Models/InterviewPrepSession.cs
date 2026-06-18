using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InternTrackAI.Models;

/// <summary>
/// Stores the most recent AI-generated interview prep results for one job application.
/// One session per application — regenerating overwrites the previous result rather than
/// creating a new version (unlike resumes/cover letters, which keep full history).
/// </summary>
public class InterviewPrepSession
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    public int JobApplicationId { get; set; }

    // JSON-serialized List<InterviewQuestion> rather than a separate Questions table —
    // the list is only ever read/written as a whole alongside its parent session, so
    // there's no need for a normalized child table.
    [Required] public string QuestionsJson { get; set; } = "[]";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    [ForeignKey("JobApplicationId")]
    public JobApplication? JobApplication { get; set; }
}
