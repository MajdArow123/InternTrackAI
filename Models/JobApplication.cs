using System.ComponentModel.DataAnnotations;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models;

public class JobApplication
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]
    public string CompanyName { get; set; } = string.Empty;

    [Required]
    [StringLength(100)]  
    public string RoleTitle { get; set; } = string.Empty;

    [Url]
    public string? JobLink { get; set; }

    [StringLength(100)]
    public string? Location { get; set; }

    public WorkMode WorkMode { get; set; }

    public ApplicationStatus Status { get; set; }

    [DataType(DataType.Date)]
    public DateTime? Deadline { get; set; }

    [DataType(DataType.Date)]
    public DateTime? DateApplied { get; set; }

    [StringLength(50)]
    public string? Salary { get; set; }

    [DataType(DataType.MultilineText)]
    public string? JobDescription { get; set; }
}