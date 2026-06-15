using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using InternTrackAI.Models;

namespace InternTrackAI.Data;

public class ApplicationDbContext : IdentityDbContext, IDataProtectionKeyContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<JobApplication> JobApplications { get; set; }
    public DbSet<UserProfile> UserProfiles { get; set; }
    public DbSet<ResumeVersion> ResumeVersions { get; set; }
    public DbSet<CoverLetterVersion> CoverLetterVersions { get; set; }
    public DbSet<GeneratedCoverLetter> GeneratedCoverLetters { get; set; }
    public DbSet<InterviewPrepSession> InterviewPrepSessions { get; set; }

    // Persists Data Protection keys to DB so they survive container restarts and redeployments.
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;
}
