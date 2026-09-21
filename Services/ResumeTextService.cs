using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>Why a resume text read did not produce usable text.</summary>
public enum ResumeTextStatus
{
    /// <summary>Text was read and is long enough to be worth sending anywhere.</summary>
    Ok,

    /// <summary>The user has no active resume version.</summary>
    NoResume,

    /// <summary>The row exists but its file is gone from disk.</summary>
    FileMissing,

    /// <summary>PdfPig threw — an encrypted, corrupt or otherwise unreadable PDF.</summary>
    Unreadable,

    /// <summary>The PDF parsed but yielded (almost) nothing, which is what a scanned image PDF does.</summary>
    NoText
}

/// <summary>
/// Result of a resume text read. <see cref="Text"/> is whatever was extracted even when
/// <see cref="Status"/> is <see cref="ResumeTextStatus.NoText"/>, so best-effort callers can use it and
/// callers that report errors to the user can branch on the status instead.
/// </summary>
public sealed record ResumeTextResult(ResumeTextStatus Status, string? Text)
{
    public bool Ok => Status == ResumeTextStatus.Ok;

    public static readonly ResumeTextResult NoResume    = new(ResumeTextStatus.NoResume, null);
    public static readonly ResumeTextResult FileMissing = new(ResumeTextStatus.FileMissing, null);
    public static readonly ResumeTextResult Unreadable  = new(ResumeTextStatus.Unreadable, null);
}

/// <summary>
/// The only way to get a resume's text. Reads <see cref="ResumeVersion.ExtractedText"/> when it is set and
/// otherwise parses the file once — <see cref="ResumeMatcherService.ExtractPdfText"/> for a PDF,
/// <see cref="DocxText.Extract"/> for a .docx, chosen by <see cref="ResumeFileType.FormatOf"/> — and writes
/// the result back, so every later read is a column read regardless of format.
///
/// The text can never go stale: a version's file is immutable (uploading a new resume creates a new row), so
/// there is nothing to invalidate. Switching the active resume simply selects a different row.
///
/// Backfill is idempotent — two requests racing produce the same text, so a duplicate write is harmless — and a
/// failed parse persists nothing, so it is retried next time rather than poisoning the row with an empty string.
/// </summary>
public class ResumeTextService
{
    /// <summary>
    /// Below this many characters the "text" is almost certainly PdfPig finding nothing in a scanned image,
    /// not a real resume. Matches the guard the AI endpoints applied individually before this service existed.
    /// </summary>
    public const int MinUsefulChars = 50;

    /// <summary>
    /// Under this, a file that parsed without error almost certainly has no text layer — a resume
    /// scanned or photographed into a PDF. Deliberately well above <see cref="MinUsefulChars"/>: the
    /// review flow tells the user to re-export as a text-based file, and saying that about a genuinely
    /// short resume would be wrong. OCR is out of scope by decision, not oversight.
    /// </summary>
    public const int LikelyScannedChars = 200;

    private readonly ApplicationDbContext _db;
    private readonly UploadStorage _uploads;
    private readonly ILogger<ResumeTextService> _logger;

    public ResumeTextService(ApplicationDbContext db, UploadStorage uploads, ILogger<ResumeTextService> logger)
    {
        _db = db;
        _uploads = uploads;
        _logger = logger;
    }

    /// <summary>Text of the user's active resume version, extracting and storing it on first use.</summary>
    public async Task<ResumeTextResult> GetActiveAsync(string userId, CancellationToken ct = default)
    {
        var version = await _db.ResumeVersions.AsNoTracking()
            .Where(r => r.UserId == userId && r.IsActive)
            .FirstOrDefaultAsync(ct);

        return version is null ? ResumeTextResult.NoResume : await GetAsync(version, ct);
    }

    /// <summary>
    /// Text of one version, extracting and storing it on first use. The caller is responsible for having
    /// loaded a version it owns; the write-back re-reads the row by id, so a no-tracking caller is fine.
    /// </summary>
    public async Task<ResumeTextResult> GetAsync(ResumeVersion version, CancellationToken ct = default)
    {
        if (version.ExtractedText is not null)
            return Classify(version.ExtractedText);

        if (!_uploads.Exists(version.StoredPath))
            return ResumeTextResult.FileMissing;

        string text;
        try
        {
            await using var fs = File.OpenRead(_uploads.Resolve(version.StoredPath));
            // The format branch lives here rather than at the call sites: this service is the one way
            // to read a resume's text (CLAUDE.md §8), so adding a format must not add a branch anywhere else.
            text = ResumeFileType.FormatOf(version.StoredPath) == ResumeFormat.Docx
                ? DocxText.Extract(fs)
                : ResumeMatcherService.ExtractPdfText(fs);
        }
        catch (Exception ex)
        {
            // Type only: a PDF parser's message can quote document content.
            _logger.LogWarning("Could not extract text from resume version {VersionId} ({Error}).",
                version.Id, ex.GetType().Name);
            return ResumeTextResult.Unreadable;
        }

        // Nothing extracted is a parse that "worked" but found no text layer. Storing "" would look like a
        // successful extraction and stop the retry, so leave the column null and let it try again next time.
        if (!string.IsNullOrWhiteSpace(text))
            await PersistAsync(version, text, ct);

        return Classify(text);
    }

    /// <summary>
    /// Extracts and stores the text of a just-uploaded version so the common path never backfills.
    /// Returns the same result a later read would, and never throws — the upload has already succeeded
    /// by this point and must not be undone by an unreadable PDF.
    /// </summary>
    public Task<ResumeTextResult> StoreAsync(ResumeVersion version, CancellationToken ct = default)
        => GetAsync(version, ct);

    private static ResumeTextResult Classify(string text) =>
        string.IsNullOrWhiteSpace(text) || text.Length < MinUsefulChars
            ? new ResumeTextResult(ResumeTextStatus.NoText, text)
            : new ResumeTextResult(ResumeTextStatus.Ok, text);

    /// <summary>
    /// True when a successful read produced so little text that the file is almost certainly a scan.
    /// Separate from <see cref="ResumeTextStatus"/> because the file is fine and the upload succeeded —
    /// only the parse has nothing useful to work with, and only the parse should say so.
    /// </summary>
    public static bool LooksScanned(ResumeTextResult result) =>
        (result.Text?.Trim().Length ?? 0) < LikelyScannedChars;

    /// <summary>
    /// Writes the text back through a tracked read of the row, so this works whether the caller loaded the
    /// version tracked or not. A row that someone else already backfilled is left alone.
    /// </summary>
    private async Task PersistAsync(ResumeVersion version, string text, CancellationToken ct)
    {
        try
        {
            var row = await _db.ResumeVersions.FirstOrDefaultAsync(r => r.Id == version.Id, ct);
            if (row is null || row.ExtractedText is not null) return;

            row.ExtractedText = text;
            await _db.SaveChangesAsync(ct);
            version.ExtractedText = text;
        }
        catch (Exception ex)
        {
            // The caller still gets its text; the row just stays null and is retried on the next read.
            _logger.LogWarning("Could not store extracted text for resume version {VersionId} ({Error}).",
                version.Id, ex.GetType().Name);
        }
    }
}
