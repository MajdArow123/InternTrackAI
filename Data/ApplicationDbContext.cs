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
    public DbSet<GeneratedCoverLetter> GeneratedCoverLetters { get; set; }
    public DbSet<PracticeQuestion> PracticeQuestions { get; set; }
    public DbSet<ApplicationNote> ApplicationNotes { get; set; }
    public DbSet<GmailConnection> GmailConnections { get; set; }
    public DbSet<StatusSuggestion> StatusSuggestions { get; set; }
    public DbSet<ParsedResume> ParsedResumes { get; set; }

    // Persists Data Protection keys to DB so they survive container restarts and redeployments.
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Notes belong to exactly one application and have no life of their own: deleting the
        // application (single or bulk) must take its notes with it, on SQLite and PostgreSQL alike.
        builder.Entity<ApplicationNote>()
            .HasOne(n => n.JobApplication)
            .WithMany()
            .HasForeignKey(n => n.JobApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // An application remembers which resume version it was sent with. Deleting that resume must
        // never delete the application — the link is cleared instead (the app then shows "No resume").
        builder.Entity<JobApplication>()
            .HasOne(a => a.ResumeVersion)
            .WithMany()
            .HasForeignKey(a => a.ResumeVersionId)
            .OnDelete(DeleteBehavior.SetNull);

        // One Gmail account per user; the tokens in this row are Data Protection ciphertext.
        builder.Entity<GmailConnection>()
            .HasIndex(c => c.UserId)
            .IsUnique();

        // Suggestions die with their application; a Gmail message is classified at most once per user.
        builder.Entity<StatusSuggestion>()
            .HasOne(s => s.Application)
            .WithMany()
            .HasForeignKey(s => s.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Entity<StatusSuggestion>()
            .HasIndex(s => new { s.UserId, s.GmailMessageId })
            .IsUnique();
        builder.Entity<StatusSuggestion>()
            .HasIndex(s => new { s.UserId, s.Status });

        // Resume parse drafts are read newest-first, per user: once to find the one awaiting review and
        // once to prune older rows on insert. No FK to ResumeVersions — deleting a resume mid-review
        // would cascade the draft away rather than leave it readable.
        builder.Entity<ParsedResume>()
            .HasIndex(p => new { p.UserId, p.CreatedAt });

        // Practice questions die with the application they were generated for, exactly as the prep
        // sessions they replace did; a question with no ApplicationId is general practice and survives.
        builder.Entity<PracticeQuestion>()
            .HasOne(q => q.Application)
            .WithMany()
            .HasForeignKey(q => q.ApplicationId)
            .OnDelete(DeleteBehavior.Cascade);

        // The hard duplicate guarantee. Prompt-level steering reduces repetition; this is what makes
        // "no duplicates" true, so a race between two generations still cannot store the same question.
        builder.Entity<PracticeQuestion>()
            .HasIndex(q => new { q.UserId, q.PromptHash })
            .IsUnique();

        // The feed, and the topic-coverage query the generator's exclusion list is built from.
        builder.Entity<PracticeQuestion>().HasIndex(q => new { q.UserId, q.CreatedAt });
        builder.Entity<PracticeQuestion>().HasIndex(q => new { q.UserId, q.Topic });
    }

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
