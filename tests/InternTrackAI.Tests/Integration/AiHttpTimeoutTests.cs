using InternTrackAI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Every outbound HTTP client this app registers has a deliberate timeout.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for is silent: leave the timeout off and the client inherits
/// <see cref="HttpClient"/>'s <b>100-second default</b>, which works perfectly until the day a call
/// hangs and a user watches a spinner for a minute and a half. Nothing else in the suite notices,
/// because the endpoint is functionally correct.
/// </para>
/// <para>
/// <b>It auto-detects new clients</b> rather than pinning a list, in the shape of
/// <c>MigrationColumnTypeTests.Every_column_lands_on_a_PostgreSQL_type_that_matches_its_CLR_type</c>:
/// it finds every type in the app assembly that takes an <see cref="HttpClient"/> in its constructor,
/// so a twelfth AI service added next month is checked without anyone remembering to add it here.
/// </para>
/// </remarks>
public class AiHttpTimeoutTests
{
    /// <summary>The default an unconfigured <see cref="HttpClient"/> gets. Seeing this means nobody chose.</summary>
    private static readonly TimeSpan UnconfiguredDefault = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Types constructed with an <see cref="HttpClient"/> — which is exactly how a typed client is
    /// registered, so this is the set of things that talk to something over HTTP.
    /// </summary>
    private static IEnumerable<Type> HttpClientTypes() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => t.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(HttpClient))))
            .OrderBy(t => t.Name, StringComparer.Ordinal);

    [Fact]
    public void Every_http_client_in_the_app_has_a_chosen_timeout()
    {
        using var factory = new TestAppFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var clients = services.GetRequiredService<IHttpClientFactory>();

        var found = HttpClientTypes().ToList();
        Assert.NotEmpty(found);   // a reflection bug that finds nothing must not pass silently

        var unbounded = new List<string>();
        var checkedAny = false;

        foreach (var type in found)
        {
            // A typed client's name is the type it was registered under — the concrete type for
            // AddHttpClient<T>, the interface for AddHttpClient<TInterface, TImplementation>.
            var names = RegisteredNamesFor(services, type).ToList();

            // Nothing resolves to this type, so its client is not registered in this environment.
            // ResendEmailSender is the real case: it only registers when Resend:ApiKey is set, and the
            // Testing environment has no key, so the console sender takes over. Asserting on it here
            // would fail on a client that does not exist rather than on one that is unbounded.
            if (names.Count == 0) continue;

            checkedAny = true;
            if (names.All(n => clients.CreateClient(n).Timeout == UnconfiguredDefault))
                unbounded.Add(type.Name);
        }

        Assert.True(checkedAny, "No registered HTTP client was checked — the name resolution above is broken.");

        Assert.True(unbounded.Count == 0,
            "These HTTP clients still use the 100-second default instead of a chosen timeout: "
            + string.Join(", ", unbounded)
            + ". Add .AiTimeout() in Program.cs for an AI client, or ConfigureHttpClient for anything else.");
    }

    /// <summary>
    /// The service types <paramref name="type"/> is actually registered under, judged by resolving them
    /// and checking what came back — an interface that resolves to a <em>different</em> implementation
    /// (IEmailSender to ConsoleEmailSender, say) is not this type's client name.
    /// </summary>
    private static IEnumerable<string> RegisteredNamesFor(IServiceProvider services, Type type)
    {
        foreach (var candidate in new[] { type }.Concat(type.GetInterfaces()))
        {
            object? resolved;
            try { resolved = services.GetService(candidate); }
            catch { continue; }          // registered but not constructible here; not our concern

            if (resolved is not null && resolved.GetType() == type) yield return candidate.Name;
        }
    }

    [Fact]
    public void The_ai_clients_are_capped_at_the_documented_thirty_seconds()
    {
        using var factory = new TestAppFactory();
        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();

        // Named explicitly here, unlike the sweep above, because these are the ones that spend money
        // and the exact value is the thing being pinned.
        foreach (var name in new[]
                 {
                     nameof(JobAnalyzerService), nameof(ResumeMatcherService), nameof(ResumeScoreService),
                     nameof(CoverLetterGeneratorService), nameof(FollowUpService), nameof(ResumeRewriteService),
                     nameof(InterviewPrepService), nameof(PracticeQuestionService), nameof(AnswerFeedbackService),
                     nameof(SalaryInsightService), nameof(IProfileExtractor), nameof(InternTrackAI.Services.Gmail.IStatusClassifier),
                 })
        {
            Assert.Equal(AiHttpDefaults.Timeout, clients.CreateClient(name).Timeout);
        }
    }
}
