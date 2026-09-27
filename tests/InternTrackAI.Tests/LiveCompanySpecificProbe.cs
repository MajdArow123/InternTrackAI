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
/// The Company-Specific prompt measurement (docs/measurements/2026-09-26-company-specific.md): Company-Specific
/// practice questions for the same two postings, 2 per batch, each posting twice, through the practice page's own
/// path with the application attached. Prints every question for the document; the verdict is applied by hand
/// against the definitions committed there before the run.
/// </summary>
/// <remarks>
/// Off unless <c>INTERNTRACK_LIVE_AI</c> is set and <c>INTERNTRACK_PROBE_POSTINGS</c> names the (uncommitted)
/// postings file — see <see cref="LiveConsolidationProbe"/>. Hard cap <see cref="MaxCalls"/> = 4, agreed with the
/// maintainer; the handler throws on a fifth. Each batch runs on a fresh database so no exclusion list or
/// near-duplicate check shapes what the model returns.
/// </remarks>
public class LiveCompanySpecificProbe
{
    public const int MaxCalls = 4;
    private const int Repeats = 2;
    private const int PerBatch = 2;

    private const string ProfileContext =
        "USER PROFILE CONTEXT\nField: Software Engineering (Technology)\nSeniority: Student, ~1 year experience\n" +
        "Location: Toronto, ON\nSkills: Python, Go, TypeScript, React, SQL, PostgreSQL, Docker, AWS";

    private readonly ITestOutputHelper _out;
    public LiveCompanySpecificProbe(ITestOutputHelper output) => _out = output;

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
    public async Task Company_specific_questions_for_the_two_postings()
    {
        var postingsFile = Environment.GetEnvironmentVariable(LiveConsolidationProbe.PostingsVar);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LiveConsolidationProbe.EnvVar)) || string.IsNullOrWhiteSpace(postingsFile))
        {
            _out.WriteLine($"Skipped: needs {LiveConsolidationProbe.EnvVar} and {LiveConsolidationProbe.PostingsVar}. This probe calls the real OpenAI API.");
            return;
        }

        var postings = JsonSerializer.Deserialize<List<Posting>>(await File.ReadAllTextAsync(postingsFile),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var config = new ConfigurationBuilder()
            .AddUserSecrets("aspnet-InternTrackAI-a9273f32-3acf-454b-ae9a-5c9465b893ec")
            .AddEnvironmentVariables()
            .Build();
        Assert.False(string.IsNullOrWhiteSpace(config["OpenAI:ApiKey"]), "No OpenAI:ApiKey in user-secrets.");

        var handler = new BudgetedHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        var md = new StringBuilder();
        void W(string line = "") { md.AppendLine(line); _out.WriteLine(line); }

        var rows = new List<string>();
        foreach (var p in postings)
        {
            for (var repeat = 1; repeat <= Repeats; repeat++)
            {
                await using var connection = new SqliteConnection("Data Source=:memory:");
                await connection.OpenAsync();
                var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options;
                await using (var setup = new ApplicationDbContext(options)) await setup.Database.MigrateAsync();

                const string userId = "company-specific-probe-user";
                int appId;
                await using (var db = new ApplicationDbContext(options))
                {
                    var app = new JobApplication { UserId = userId, CompanyName = p.Company, RoleTitle = p.Role, JobDescription = p.Description };
                    db.JobApplications.Add(app);
                    await db.SaveChangesAsync();
                    appId = app.Id;
                }

                await using var gen = new ApplicationDbContext(options);
                var service = new PracticeQuestionService(gen, http, config, NullLogger<PracticeQuestionService>.Instance);
                try
                {
                    var r = await service.GenerateAsync(userId, PracticeDifficulty.Medium, QuestionCategory.CompanySpecific, PerBatch, appId, ProfileContext);
                    if (!r.Success) rows.Add($"| {p.Company} | {repeat} | FAILED: {r.Error} | |");
                    foreach (var q in r.Questions) rows.Add($"| {p.Company} | {repeat} | {q.Prompt} | `{q.Topic}` |");
                }
                catch (InvalidOperationException ex) when (ex.Message.StartsWith("Call budget"))
                {
                    rows.Add($"| {p.Company} | {repeat} | NOT RUN: {ex.Message} | |");
                }
            }
        }

        W($"Model calls: {handler.Calls} of {MaxCalls}. Inputs: " + string.Join(", ", postings.Select(p =>
            $"{p.Company} SHA-256 {Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Description)))[..16]}…")));
        W();
        W("| posting | batch | question (verbatim) | topic |");
        W("|---|---|---|---|");
        foreach (var r in rows) W(r);

        var outFile = Environment.GetEnvironmentVariable("INTERNTRACK_PROBE_OUT");
        if (!string.IsNullOrWhiteSpace(outFile)) await File.WriteAllTextAsync(outFile, md.ToString());
        Assert.True(handler.Calls <= MaxCalls);
    }
}
