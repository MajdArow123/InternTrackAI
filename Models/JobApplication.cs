using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models;

/// <summary>
/// Core entity representing a single internship/job application tracked by a user.
/// Owned by <see cref="UserId"/> (the ASP.NET Identity user id). This is the only
/// table the app's main tracking features (CRUD, dashboard, AI features) read and write.
/// </summary>
public class JobApplication
{
    public int Id { get; set; }

    [Required]
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

    // Full original job posting text as entered/pasted by the user. The AI Job Analyzer
    // only reads this to extract structured fields — it must never overwrite it with a
    // summary, or the user's source text would be lost (see Create.cshtml analyzer flow).
    [DataType(DataType.MultilineText)]
    public string? JobDescription { get; set; }

    // Null until the user has run a resume match against this application (no resume
    // uploaded yet, or analysis never triggered). 0-100 once populated.
    public int? MatchScore { get; set; }

    // Tier label produced by the AI match (e.g. "APPLY", "MAYBE", "SKIP") — see the TIERS
    // table in Create.cshtml/Index.cshtml for the score-range-to-label mapping.
    [StringLength(50)]
    public string? MatchRecommendation { get; set; }

    // Free-text AI-generated explanation of the match score, shown alongside the score.
    public string? MatchSummary { get; set; }

    // Skills the AI found in both the resume and the job description, stored as a
    // JSON-serialized string array rather than a normalized join table. This mirrors the
    // existing SkillsJson/TargetRolesJson pattern on UserProfile — for a small, per-record
    // list that's only ever read/written as a whole (never queried or joined on
    // individually), a JSON column avoids the overhead of a separate table + FK while
    // keeping the schema simple.
    public string? MatchingSkillsJson { get; set; }

    // Skills the job description requires that the AI did not find in the resume.
    // Same JSON-column rationale as MatchingSkillsJson.
    public string? MissingSkillsJson { get; set; }

    // Position of the card within its status column on the Kanban board (0-based). Rows that
    // were never dragged keep the default 0 and fall back to newest-applied-first ordering.
    public int BoardOrder { get; set; }

    // ── Reminder fields (see Services/ReminderService.cs for the rules that read them) ──

    // Scheduled interview as a UTC instant. The Create/Edit forms read and write it in the user's
    // profile time zone (UserClock converts both ways); every display goes through UserClock too.
    [DataType(DataType.DateTime)]
    public DateTime? InterviewAt { get; set; }

    // Explicit follow-up date, stored as midnight of that day in the user's zone (a UTC instant).
    // While it is in the future the follow-up reminder stays quiet ("Snooze" sets it to today + 3);
    // once it passes, the normal follow-up rule applies again.
    [DataType(DataType.Date)]
    public DateTime? FollowUpAt { get; set; }

    // When the user last reached out ("Mark contacted"), UTC. Restarts the follow-up clock.
    public DateTime? LastContactAt { get; set; }

    // ── Resume used ──

    // Which resume version this application was sent with (see Services/ResumeAnalyticsService.cs).
    // Defaults to the user's active resume when the application is created and can be changed on
    // Edit. Nullable with ON DELETE SET NULL (ApplicationDbContext.OnModelCreating): deleting a
    // resume never deletes an application, the link just goes away.
    public int? ResumeVersionId { get; set; }

    [ForeignKey(nameof(ResumeVersionId))]
    public ResumeVersion? ResumeVersion { get; set; }
}
