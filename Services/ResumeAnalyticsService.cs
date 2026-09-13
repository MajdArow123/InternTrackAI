using System.Globalization;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>One row of the "Resume performance" card: how the applications sent with one resume version fared.</summary>
public sealed record ResumeStats(
    int? ResumeVersionId,   // null for the "No resume" bucket
    string Label,
    int VersionNumber,      // 0 for the "No resume" bucket
    bool IsActive,
    int Sent,
    int Interviews,
    int Offers,
    double? AverageMatchScore)
{
    /// <summary>Applications that got a reply worth counting: currently at Interview or Offer.</summary>
    public int Responses => Interviews + Offers;

    /// <summary>Responses as a percentage of applications sent (0 when nothing was sent).</summary>
    public double ResponseRate => Sent == 0 ? 0 : Responses * 100.0 / Sent;

    public int ResponseRatePercent => (int)Math.Round(ResponseRate, MidpointRounding.AwayFromZero);

    /// <summary>Fewer than <see cref="ResumeAnalyticsService.MinimumSample"/> applications: shown greyed and left out of the takeaway.</summary>
    public bool LowConfidence => Sent < ResumeAnalyticsService.MinimumSample;

    public bool IsNoResumeBucket => ResumeVersionId is null;
}

/// <summary>Everything the dashboard card and the profile list need, computed once per page.</summary>
public sealed class ResumeAnalytics
{
    /// <summary>Rows sorted by response rate (desc), then applications sent (desc), then newest version first.</summary>
    public List<ResumeStats> Rows { get; init; } = new();

    /// <summary>The one-line, non-AI summary shown above the table.</summary>
    public string Takeaway { get; init; } = "";

    /// <summary>How many resume versions the user has (the card only makes sense when there are two or more).</summary>
    public int ResumeCount { get; init; }

    public bool ShowCard => ResumeCount >= 2;

    /// <summary>Per-resume rows keyed by version id, for the profile list's inline stat.</summary>
    public IReadOnlyDictionary<int, ResumeStats> ByResumeId =>
        Rows.Where(r => r.ResumeVersionId.HasValue).ToDictionary(r => r.ResumeVersionId!.Value);
}

/// <summary>
/// Which resume version gets responses. Pure rules in <see cref="Build"/> (unit-tested), plus a
/// DI wrapper that loads the user's resumes and applications.
///
/// Rules: only applications that were actually sent count (status Applied or later — Saved is
/// excluded). There is no status history, so the current status stands in for "ever reached":
/// Interview and Offer both count as a response, Offer additionally as an offer. Rejected counts
/// as sent but not as a response. Applications whose resume was deleted (link cleared) fall into
/// the "No resume" bucket, which only appears when it has at least one application.
/// </summary>
public class ResumeAnalyticsService
{
    /// <summary>Applications a resume needs before its response rate is treated as meaningful.</summary>
    public const int MinimumSample = 5;

    public const string NoResumeLabel   = "No resume";
    public const string NotEnoughData   = "Send at least 5 applications per resume to compare.";

    private readonly ApplicationDbContext _db;

    public ResumeAnalyticsService(ApplicationDbContext db) => _db = db;

    public async Task<ResumeAnalytics> ForUserAsync(string userId)
    {
        var resumes = await _db.ResumeVersions.AsNoTracking().Where(r => r.UserId == userId).ToListAsync();
        var apps    = await _db.JobApplications.AsNoTracking().Where(a => a.UserId == userId).ToListAsync();
        return Build(resumes, apps);
    }

    /// <summary>Pure computation over already-loaded rows (the profile page reuses its own query results).</summary>
    public static ResumeAnalytics Build(IEnumerable<ResumeVersion> resumes, IEnumerable<JobApplication> applications)
    {
        var versions = resumes.ToList();
        var ids      = versions.Select(r => r.Id).ToHashSet();
        var sent     = applications.Where(Counts).ToList();

        var rows = versions
            .Select(r => Row(r.Id, r.DisplayName, r.VersionNumber, r.IsActive, sent.Where(a => a.ResumeVersionId == r.Id)))
            .ToList();

        var unlinked = sent.Where(a => !a.ResumeVersionId.HasValue || !ids.Contains(a.ResumeVersionId.Value)).ToList();
        if (unlinked.Count > 0)
            rows.Add(Row(null, NoResumeLabel, 0, false, unlinked));

        rows = rows
            .OrderByDescending(r => r.ResponseRate)
            .ThenByDescending(r => r.Sent)
            .ThenByDescending(r => r.VersionNumber)
            .ToList();

        return new ResumeAnalytics
        {
            Rows        = rows,
            Takeaway    = Takeaway(rows),
            ResumeCount = versions.Count
        };
    }

    /// <summary>Applied or later. Saved applications were never sent, so they say nothing about the resume.</summary>
    public static bool Counts(JobApplication a) => a.Status != ApplicationStatus.Saved;

    private static ResumeStats Row(int? id, string label, int version, bool active, IEnumerable<JobApplication> apps)
    {
        var list   = apps.ToList();
        var scored = list.Where(a => a.MatchScore.HasValue).Select(a => a.MatchScore!.Value).ToList();
        return new ResumeStats(
            id, label, version, active,
            Sent:              list.Count,
            Interviews:        list.Count(a => a.Status == ApplicationStatus.Interview),
            Offers:            list.Count(a => a.Status == ApplicationStatus.Offer),
            AverageMatchScore: scored.Count > 0 ? scored.Average() : null);
    }

    /// <summary>
    /// "Backend v2 has the best response rate so far (24% across 17 applications)." — only real resume
    /// versions with at least <see cref="MinimumSample"/> applications compete; otherwise the
    /// "send at least 5" nudge.
    /// </summary>
    public static string Takeaway(IEnumerable<ResumeStats> rows)
    {
        var best = rows
            .Where(r => !r.IsNoResumeBucket && !r.LowConfidence)
            .OrderByDescending(r => r.ResponseRate)
            .ThenByDescending(r => r.Sent)
            .FirstOrDefault();

        if (best is null) return NotEnoughData;

        return string.Format(CultureInfo.InvariantCulture,
            "{0} has the best response rate so far ({1}% across {2} application{3}).",
            best.Label, best.ResponseRatePercent, best.Sent, best.Sent == 1 ? "" : "s");
    }
}
