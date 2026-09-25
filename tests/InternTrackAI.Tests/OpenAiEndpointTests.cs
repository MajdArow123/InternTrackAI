using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using InternTrackAI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace InternTrackAI.Tests;

/// <summary>
/// <c>OpenAI:BaseUrl</c> reaches every AI service. Five of them used to hardcode api.openai.com, so a dev
/// server pointed at a stub still sent their calls to the real API — the local-verification rule in
/// CLAUDE.md §8 depended on a placeholder key to stay free.
/// </summary>
public class OpenAiEndpointTests
{
    private const string Stub = "https://stub.openai.test";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_or_blank_means_the_real_api(string? configured)
    {
        Assert.Equal("https://api.openai.com/v1/chat/completions", OpenAiEndpoint.ChatCompletions(Config(configured)));
    }

    [Theory]
    [InlineData("https://stub.openai.test")]
    [InlineData("https://stub.openai.test/")]
    [InlineData("  https://stub.openai.test/  ")]
    public void A_configured_base_is_used_with_or_without_a_trailing_slash(string configured)
    {
        Assert.Equal(Stub + "/v1/chat/completions", OpenAiEndpoint.ChatCompletions(Config(configured)));
    }

    [Fact]
    public void The_real_api_url_is_written_in_exactly_one_place()
    {
        var root = RepoRoot();
        var offenders = new[] { "Services", "Controllers", "Areas", "Helpers" }
            .Select(d => Path.Combine(root, d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => Path.GetFileName(f) != "OpenAiEndpoint.cs")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"https://api\.openai\.com"))
            .Select(f => Path.GetRelativePath(root, f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "These build their own OpenAI URL instead of OpenAiEndpoint.ChatCompletions, so they ignore OpenAI:BaseUrl: "
            + string.Join(", ", offenders));
    }

    // ── The five services that used to ignore the setting, driven for real against a recording transport ──

    [Fact]
    public async Task Job_analyzer_posts_to_the_configured_base()
    {
        var (http, seen) = Recorder();
        var svc = new JobAnalyzerService(http, new NoFactory(), Config(Stub), NullLogger<JobAnalyzerService>.Instance);
        await svc.AnalyzeAsync("Backend intern at Shopify. Go, Postgres, Kafka. Toronto, hybrid.");
        AssertOnlyStub(seen);
    }

    [Fact]
    public async Task Resume_matcher_posts_to_the_configured_base()
    {
        var (http, seen) = Recorder();
        var svc = new ResumeMatcherService(http, Config(Stub), NullLogger<ResumeMatcherService>.Instance);
        await svc.MatchAsync("Built a Go service handling 2k requests a second.", "Backend intern, Go and Postgres.");
        AssertOnlyStub(seen);
    }

    [Fact]
    public async Task Cover_letter_generate_and_improve_post_to_the_configured_base()
    {
        var (http, seen) = Recorder();
        var svc = new CoverLetterGeneratorService(http, Config(Stub), NullLogger<CoverLetterGeneratorService>.Instance);
        await svc.GenerateAsync("Shopify", "Backend Intern", "Go and Postgres.", "Resume text.", "Alex", "Go", "Backend", "");
        await svc.ImproveAsync("Dear team, …", "Shopify", "Backend Intern", "Shorter.");
        Assert.Equal(2, seen.Count);
        AssertOnlyStub(seen);
    }

    [Fact]
    public async Task Interview_prep_posts_to_the_configured_base()
    {
        var (http, seen) = Recorder();
        var svc = new InterviewPrepService(http, Config(Stub), NullLogger<InterviewPrepService>.Instance);
        await svc.GenerateAsync("Shopify", "Backend Intern", "Go and Postgres.", "Resume text.", "Go");
        AssertOnlyStub(seen);
    }

    [Fact]
    public async Task Salary_insight_posts_to_the_configured_base()
    {
        var (http, seen) = Recorder();
        var svc = new SalaryInsightService(http, Config(Stub), NullLogger<SalaryInsightService>.Instance);
        await svc.EstimateAsync("Backend Intern", "Shopify", "Toronto", "Hybrid");
        AssertOnlyStub(seen);
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static IConfiguration Config(string? baseUrl) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Not the placeholder, or every service short-circuits before building a request.
            ["OpenAI:ApiKey"]  = "sk-test-not-a-real-key",
            ["OpenAI:BaseUrl"] = baseUrl,
        })
        .Build();

    private static void AssertOnlyStub(List<Uri> seen)
    {
        Assert.NotEmpty(seen);
        Assert.All(seen, u => Assert.Equal(Stub + "/v1/chat/completions", u.ToString()));
    }

    private static (HttpClient, List<Uri>) Recorder()
    {
        var seen = new List<Uri>();
        return (new HttpClient(new RecordingHandler(seen)), seen);
    }

    /// <summary>Records the URL and answers with an empty completion; never touches the network.</summary>
    private sealed class RecordingHandler(List<Uri> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Add(request.RequestUri!);
            const string body = "{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>The analyzer only uses its factory to fetch URLs; text input never reaches it.</summary>
    private sealed class NoFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("no page fetch expected for text input");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "InternTrackAI.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
