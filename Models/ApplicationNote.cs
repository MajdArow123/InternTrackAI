using System.ComponentModel.DataAnnotations;

namespace InternTrackAI.Models;

/// <summary>
/// A single free-text note logged against a <see cref="JobApplication"/>, shown as a
/// timestamped activity-timeline entry in the application detail drawer. Notes are
/// append-only — there's no edit/delete UI, since the timeline is meant to read like a log.
/// </summary>
public class ApplicationNote
{
    public int Id { get; set; }

    [Required]
    public int JobApplicationId { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(2000)]
    public string Text { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
