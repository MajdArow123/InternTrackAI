using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InternTrackAI.Models;

/// <summary>
/// An AI-generated cover letter saved by the user, with version history per
/// (user, job application) pair similar to <see cref="ResumeVersion"/>/<see cref="CoverLetterVersion"/>.
/// </summary>
public class GeneratedCoverLetter
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = "";

    // Nullable because the source application can be deleted later — the letter and its
    // CompanyName/RoleTitle snapshot below remain valid even after that happens.
    public int? JobApplicationId { get; set; }

    [Required]
    public string Content { get; set; } = "";

    // Snapshots — preserved even if the linked application is later deleted
    public string? CompanyName { get; set; }
    public string? RoleTitle { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    // Only one letter per user should be true at a time; enforced in application code,
    // not via a DB constraint.
    public bool IsActive { get; set; }
    public int VersionNumber { get; set; }

    [ForeignKey("JobApplicationId")]
    public JobApplication? JobApplication { get; set; }
}
