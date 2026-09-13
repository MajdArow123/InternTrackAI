namespace InternTrackAI.Models.Enums;

/// <summary>
/// Lifecycle of a <see cref="Models.StatusSuggestion"/>. Stored as its underlying int; member order
/// is significant for the same reason as <see cref="ApplicationStatus"/>.
/// </summary>
public enum SuggestionState
{
    Pending,
    Accepted,
    Dismissed
}
