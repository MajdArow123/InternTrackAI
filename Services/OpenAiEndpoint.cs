namespace InternTrackAI.Services;

/// <summary>
/// The Chat Completions URL every AI service posts to — the one place it is written.
/// </summary>
/// <remarks>
/// <para>
/// <c>OpenAI:BaseUrl</c> points the app at a stub for local verification (§8's working rules: routine runs
/// must not spend). Each service used to build the URL itself, and five of the thirteen hardcoded
/// <c>https://api.openai.com</c> instead — so a dev server run "against a stub" still sent the analyzer,
/// matcher, cover letter, interview prep and salary calls to the real API. That happened once while
/// verifying Phase 4; only the placeholder key kept it free. One helper means a new service cannot drift
/// the same way, and <c>OpenAiEndpointTests</c> fails if the literal URL reappears anywhere else.
/// </para>
/// <para>
/// Blank or whitespace counts as unset (an empty Railway variable would otherwise produce the relative URL
/// <c>/v1/chat/completions</c> and fail every call), and a trailing slash is tolerated.
/// </para>
/// </remarks>
public static class OpenAiEndpoint
{
    public const string ConfigKey = "OpenAI:BaseUrl";
    public const string DefaultBaseUrl = "https://api.openai.com";

    public static string ChatCompletions(IConfiguration config)
    {
        var configured = config[ConfigKey];
        var baseUrl = string.IsNullOrWhiteSpace(configured) ? DefaultBaseUrl : configured.Trim().TrimEnd('/');
        return baseUrl + "/v1/chat/completions";
    }
}
