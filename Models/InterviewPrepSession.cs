using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InternTrackAI.Models;

public class InterviewPrepSession
{
    public int Id { get; set; }
    [Required] public string UserId { get; set; } = "";
    public int JobApplicationId { get; set; }
    [Required] public string QuestionsJson { get; set; } = "[]";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    [ForeignKey("JobApplicationId")]
    public JobApplication? JobApplication { get; set; }
}
