namespace InternTrackAI.Services;

/// <summary>
/// Single source of truth for where user-uploaded files (profile photos, resumes, cover letters)
/// live on disk. The root directory comes from the <c>UPLOADS_PATH</c> setting (environment
/// variable or appsettings key); when unset it falls back to <c>uploads/</c> under the content
/// root, which matches the pre-existing local layout. In production, point <c>UPLOADS_PATH</c>
/// at a mounted persistent volume so files survive redeploys.
///
/// Layout under the root:
/// <code>
///   photos/{userId}.{ext}             — served publicly at /uploads/photos/... (see Program.cs)
///   resumes/{userId}/{guid}.pdf       — private; only served through ProfileController after an ownership check
///   coverletters/{userId}/{guid}.pdf  — private; same as resumes
/// </code>
/// </summary>
public class UploadStorage
{
    public const string ConfigKey = "UPLOADS_PATH";
    private const string DefaultRelativeRoot = "uploads";

    /// <summary>Absolute path of the uploads root. Created on startup if missing.</summary>
    public string Root { get; }

    /// <summary>Absolute path of the public profile-photo directory. Created on startup if missing.</summary>
    public string PhotosDirectory { get; }

    public UploadStorage(IConfiguration config, IWebHostEnvironment env)
    {
        var configured = config[ConfigKey];
        var path = string.IsNullOrWhiteSpace(configured) ? DefaultRelativeRoot : configured.Trim();

        Root = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path));
        PhotosDirectory = Path.Combine(Root, "photos");

        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(PhotosDirectory);
    }

    /// <summary>
    /// Returns (and creates) the per-user directory for a private document type,
    /// e.g. <c>GetUserDirectory("resumes", userId)</c>.
    /// </summary>
    public string GetUserDirectory(string subDir, string userId)
    {
        var dir = Path.Combine(Root, subDir, userId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Builds the value persisted in <c>StoredPath</c> for a new document: a path relative to
    /// the uploads root using forward slashes, e.g. <c>resumes/{userId}/{file}.pdf</c>.
    /// </summary>
    public static string MakeStoredPath(string subDir, string userId, string fileName)
        => $"{subDir}/{userId}/{fileName}";

    /// <summary>
    /// Resolves a persisted <c>StoredPath</c> to an absolute path under the current root.
    /// Tolerates the legacy <c>uploads/...</c> prefix written before the root became configurable,
    /// so existing database rows keep working without a migration.
    /// </summary>
    public string Resolve(string storedPath)
    {
        var rel = storedPath.Replace('\\', '/').TrimStart('/');
        if (rel.StartsWith(DefaultRelativeRoot + "/", StringComparison.OrdinalIgnoreCase))
            rel = rel.Substring(DefaultRelativeRoot.Length + 1);

        var full = Path.GetFullPath(Path.Combine(Root, rel));

        // Defence in depth: a StoredPath must never escape the uploads root.
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Stored path resolves outside the uploads root.");

        return full;
    }

    /// <summary>True if the document referenced by <paramref name="storedPath"/> exists on disk.</summary>
    public bool Exists(string storedPath) => File.Exists(Resolve(storedPath));

    /// <summary>Deletes the document referenced by <paramref name="storedPath"/> if it exists; no-ops otherwise.</summary>
    public void Delete(string storedPath)
    {
        var full = Resolve(storedPath);
        if (File.Exists(full)) File.Delete(full);
    }
}
