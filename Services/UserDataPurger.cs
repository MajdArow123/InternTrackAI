using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// Deletes everything the app stores for a user: applications, notes, generated cover letters,
/// interview prep sessions, uploaded resume/cover-letter versions (rows and files), and optionally
/// the profile row and photo. The Identity user row itself is left alone so callers decide whether
/// to delete the account (account deletion) or keep it (demo reset).
/// </summary>
public class UserDataPurger
{
    private readonly ApplicationDbContext _db;
    private readonly UploadStorage _uploads;
    private readonly ILogger<UserDataPurger> _logger;

    public UserDataPurger(ApplicationDbContext db, UploadStorage uploads, ILogger<UserDataPurger> logger)
    {
        _db = db;
        _uploads = uploads;
        _logger = logger;
    }

    /// <param name="userId">Identity user id whose data is removed.</param>
    /// <param name="keepProfile">When true the <c>UserProfile</c> row and photo survive (demo reset).</param>
    /// <param name="keepActiveDocuments">
    /// When true the currently active resume and active uploaded cover letter (row + file) survive;
    /// all other versions are removed. Used by the demo reset so the sample resume stays in place.
    /// </param>
    public async Task PurgeAsync(string userId, bool keepProfile = false, bool keepActiveDocuments = false)
    {
        // Child rows first so no FK constraint trips on providers that enforce them (Postgres).
        await _db.ApplicationNotes.Where(n => n.UserId == userId).ExecuteDeleteAsync();
        await _db.InterviewPrepSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync();
        await _db.GeneratedCoverLetters.Where(c => c.UserId == userId).ExecuteDeleteAsync();
        await _db.JobApplications.Where(a => a.UserId == userId).ExecuteDeleteAsync();

        var resumes = await _db.ResumeVersions.Where(r => r.UserId == userId).ToListAsync();
        var letters = await _db.CoverLetterVersions.Where(c => c.UserId == userId).ToListAsync();

        if (keepActiveDocuments)
        {
            resumes = resumes.Where(r => !r.IsActive).ToList();
            letters = letters.Where(c => !c.IsActive).ToList();
        }

        foreach (var r in resumes) TryDeleteFile(r.StoredPath);
        foreach (var c in letters) TryDeleteFile(c.StoredPath);
        _db.ResumeVersions.RemoveRange(resumes);
        _db.CoverLetterVersions.RemoveRange(letters);

        if (!keepProfile)
        {
            var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId);
            if (profile != null)
            {
                if (!string.IsNullOrEmpty(profile.PhotoFileName))
                    TryDeleteFile(Path.Combine(_uploads.PhotosDirectory, profile.PhotoFileName), absolute: true);
                _db.UserProfiles.Remove(profile);
            }
        }

        await _db.SaveChangesAsync();

        // Once the last version is gone the per-user folders are empty; remove them so the
        // uploads volume doesn't accumulate one empty directory per deleted account.
        if (!keepActiveDocuments)
        {
            TryDeleteDirectory(Path.Combine(_uploads.Root, "resumes", userId));
            TryDeleteDirectory(Path.Combine(_uploads.Root, "coverletters", userId));
        }
    }

    private void TryDeleteFile(string path, bool absolute = false)
    {
        try
        {
            if (absolute) { if (File.Exists(path)) File.Delete(path); }
            else _uploads.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not delete upload {Path}", path);
        }
    }

    private void TryDeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove directory {Dir}", dir);
        }
    }
}
