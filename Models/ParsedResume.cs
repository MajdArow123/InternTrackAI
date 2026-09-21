namespace InternTrackAI.Models;

/// <summary>
/// One AI parse of a resume, held as a <b>draft</b> until the user confirms it on
/// <c>/Profile/ReviewResume</c>. Nothing here has touched the profile.
/// </summary>
/// <remarks>
/// <para>
/// This row is the whole point of the review step. The app used to merge the model's output into
/// the profile the moment an upload finished, with a toast as the only notice — so an invented
/// skill landed on a real profile and stayed there. Parking the parse here first means the user
/// sees every skill, and the evidence behind it, before a single value is written.
/// </para>
/// <para>
/// <see cref="RawJson"/> is the model's reply exactly as it came back, kept for debugging a bad
/// parse after the fact. It holds fields the profile has no column for — education, the summary —
/// which the review screen shows read-only. Only the most recent
/// <see cref="Services.ResumeParseService.MaxDraftsPerUser"/> rows per user are kept.
/// </para>
/// </remarks>
public class ParsedResume
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// The resume version this came from, or null when the version has since been deleted. Not a
    /// foreign key — no app table has one (see CLAUDE.md §7) — so a deleted version leaves the
    /// draft readable rather than cascading it away mid-review.
    /// </summary>
    public int? ResumeVersionId { get; set; }

    /// <summary>The model's reply verbatim. Never rendered as HTML; parsed through the same reader the extractor uses.</summary>
    public string RawJson { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>Set once the user has confirmed this draft; an applied draft is history, never re-shown for review.</summary>
    public bool Applied { get; set; }

    /// <summary>How much resume text went in — the one number worth having when a parse comes back thin.</summary>
    public int CharactersExtracted { get; set; }
}
