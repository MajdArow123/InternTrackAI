using System.Net;
using System.Text;
using System.Text.Json;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Stands in for the OpenAI endpoint: replies with a scripted chat-completion body and records every
/// prompt it was sent, so a test can assert on what the generator actually asked for.
/// </summary>
/// <remarks>
/// A handler rather than a subclass because <c>PracticeQuestionService</c> owns its own HTTP call —
/// stubbing at the transport keeps the dedupe loop, the retry cap and the parsing all under test
/// instead of mocked away.
/// </remarks>
public sealed class ScriptedOpenAi : HttpMessageHandler
{
    private readonly Queue<string> _replies = new();
    private readonly string? _fallback;

    /// <summary>Every user prompt sent, in order. Index 0 is the first call, 1 the top-up.</summary>
    public List<string> Prompts { get; } = new();
    public int Calls => Prompts.Count;

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public ScriptedOpenAi(params string[] replies)
    {
        foreach (var r in replies) _replies.Enqueue(r);
        _fallback = replies.LastOrDefault();
    }

    /// <summary>Builds the JSON body the generator expects: <c>{"questions":[…]}</c>.</summary>
    public static string Questions(params (string Prompt, string Topic)[] questions) =>
        JsonSerializer.Serialize(new
        {
            questions = questions.Select(q => new { prompt = q.Prompt, topic = q.Topic, modelHint = new[] { "a point", "another point" } })
        });

    /// <summary>Builds the JSON body the answer grader expects. Improvements default to the two it asks for.</summary>
    public static string Feedback(
        int score = 3,
        string[]? strengths = null,
        string[]? improvements = null,
        string[]? missingPoints = null,
        string revisedOpening = "On the night shift, I was the only nurse covering triage.") =>
        JsonSerializer.Serialize(new
        {
            score,
            strengths     = strengths     ?? new[] { "You gave a concrete example." },
            improvements  = improvements  ?? new[] { "Say what the outcome was.", "Name the protocol." },
            missingPoints = missingPoints ?? new[] { "How you escalated." },
            revisedOpening
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var sent = await request.Content!.ReadAsStringAsync(cancellationToken);
        using (var doc = JsonDocument.Parse(sent))
        {
            var messages = doc.RootElement.GetProperty("messages");
            Prompts.Add(messages[messages.GetArrayLength() - 1].GetProperty("content").GetString() ?? "");
        }

        if (Status != HttpStatusCode.OK)
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"error\":{}}", Encoding.UTF8, "application/json") };

        var content = _replies.Count > 0 ? _replies.Dequeue() : _fallback ?? "{\"questions\":[]}";
        var body = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
