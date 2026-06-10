using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using InternTrackAI.Models;

namespace InternTrackAI.Data;

public class ApplicationDbContext : IdentityDbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<JobApplication> JobApplications { get; set; }
    public DbSet<UserProfile> UserProfiles { get; set; }
    public DbSet<ResumeVersion> ResumeVersions { get; set; }
    public DbSet<CoverLetterVersion> CoverLetterVersions { get; set; }
}