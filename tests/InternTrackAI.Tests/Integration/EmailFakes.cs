using System.Net;
using System.Text;
using System.Text.Json;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Stands in for Resend's API: records every send and answers from a scripted queue (the last reply repeats).
/// Shared by the password-reset tests so "was an email sent?" is one counter in one place.
/// </summary>
public sealed class FakeResend
{
    public List<string> Bodies { get; } = new();
    public List<Func<HttpResponseMessage>> Replies { get; } = new();

    public int Sends { get { lock (Bodies) return Bodies.Count; } }

    public JsonElement Payload(int index = 0) => JsonDocument.Parse(Bodies[index]).RootElement;

    /// <summary>Every recipient this fake has been asked to mail, in order.</summary>
    public IEnumerable<string> Recipients =>
        Bodies.Select(b => JsonDocument.Parse(b).RootElement.GetProperty("to")[0].GetString()!);

    public void AlwaysFail(HttpStatusCode status) => Replies.Add(() => new HttpResponseMessage(status)
    {
        Content = new StringContent(
            $"{{\"statusCode\":{(int)status},\"name\":\"application_error\",\"message\":\"boom\"}}",
            Encoding.UTF8, "application/json")
    });

    public sealed class Handler(FakeResend state) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            int count;
            lock (state.Bodies) { state.Bodies.Add(body); count = state.Bodies.Count; }

            if (state.Replies.Count == 0)
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"id\":\"stub-0001\"}", Encoding.UTF8, "application/json") };

            return state.Replies[Math.Min(count - 1, state.Replies.Count - 1)]();
        }
    }
}
