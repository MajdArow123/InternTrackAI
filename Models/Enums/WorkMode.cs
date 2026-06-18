namespace InternTrackAI.Models.Enums;

/// <summary>
/// Work arrangement for a job application. Stored as its underlying int in the database,
/// same caveat as <see cref="ApplicationStatus"/> — member order is significant.
/// </summary>
public enum WorkMode
{
    Remote,
    Hybrid,
    OnSite
}
