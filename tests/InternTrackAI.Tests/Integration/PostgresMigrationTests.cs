using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Opt-in end-to-end check against a real PostgreSQL server: set
/// <c>INTERNTRACK_PG_CONNECTION</c> (e.g. <c>Host=localhost;Port=5432;Username=postgres;Database=interntrack_test</c>)
/// to run it; without the variable the test passes as a no-op so CI (SQLite only) is unaffected.
/// The database named in the connection string is DROPPED and recreated. Migrates from scratch,
/// then verifies every date/time column's real type and that notes (and locked-out users) can be
/// written and read back — the two things that were broken on Postgres before FixApplicationNoteCreatedAtType.
/// </summary>
[Collection(RealPostgresCollection.Name)]   // serialised with ProviderParityTests: both drop and recreate the same database
public class PostgresMigrationTests
{
    private readonly ITestOutputHelper _out;
    public PostgresMigrationTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Migrations_produce_timestamptz_columns_and_notes_round_trip()
    {
        var conn = Environment.GetEnvironmentVariable("INTERNTRACK_PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(conn)) { _out.WriteLine("Skipped: INTERNTRACK_PG_CONNECTION is not set."); return; }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(conn)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))   // same as Program.cs
            .Options;

        int noteId; string userId;
        await using (var db = new ApplicationDbContext(options))
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();

            var columns = await db.Database.SqlQueryRaw<string>(
                    "select table_name || '.' || column_name || '=' || data_type as \"Value\" " +
                    "from information_schema.columns where table_schema = 'public' " +
                    "and (column_name like '%At' or column_name in ('Deadline', 'DateApplied', 'LockoutEnd')) order by 1")
                .ToListAsync();
            Assert.NotEmpty(columns);
            Assert.Contains("ApplicationNotes.CreatedAt=timestamp with time zone", columns);
            Assert.Contains("AspNetUsers.LockoutEnd=timestamp with time zone", columns);
            Assert.All(columns, c => Assert.EndsWith("=timestamp with time zone", c));

            var app = new JobApplication { UserId = "pg-user", CompanyName = "Pg Co", RoleTitle = "Intern" };
            db.JobApplications.Add(app);
            await db.SaveChangesAsync();

            var note = new ApplicationNote { UserId = "pg-user", JobApplicationId = app.Id, Text = "hello", CreatedAt = new DateTime(2026, 9, 13, 18, 30, 0, DateTimeKind.Utc) };
            db.ApplicationNotes.Add(note);                       // needs identity on Id and a timestamptz CreatedAt
            await db.SaveChangesAsync();
            noteId = note.Id;
            Assert.True(noteId > 0);

            var user = new IdentityUser("pg@example.test") { Email = "pg@example.test", NormalizedEmail = "PG@EXAMPLE.TEST", NormalizedUserName = "PG@EXAMPLE.TEST", LockoutEnd = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        await using (var db = new ApplicationDbContext(options))
        {
            var note = await db.ApplicationNotes.AsNoTracking().SingleAsync(n => n.Id == noteId);
            Assert.Equal(new DateTime(2026, 9, 13, 18, 30, 0, DateTimeKind.Utc), note.CreatedAt);

            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), user.LockoutEnd);
        }
    }

    /// <summary>
    /// Upgrade path: a database at the previous head (text columns, no identity) that already holds
    /// legacy text values — a real timestamp string, a blank CreatedAt, a blank LockoutEnd — is
    /// migrated forward. The USING clauses must convert the real value, fall back to now() for the
    /// blank NOT NULL CreatedAt, turn the blank LockoutEnd into NULL, and the identity must continue
    /// after the existing max Id.
    /// </summary>
    [Fact]
    public async Task Upgrading_a_database_with_legacy_text_values_converts_them()
    {
        var conn = Environment.GetEnvironmentVariable("INTERNTRACK_PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(conn)) { _out.WriteLine("Skipped: INTERNTRACK_PG_CONNECTION is not set."); return; }

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(conn)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

        await using var db = new ApplicationDbContext(options);
        await db.Database.EnsureDeletedAsync();

        var assembly = db.GetService<IMigrationsAssembly>();
        var previousHead = assembly.Migrations.Keys.Single(k => k.EndsWith("_AddCalendarToken"));
        await db.GetService<IMigrator>().MigrateAsync(previousHead);

        var before = await db.Database.SqlQueryRaw<string>("select data_type as \"Value\" from information_schema.columns where table_name = 'ApplicationNotes' and column_name = 'CreatedAt'").SingleAsync();
        Assert.Equal("text", before);

        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "JobApplications" ("UserId","CompanyName","RoleTitle","WorkMode","Status","BoardOrder") VALUES ('legacy','Legacy Co','Intern',0,0,0)""");
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "ApplicationNotes" ("Id","JobApplicationId","UserId","Text","CreatedAt") VALUES (7, 1, 'legacy', 'real', '2026-09-01 10:15:00+00'), (8, 1, 'legacy', 'blank', '  ')""");
        await db.Database.ExecuteSqlRawAsync("""INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","Email","NormalizedEmail","EmailConfirmed","PasswordHash","SecurityStamp","ConcurrencyStamp","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnd","LockoutEnabled","AccessFailedCount") VALUES ('u-real','a','A','a@x','A@X',false,'','s','c',false,false,'2030-01-01 00:00:00+00',true,0), ('u-blank','b','B','b@x','B@X',false,'','s','c',false,false,'',true,0)""");

        var started = DateTime.UtcNow.AddMinutes(-1);
        await db.Database.MigrateAsync();

        var after = await db.Database.SqlQueryRaw<string>("select data_type as \"Value\" from information_schema.columns where table_name = 'ApplicationNotes' and column_name = 'CreatedAt'").SingleAsync();
        Assert.Equal("timestamp with time zone", after);

        var notes = await db.ApplicationNotes.AsNoTracking().OrderBy(n => n.Id).ToListAsync();
        Assert.Equal(new DateTime(2026, 9, 1, 10, 15, 0, DateTimeKind.Utc), notes[0].CreatedAt);
        Assert.True(notes[1].CreatedAt >= started, "blank CreatedAt should have fallen back to now()");

        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id).ToListAsync();
        Assert.Null(users.Single(u => u.Id == "u-blank").LockoutEnd);
        Assert.Equal(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), users.Single(u => u.Id == "u-real").LockoutEnd);

        db.ApplicationNotes.Add(new ApplicationNote { UserId = "legacy", JobApplicationId = 1, Text = "new", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Assert.Equal(9, await db.ApplicationNotes.MaxAsync(n => n.Id));   // identity resumed after the existing max
    }

    /// <summary>
    /// The demo/admin account lookup on real PostgreSQL: a config address with different casing and surrounding
    /// whitespace resolves the user through <see cref="ConfiguredAccounts.FindAsync"/>, while a raw
    /// <c>Email ==</c> comparison (never to be used for this) is case-sensitive.
    /// </summary>
    [Fact]
    public async Task Configured_account_lookup_ignores_case_and_whitespace_on_PostgreSQL()
    {
        var conn = Environment.GetEnvironmentVariable("INTERNTRACK_PG_CONNECTION");
        if (string.IsNullOrWhiteSpace(conn)) { _out.WriteLine("Skipped: INTERNTRACK_PG_CONNECTION is not set."); return; }

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(conn).ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));
        services.AddIdentityCore<IdentityUser>().AddEntityFrameworkStores<ApplicationDbContext>();
        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.MigrateAsync();
            var created = await scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>()
                .CreateAsync(new IdentityUser { UserName = "demo@interntrackai.com", Email = "demo@interntrackai.com" }, "Pg-Demo-Pass-1!");
            Assert.True(created.Succeeded);
        }

        using (var scope = provider.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
            var (user, match) = await ConfiguredAccounts.FindAsync(users, "  Demo@InternTrackAI.COM\n");
            Assert.NotNull(user);
            Assert.Equal(ConfiguredAccountMatch.Email, match);

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Email == "Demo@InternTrackAI.COM"));   // the trap ConfiguredAccounts avoids
        }
    }
}
