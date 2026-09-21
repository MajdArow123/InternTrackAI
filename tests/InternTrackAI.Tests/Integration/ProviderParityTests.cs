using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Serialises everything that touches the real PostgreSQL database: each class drops and recreates
/// it, so two running at once would tear the schema out from under each other.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class RealPostgresCollection
{
    public const string Name = "RealPostgres";
}

/// <summary>
/// One migrated schema per provider, built once for the whole class.
/// </summary>
/// <remarks>
/// <b>Migrations, not <c>EnsureCreated</c>.</b> The schema under test has to be the one that ships,
/// and the two differ in exactly the place these tests care about: <c>EnsureCreated</c> builds from
/// the model, where <c>[StringLength(300)]</c> becomes <c>varchar(300)</c> on PostgreSQL, while the
/// migrations set <c>type: "TEXT"</c> explicitly and leave it <c>text</c>. Building from the model
/// would quietly test a schema production has never had.
/// </remarks>
public class ProviderParityFixture : IAsyncLifetime
{
    public const string EnvVar = "INTERNTRACK_PG_CONNECTION";

    private SqliteConnection _sqlite = null!;
    public string? PostgresConnection { get; private set; }
    public bool HasPostgres => PostgresConnection is not null;

    public async Task InitializeAsync()
    {
        _sqlite = new SqliteConnection("Data Source=:memory:");
        await _sqlite.OpenAsync();                       // an in-memory database lives only as long as its connection
        await using (var db = NewSqlite()) await db.Database.MigrateAsync();

        var conn = Environment.GetEnvironmentVariable(EnvVar);
        if (string.IsNullOrWhiteSpace(conn)) return;

        PostgresConnection = conn;
        await using var pg = NewPostgres();
        await pg.Database.EnsureDeletedAsync();
        await pg.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _sqlite.DisposeAsync().AsTask();

    public ApplicationDbContext NewSqlite() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(_sqlite).Options);

    public ApplicationDbContext NewPostgres() =>
        new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(PostgresConnection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))   // same as Program.cs
            .Options);
}

/// <summary>
/// Runs the same seed and the same query on <b>both</b> providers and asserts they agree.
/// </summary>
/// <remarks>
/// <para>
/// The schema guard in <c>MigrationColumnTypeTests</c> reads generated DDL and catches column types.
/// It cannot see <em>behaviour</em>: how a database sorts text, folds case, or rounds a timestamp. Two
/// bugs in this repo were of that second kind and both would have been production-only, so this suite
/// covers just the handful of queries where SQLite and Npgsql can legitimately disagree — it is
/// deliberately not a second copy of the whole test suite.
/// </para>
/// <para>
/// Every test asserts <b>parity</b> rather than a hardcoded answer wherever the answer is a database's
/// choice: a test that hardcoded SQLite's answer would just re-encode the local behaviour as correct.
/// Without <c>INTERNTRACK_PG_CONNECTION</c> the PostgreSQL half is skipped, the SQLite half still runs,
/// and each test says so in its output rather than passing silently.
/// </para>
/// </remarks>
[Collection(RealPostgresCollection.Name)]
public class ProviderParityTests : IClassFixture<ProviderParityFixture>
{
    private readonly ProviderParityFixture _fx;
    private readonly ITestOutputHelper _out;

    public ProviderParityTests(ProviderParityFixture fx, ITestOutputHelper output)
    {
        _fx = fx;
        _out = output;
    }

    /// <summary>Runs <paramref name="probe"/> on every provider available and returns name → result.</summary>
    private async Task<Dictionary<string, T>> OnEachProvider<T>(Func<ApplicationDbContext, Task<T>> probe)
    {
        var results = new Dictionary<string, T>();

        await using (var sqlite = _fx.NewSqlite()) results["SQLite"] = await probe(sqlite);

        if (_fx.HasPostgres)
        {
            await using var pg = _fx.NewPostgres();
            results["PostgreSQL"] = await probe(pg);
        }
        else
        {
            _out.WriteLine($"PostgreSQL half skipped: {ProviderParityFixture.EnvVar} is not set.");
        }

        foreach (var (name, value) in results)
            _out.WriteLine($"{name,-11} -> {Describe(value)}");

        return results;
    }

    private static string Describe<T>(T value) =>
        value is System.Collections.IEnumerable e and not string
            ? "[" + string.Join(", ", e.Cast<object>()) + "]"
            : $"{value}";

    /// <summary>Fails when the providers disagree, printing both answers.</summary>
    private void AssertParity<T>(Dictionary<string, T> results, string what)
    {
        if (results.Count < 2) return;   // nothing to compare; the skip was already reported

        var distinct = results.Values.Select(Describe).Distinct().ToList();
        Assert.True(distinct.Count == 1,
            $"{what} differs by provider:\n  " + string.Join("\n  ", results.Select(kv => $"{kv.Key,-11} {Describe(kv.Value)}")));
    }

    private static string NewUser() => $"parity-{Guid.NewGuid():N}";

    private static async Task<JobApplication> SeedApp(ApplicationDbContext db, string userId, string company, string role = "Intern")
    {
        var app = new JobApplication { UserId = userId, CompanyName = company, RoleTitle = role };
        db.JobApplications.Add(app);
        await db.SaveChangesAsync();
        return app;
    }

    // ── 1. String ordering ───────────────────────────────────────────────────

    /// <summary>
    /// "Sort by company" on the Applications list, which orders by <c>lower(CompanyName)</c> so the
    /// answer does not depend on the database's collation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before the fix this genuinely differed in production. Measured 2026-09-20: a bare
    /// <c>ORDER BY "CompanyName"</c> gives <c>Apple, Zebra, apple, banana, zebra</c> on SQLite and on the
    /// local test Postgres (collation <c>C</c>), but <c>apple, Apple, banana, zebra, Zebra</c> on Railway
    /// production (<c>en_US.utf8</c>).
    /// </para>
    /// <para>
    /// <b>The local Postgres cannot show that</b>, which is the point worth remembering here: a
    /// parity-only assertion passes vacuously against a <c>C</c> server. So this test does two further
    /// things — it asserts the <b>absolute</b> expected order, and on PostgreSQL it re-runs the query
    /// under an explicitly forced <c>en_US.utf8</c> collation and requires the same answer. That last
    /// assertion is the only one here that actually exercises production's collation.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Sorting_applications_by_company_gives_the_same_order_on_both_providers()
    {
        var names = new[] { "apple", "Zebra", "banana", "Apple", "zebra" };

        var results = await OnEachProvider(async db =>
        {
            var userId = NewUser();
            foreach (var n in names) await SeedApp(db, userId, n);

            return await db.JobApplications
                .Where(a => a.UserId == userId)
                .OrderBy(a => a.CompanyName.ToLower()).ThenBy(a => a.Id)   // JobApplicationsController.FilteredQuery
                .Select(a => a.CompanyName)
                .ToListAsync();
        });

        AssertParity(results, "ORDER BY lower(CompanyName)");

        // Case-insensitive alphabetical, ties broken by insertion order — the same answer under any
        // collation, which is the whole point of lowering in the query.
        var expected = new[] { "apple", "Apple", "banana", "Zebra", "zebra" };
        Assert.All(results, kv => Assert.Equal(expected, kv.Value));

        if (!_fx.HasPostgres) return;

        // A dictionary-order collation, forced, because the server this runs against is C and would
        // otherwise pass even if the ordering were still collation-dependent. Discovered rather than
        // hardcoded: the same locale is spelled "en_US.UTF-8" on macOS and "en_US.utf8" on Linux, and
        // production reports the latter.
        await using var pg = _fx.NewPostgres();

        var collation = await FirstAvailableCollation(pg);
        if (collation is null)
        {
            _out.WriteLine("No dictionary-order collation on this server; skipped the forced-collation check.");
            return;
        }

        var userId = NewUser();
        foreach (var n in names) await SeedApp(pg, userId, n);

        var forced = await pg.JobApplications
            .Where(a => a.UserId == userId)
            .OrderBy(a => EF.Functions.Collate(a.CompanyName.ToLower(), collation)).ThenBy(a => a.Id)
            .Select(a => a.CompanyName)
            .ToListAsync();

        _out.WriteLine($"PG {collation,-12} -> {Describe(forced)}");
        Assert.Equal(expected, forced);
    }

    /// <summary>
    /// The first dictionary-order collation this server actually has, or null. Production is
    /// <c>en_US.utf8</c>; macOS spells the same thing <c>en_US.UTF-8</c>; the ICU names exist on any
    /// build with ICU. All four order text the same way for the ASCII names this test uses.
    /// </summary>
    private static async Task<string?> FirstAvailableCollation(ApplicationDbContext pg)
    {
        var candidates = new[] { "en_US.utf8", "en_US.UTF-8", "und-x-icu", "en-US-x-icu" };

        var present = await pg.Database
            .SqlQueryRaw<string>("select collname as \"Value\" from pg_collation where collname = any({0})", new object[] { candidates })
            .ToListAsync();

        return candidates.FirstOrDefault(present.Contains);
    }

    // ── 2. Case-insensitive search ───────────────────────────────────────────

    /// <summary>
    /// The list search. <c>lower()</c> on both sides is what makes this case-insensitive by
    /// construction instead of by EF's translation choice — SQLite gets <c>instr(lower(…))</c> and
    /// Npgsql <c>lower(…) LIKE</c>, and this pins that they mean the same thing.
    /// </summary>
    [Theory]
    [InlineData("shopify", 1)]
    [InlineData("SHOPIFY", 1)]
    [InlineData("ShOpIfY", 1)]
    [InlineData("stripe", 0)]
    public async Task Searching_is_case_insensitive_on_both_providers(string typed, int expected)
    {
        var results = await OnEachProvider(async db =>
        {
            var userId = NewUser();
            await SeedApp(db, userId, "Shopify");

            var term = typed.ToLowerInvariant();
            return await db.JobApplications
                .Where(a => a.UserId == userId)
                .Where(a => a.CompanyName.ToLower().Contains(term) || a.RoleTitle.ToLower().Contains(term))
                .CountAsync();
        });

        AssertParity(results, $"case-insensitive search for \"{typed}\"");
        Assert.All(results, kv => Assert.Equal(expected, kv.Value));
    }

    // ── 3. DateTime round-trip ───────────────────────────────────────────────

    /// <summary>
    /// The difference that produced the demo-reset stamp bug: SQLite keeps a DateTime's full tick
    /// precision, PostgreSQL's <c>timestamptz</c> rounds to microseconds. Anything comparing a written
    /// value against a read-back one has to survive that, which is why
    /// <see cref="Services.DemoProfileReset.Stamp"/> truncates to whole seconds.
    /// </summary>
    [Fact]
    public async Task A_DateTime_round_trips_as_UTC_and_agrees_to_the_precision_the_app_relies_on()
    {
        // 100-nanosecond ticks that no PostgreSQL timestamptz can hold.
        var written = new DateTime(2026, 9, 20, 19, 31, 53, DateTimeKind.Utc).AddTicks(6_583_717);

        var results = await OnEachProvider(async db =>
        {
            var userId = NewUser();
            db.ParsedResumes.Add(new ParsedResume { UserId = userId, RawJson = "{}", CreatedAt = written });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();                              // force a real read, not the cached entity

            var row = await db.ParsedResumes.AsNoTracking().SingleAsync(p => p.UserId == userId);
            return (Kind: row.CreatedAt.Kind, Stamp: Services.DemoProfileReset.Stamp(row.CreatedAt));
        });

        // Kind must be Utc everywhere — ApplicationDbContext.ConfigureConventions relabels on read.
        Assert.All(results, kv => Assert.Equal(DateTimeKind.Utc, kv.Value.Kind));

        // Whole ticks may differ; the second-level stamp the app actually compares must not.
        AssertParity(results.ToDictionary(kv => kv.Key, kv => kv.Value.Stamp), "second-level DateTime stamp");
        Assert.All(results, kv => Assert.Equal(Services.DemoProfileReset.Stamp(written), kv.Value.Stamp));
    }

    // ── 4. Ordering ties ─────────────────────────────────────────────────────

    /// <summary>
    /// Draft pruning orders by <c>CreatedAt DESC, Id DESC</c>. On PostgreSQL two rows written inside the
    /// same microsecond tie on CreatedAt, so the Id tie-break is what makes "keep the newest three"
    /// deterministic rather than whatever order the engine returns.
    /// </summary>
    [Fact]
    public async Task Pruning_breaks_CreatedAt_ties_by_Id_on_both_providers()
    {
        var sameInstant = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

        var results = await OnEachProvider(async db =>
        {
            var userId = NewUser();
            for (var i = 0; i < 5; i++)
                db.ParsedResumes.Add(new ParsedResume { UserId = userId, RawJson = $"{{\"n\":{i}}}", CreatedAt = sameInstant });
            await db.SaveChangesAsync();

            var kept = await db.ParsedResumes.Where(p => p.UserId == userId)
                .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .Take(Services.ResumeParseService.MaxDraftsPerUser)
                .Select(p => p.RawJson)
                .ToListAsync();

            return kept;
        });

        AssertParity(results, "tie-broken prune order");
        // Newest-first by Id: the last three inserted, in reverse insertion order.
        Assert.All(results, kv => Assert.Equal(new[] { "{\"n\":4}", "{\"n\":3}", "{\"n\":2}" }, kv.Value));
    }

    // ── 5. Floating point ────────────────────────────────────────────────────

    /// <summary>
    /// <c>StatusSuggestion.Confidence</c> is a <c>double</c>. Scaffolding it as SQLite's <c>REAL</c>
    /// would make it <c>float4</c> on PostgreSQL and silently truncate — the trap CLAUDE.md §8 records
    /// and <c>AddStatusSuggestions</c> branches for.
    /// </summary>
    [Fact]
    public async Task A_double_round_trips_without_losing_precision_on_both_providers()
    {
        const double written = 0.8123456789012345;

        var results = await OnEachProvider(async db =>
        {
            var userId = NewUser();
            var app = await SeedApp(db, userId, "Pg Co");

            db.StatusSuggestions.Add(new StatusSuggestion
            {
                UserId = userId, ApplicationId = app.Id, GmailMessageId = Guid.NewGuid().ToString("N"),
                SuggestedStatus = ApplicationStatus.Interview, Confidence = written,
                Summary = "s", EmailSubject = "s", EmailFrom = "a@b.c"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            return (await db.StatusSuggestions.AsNoTracking().SingleAsync(s => s.UserId == userId)).Confidence;
        });

        AssertParity(results, "double round-trip");
        Assert.All(results, kv => Assert.Equal(written, kv.Value));   // exact: float4 would not survive this
    }

    // ── 6. String length ─────────────────────────────────────────────────────

    /// <summary>
    /// Every migration sets <c>type: "TEXT"</c> explicitly, which overrides <c>[StringLength]</c> and
    /// keeps these columns <c>text</c> on PostgreSQL too. This is the runtime half of
    /// <c>MigrationColumnTypeTests.No_app_column_becomes_a_length_limited_varchar_on_PostgreSQL</c>:
    /// if that type were ever dropped, the column would become <c>varchar(100)</c> and this write would
    /// be error 22001 in production while still succeeding locally.
    /// </summary>
    [Fact]
    public async Task An_over_long_string_is_accepted_on_both_providers()
    {
        var overLong = new string('x', 5_000);   // RoleTitle is [StringLength(100)]

        var results = await OnEachProvider(async db =>
        {
            var userId = NewUser();
            await SeedApp(db, userId, "Length Co", overLong);
            db.ChangeTracker.Clear();

            return (await db.JobApplications.AsNoTracking().SingleAsync(a => a.UserId == userId)).RoleTitle.Length;
        });

        AssertParity(results, "stored length of an over-long RoleTitle");
        Assert.All(results, kv => Assert.Equal(overLong.Length, kv.Value));
    }
}
