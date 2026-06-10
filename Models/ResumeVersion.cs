namespace InternTrackAI.Models;

public class ResumeVersion
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string OriginalFileName { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; }
}
