using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Models;

/// <summary>
/// One "this email looks like a status change" proposal produced by <c>GmailSyncService</c> for a
/// specific application. Deliberately stores no email body: only the subject, sender, date, the
/// Gmail message id (unique per user, so a message is never classified twice) and the one-sentence
/// AI summary. Accepting applies <see cref="SuggestedStatus"/> (and <see cref="InterviewAt"/>) to
/// the application; dismissing just records the decision. Rows go with their application (cascade).
/// </summary>
public class StatusSuggestion
{
    public int Id { get; set; }

    [Required]
    public int ApplicationId { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gmail's message id. Unique together with <see cref="UserId"/>.</summary>
    [Required, StringLength(64)]
    public string GmailMessageId { get; set; } = string.Empty;

    /// <summary>Only Interview, Offer or Rejected are ever stored (the classifier's other answers are dropped).</summary>
    public ApplicationStatus SuggestedStatus { get; set; }

    /// <summary>Model confidence, clamped to 0..1.</summary>
    public double Confidence { get; set; }

    /// <summary>One sentence from the model describing what the email says.</summary>
    [Required, StringLength(300)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>Interview instant the model found in the email (UTC), applied to the application on Accept.</summary>
    public DateTime? InterviewAt { get; set; }

    [StringLength(300)]
    public string EmailSubject { get; set; } = string.Empty;

    [StringLength(320)]
    public string EmailFrom { get; set; } = string.Empty;

    /// <summary>When the email was received (UTC), from Gmail's internal date.</summary>
    public DateTime? EmailDate { get; set; }

    public SuggestionState Status { get; set; } = SuggestionState.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(ApplicationId))]
    public JobApplication? Application { get; set; }
}
