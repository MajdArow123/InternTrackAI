namespace InternTrackAI.Helpers;

/// <summary>
/// Names the resume an application was sent with, given the user's resume id → display-name map
/// (built once per page by the controller). Keeps the "deleted" wording in one place.
/// </summary>
public static class ResumeDisplay
{
    public const string Deleted = "Resume deleted";

    /// <summary>Null when the application has no resume link; <see cref="Deleted"/> when the link points at a version that no longer exists.</summary>
    public static string? Label(int? resumeVersionId, IDictionary<int, string>? labels)
    {
        if (!resumeVersionId.HasValue) return null;
        return labels != null && labels.TryGetValue(resumeVersionId.Value, out var name) ? name : Deleted;
    }
}
