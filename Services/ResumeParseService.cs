using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>Outcome of one parse attempt: a stored draft to review, or a reason it didn't happen.</summary>
public sealed record ResumeParseResult(bool Success, int? DraftId, string? Error)
{
    public static ResumeParseResult Failed(string error) => new(false, null, error);
    public static ResumeParseResult Ok(int draftId) => new(true, draftId, null);
}

/// <summary>
/// Turns a resume's text into a stored <see cref="ParsedResume"/> draft. <b>This service never writes
/// to the profile</b> — that happens only in <see cref="ProfileAutoFillService"/>, and only after the
/// user confirms the draft on <c>/Profile/ReviewResume</c>.
/// </summary>
/// <remarks>
/// <para>
/// Splitting "parse" from "apply" is the whole shape of this feature. Before, an upload extracted and
/// merged in one step, so the only thing between a hallucinated skill and a real profile was a toast
/// the user had already scrolled past. Now parsing produces a proposal and applying is a separate,
/// explicit act.
/// </para>
/// <para>
/// Rate limiting is the caller's job, as it is for <see cref="ProfileAutoFillService"/>: the upload
/// path takes a permit by hand so the file still saves when the bucket is empty, while the re-parse
/// endpoint sits behind the <c>"ai"</c> policy.
/// </para>
/// </remarks>
public class ResumeParseService
{
    /// <summary>
    /// Drafts kept per user. Old drafts are only ever useful for explaining a bad parse after the
    /// fact, and each one holds a model's reading of a resume — so they are pruned aggressively
    /// rather than accumulating a per-user archive nobody asked for.
    /// </summary>
    public const int MaxDraftsPerUser = 3;

    private readonly ApplicationDbContext _db;
    private readonly IProfileExtractor _extractor;
    private readonly ILogger<ResumeParseService> _logger;

    public ResumeParseService(ApplicationDbContext db, IProfileExtractor extractor, ILogger<ResumeParseService> logger)
    {
        _db = db;
        _extractor = extractor;
        _logger = logger;
    }

    /// <summary>
    /// Parses <paramref name="resumeText"/> and stores the result as a draft awaiting review. The
    /// caller has already decided there is text worth sending (see <see cref="ResumeTextService"/>).
    /// </summary>
    public async Task<ResumeParseResult> ParseAsync(string userId, string resumeText, int? resumeVersionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resumeText))
            return ResumeParseResult.Failed("No readable text found in the resume.");

        var extracted = await _extractor.ExtractAsync(resumeText, ct);
        if (!extracted.Success)
            return ResumeParseResult.Failed(extracted.Error ?? "Could not analyze the resume.");

        var draft = await StoreDraftAsync(userId, extracted.RawJson, resumeText.Length, resumeVersionId, ct);

        _logger.LogInformation("Stored resume parse draft {DraftId}: {Skills} skills, {Roles} roles.",
            draft.Id, extracted.Profile.Skills.Count, extracted.Profile.TargetRoles.Count);

        return ResumeParseResult.Ok(draft.Id);
    }

    /// <summary>
    /// Stores the fixed demo parse without calling the model, so the shared demo account can show the
    /// review screen without spending the owner's API budget. See
    /// <see cref="ProfileExtractorService.DemoParse"/> for why it is a nursing resume.
    /// </summary>
    public async Task<ResumeParseResult> StoreDemoParseAsync(string userId, int? resumeVersionId, int charactersExtracted, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(ToWireShape(ProfileExtractorService.DemoParse()));
        var draft = await StoreDraftAsync(userId, json, charactersExtracted, resumeVersionId, ct);
        return ResumeParseResult.Ok(draft.Id);
    }

    /// <summary>The draft awaiting review for this user, or null when there is none.</summary>
    public Task<ParsedResume?> PendingAsync(string userId, CancellationToken ct = default) =>
        _db.ParsedResumes.AsNoTracking()
            .Where(p => p.UserId == userId && !p.Applied)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>One draft by id, owner-scoped — null for a foreign or missing id, which the caller turns into a 404.</summary>
    public Task<ParsedResume?> ByIdAsync(string userId, int id, CancellationToken ct = default) =>
        _db.ParsedResumes.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);

    private async Task<ParsedResume> StoreDraftAsync(string userId, string rawJson, int characters, int? resumeVersionId, CancellationToken ct)
    {
        var draft = new ParsedResume
        {
            UserId              = userId,
            ResumeVersionId     = resumeVersionId,
            RawJson             = rawJson,
            CreatedAt           = DateTime.UtcNow,
            CharactersExtracted = characters,
            Applied             = false
        };

        _db.ParsedResumes.Add(draft);
        await _db.SaveChangesAsync(ct);

        await PruneAsync(userId, ct);
        return draft;
    }

    /// <summary>
    /// Keeps the newest <see cref="MaxDraftsPerUser"/> rows and deletes the rest, applied or not — an
    /// applied draft is history and an older unapplied one has already been superseded by the draft
    /// that just landed, so neither is worth keeping a resume's contents around for.
    /// </summary>
    private async Task PruneAsync(string userId, CancellationToken ct)
    {
        var stale = await _db.ParsedResumes
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip(MaxDraftsPerUser)
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        _db.ParsedResumes.RemoveRange(stale);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Renders a <see cref="ParsedProfile"/> back into the JSON shape the model produces, so the demo
    /// draft is stored and re-read through exactly the same path as a real one — no second reader to
    /// drift out of step with <see cref="ParsedProfile.FromJson"/>.
    /// </summary>
    private static object ToWireShape(ParsedProfile p) => new
    {
        fullName        = p.FullName,
        field           = p.Field,
        fieldCategory   = p.Category?.ToString(),
        seniority       = p.Seniority?.ToString(),
        yearsExperience = p.YearsExperience,
        location        = p.Location,
        skills          = p.Skills.Select(s => new { name = s.Name, evidence = s.Evidence, confidence = s.Confidence.ToString().ToLowerInvariant() }),
        targetRoles     = p.TargetRoles,
        education       = p.Education.Select(e => new { institution = e.Institution, credential = e.Credential, endDate = e.EndDate }),
        summary         = p.Summary
    };
}
