namespace InternTrackAI.Models;

/// <summary>
/// One uploaded cover letter file in a user's version history. Multiple versions can
/// exist per user; <see cref="IsActive"/> marks the one currently used as AI context
/// and offered for download by default.
/// </summary>
public class CoverLetterVersion
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;

    // Server-side path under the per-user uploads directory (not publicly served directly —
    // files are streamed back through a controller action that checks ownership).
    public string StoredPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    // Only one version per user should be true at a time; enforced in application code
    // when a new version is set active, not via a DB constraint.
    public bool IsActive { get; set; }
}
