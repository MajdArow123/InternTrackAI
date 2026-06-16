using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
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

    // PostgreSQL's "timestamp with time zone" columns reject DateTime.Kind=Unspecified
    // (which is what model binding produces from <input type="date">). Force UTC on the
    // way in and out so this holds regardless of provider.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    // Re-labels rather than shifts: form-bound dates (Kind=Unspecified) have no real timezone
    // meaning, so they should keep their calendar value and just be tagged UTC for Npgsql.
    private class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
    {
        public UtcDateTimeConverter() : base(
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
            v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
        {
        }
    }

    private class NullableUtcDateTimeConverter : ValueConverter<DateTime?, DateTime?>
    {
        public NullableUtcDateTimeConverter() : base(
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v)
        {
        }
    }
}
