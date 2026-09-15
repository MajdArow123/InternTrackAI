using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace InternTrackAI.Models;

/// <summary>
/// One uploaded resume PDF in a user's version history. Multiple versions can exist per
/// user; <see cref="IsActive"/> marks the one used for AI resume matching/scoring and
/// offered for download by default.
/// </summary>
public class ResumeVersion
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

    // Optional user-facing name ("Backend v2", "Data science"), editable inline on the profile.
    // Null means "use the file name" — see <see cref="DisplayName"/>. Applications link to a
    // version through JobApplication.ResumeVersionId so the dashboard can compare response rates.
    [StringLength(60)]
    public string? Label { get; set; }

    /// <summary>
    /// The PDF's text, extracted once by <see cref="Services.ResumeTextService"/>. The file behind a version
    /// never changes (a new upload creates a new row), so this can never go stale and needs no invalidation.
    /// Null means "not extracted yet" — rows uploaded before this column existed, and uploads whose extraction
    /// failed, are backfilled lazily on first read. Never write an empty string here: that would look extracted
    /// and stop the retry. Always read it through <see cref="Services.ResumeTextService"/>, never directly.
    /// </summary>
    public string? ExtractedText { get; set; }

    /// <summary>What the UI calls this version: the label if set, otherwise the file name without ".pdf".</summary>
    [NotMapped]
    public string DisplayName => string.IsNullOrWhiteSpace(Label)
        ? Path.GetFileNameWithoutExtension(OriginalFileName)
        : Label.Trim();
}
