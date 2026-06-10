using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InternTrackAI.Models;

public class GeneratedCoverLetter
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = "";

    public int? JobApplicationId { get; set; }

    [Required]
    public string Content { get; set; } = "";

    // Snapshots — preserved even if the linked application is later deleted
    public string? CompanyName { get; set; }
    public string? RoleTitle { get; set; }

    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; }
    public int VersionNumber { get; set; }

    [ForeignKey("JobApplicationId")]
    public JobApplication? JobApplication { get; set; }
}
