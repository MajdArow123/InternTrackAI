namespace InternTrackAI.Models.Enums;

/// <summary>
/// Pipeline stage of a job application. Declaration order matters: the underlying int
/// values are stored in the database and referenced directly by status filters, badge
/// color lookups, and dashboard stat groupings in the views — reordering these members
/// would silently change the meaning of existing stored data.
/// </summary>
public enum ApplicationStatus
{
    Saved,
    Applied,
    Interview,
    Rejected,
    Offer
}
