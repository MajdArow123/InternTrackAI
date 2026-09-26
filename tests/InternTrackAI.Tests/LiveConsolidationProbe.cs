using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace InternTrackAI.Tests;

/// <summary>
/// The generator-consolidation re-measure (CLAUDE.md §12, deferred item 1): for the same two postings, what
/// today's interview-prep generator produces, and what the practice generator produces when the prep page
/// would call it once per category for that application. It prints every question and topic from both sides
/// so the run can be committed to <c>docs/measurements/</c> — the 2026-09-24 prep-topic run kept its topics
/// but not its questions, which is why this one needed two extra calls for a fresh baseline.
/// </summary>
/// <remarks>
/// <para><b>This spends money and is off by default.</b> It runs only with <c>INTERNTRACK_LIVE_AI</c> set and
/// <c>INTERNTRACK_PROBE_POSTINGS</c> naming a JSON file of <c>[{company, role, description}]</c>. The postings
/// are real third-party job ads and the repo is public, so they are never committed; the output records each
/// one's SHA-256 so a later run can confirm it used the same text. The resume is a synthetic fixture below, not
/// anyone's real resume, because the prep generator quotes the resume back and the questions are committed.</para>
/// <para>Budget agreed with the maintainer: <see cref="MaxCalls"/> = 8 (2 prep baselines + 3 categories × 2
/// postings through the practice generator). The handler throws past it; if a practice generation needed its
/// top-up call, a later one fails and the output says so rather than spending a ninth.</para>
/// </remarks>
public class LiveConsolidationProbe
{
    public const string EnvVar = "INTERNTRACK_LIVE_AI";
    public const string PostingsVar = "INTERNTRACK_PROBE_POSTINGS";
    public const int MaxCalls = 8;

    /// <summary>The prep generator's own mix (3–4 / 3–4 / 2), so both sides return about the same number.</summary>
    private static readonly (QuestionCategory Category, int Count)[] Mix =
    {
        (QuestionCategory.Technical, 4), (QuestionCategory.Behavioral, 3), (QuestionCategory.CompanySpecific, 2),
    };

    public const string SyntheticResume =
        "Alex Chen — Computer Science student, University of Toronto (expected 2027).\n" +
        "Experience: Software Developer Intern, Northwind Logistics (Summer 2025) — built a REST API in Python/FastAPI " +
        "for shipment tracking, added PostgreSQL indexes that cut a slow report from 40s to 3s, wrote pytest suites.\n" +
        "Teaching Assistant, Intro to Programming (2024–2025) — ran weekly labs for 30 students.\n" +
        "Projects: Campus Events app (React, Node.js, deployed on AWS with Docker and GitHub Actions CI); " +
        "a Raft consensus toy implementation in Go for a distributed systems course.\n" +
        "Skills: Python, Go, JavaScript, TypeScript, React, Node.js, SQL, PostgreSQL, Docker, AWS, Git, Linux.";

    private const string Skills = "Python, Go, TypeScript, React, SQL, PostgreSQL, Docker, AWS";
    private const string ProfileContext =
        "USER PROFILE CONTEXT\nField: Software Engineering (Technology)\nSeniority: Student, ~1 year experience\n" +
        "Location: Toronto, ON\nSkills: Python, Go, TypeScript, React, SQL, PostgreSQL, Docker, AWS";

    private readonly ITestOutputHelper _out;
    public LiveConsolidationProbe(ITestOutputHelper output) => _out = output;

    private sealed class BudgetedHandler : DelegatingHandler
    {
        public int Calls { get; private set; }
        public BudgetedHandler() : base(new HttpClientHandler()) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Calls >= MaxCalls) throw new InvalidOperationException($"Call budget of {MaxCalls} exhausted — refusing to spend more.");
            Calls++;
            return base.SendAsync(request, ct);
        }
    }

    private sealed record Posting(string Company, string Role, string Description);

    [Fact]
    public async Task Prep_generator_versus_practice_generator_on_the_same_postings()
    {
        var postingsFile = Environment.GetEnvironmentVariable(PostingsVar);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)) || string.IsNullOrWhiteSpace(postingsFile))
        {
            _out.WriteLine($"Skipped: needs {EnvVar} and {PostingsVar}. This probe calls the real OpenAI API and costs money.");
            return;
        }

        var postings = JsonSerializer.Deserialize<List<Posting>>(await File.ReadAllTextAsync(postingsFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(2, postings.Count);

        // The real key, from user-secrets, never printed.
        var config = new ConfigurationBuilder()
            .AddUserSecrets("aspnet-InternTrackAI-a9273f32-3acf-454b-ae9a-5c9465b893ec")
            .AddEnvironmentVariables()
            .Build();
        Assert.False(string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]), "No OpenAI:ApiKey in user-secrets.");

        var handler = new BudgetedHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        var md = new StringBuilder();
        void W(string line = "") { md.AppendLine(line); _out.WriteLine(line); }

        // ── Side A: today's prep generator, one call per posting ──
        var prep = new InterviewPrepService(http, config, NullLogger<InterviewPrepService>.Instance);
        var prepResults = new List<(Posting P, List<GeneratedQuestion> Qs, string? Error)>();
        foreach (var p in postings)
        {
            var (ok, qs, err) = await prep.GenerateAsync(p.Company, p.Role, p.Description, SyntheticResume, Skills, ProfileContext);
            prepResults.Add((p, ok ? qs : new(), ok ? null : err));
        }

        // ── Side B: the practice generator, per category, grounded in the same application ──
        var practiceResults = new List<(Posting P, QuestionCategory C, List<PracticeQuestion> Qs, string? Error)>();
        foreach (var p in postings)
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
            await using (var setup = new ApplicationDbContext(options)) await setup.Database.MigrateAsync();

            const string userId = "consolidation-probe-user";
            int appId;
            await using (var db = new ApplicationDbContext(options))
            {
                var app = new JobApplication { UserId = userId, CompanyName = p.Company, RoleTitle = p.Role, JobDescription = p.Description };
                db.JobApplications.Add(app);
                await db.SaveChangesAsync();
                appId = app.Id;
            }

            foreach (var (category, count) in Mix)
            {
                await using var db = new ApplicationDbContext(options);
                var service = new PracticeQuestionService(db, http, config, NullLogger<PracticeQuestionService>.Instance);
                try
                {
                    var r = await service.GenerateAsync(userId, PracticeDifficulty.Medium, category, count, appId, ProfileContext);
                    practiceResults.Add((p, category, r.Success ? r.Questions : new(), r.Success ? null : r.Error));
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Call budget"))
                {
                    practiceResults.Add((p, category, new(), ex.Message));
                }
            }
        }

        // ── Output: everything, in a form that can be committed as-is ──
        W($"Model calls: {handler.Calls} of {MaxCalls}. Model: gpt-4o-mini. Difficulty for side B: Medium (what prep rows store).");
        W();
        W("Inputs (posting text is not committed — public repo, third-party ads; hashes identify it):");
        foreach (var p in postings)
            W($"- {p.Company} — {p.Role}: {p.Description.Length} chars, SHA-256 {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Description)))[..16]}…");
        W("- Resume: the synthetic fixture `LiveConsolidationProbe.SyntheticResume` (side A only; the practice generator takes no resume).");
        foreach (var p in postings)
        {
            W();
            W($"## {p.Company} — {p.Role}");
            W();
            W("### A. Interview-prep generator (one call)");
            var a = prepResults.Single(x => x.P == p);
            if (a.Error is not null) W($"FAILED: {a.Error}");
            foreach (var q in a.Qs) W($"- [{QuestionCategories.Display(q.Category)}] topic: `{q.Topic}` — {q.Question}");
            W();
            W("### B. Practice generator, per category, with this application attached");
            foreach (var b in practiceResults.Where(x => x.P == p))
            {
                if (b.Error is not null) W($"- [{QuestionCategories.Display(b.C)}] FAILED: {b.Error}");
                foreach (var q in b.Qs) W($"- [{QuestionCategories.Display(b.C)}] topic: `{q.Topic}` — {q.Prompt}");
            }
        }

        var outFile = Environment.GetEnvironmentVariable("INTERNTRACK_PROBE_OUT");
        if (!string.IsNullOrWhiteSpace(outFile)) await File.WriteAllTextAsync(outFile, md.ToString());
        Assert.True(handler.Calls <= MaxCalls);
    }
}
