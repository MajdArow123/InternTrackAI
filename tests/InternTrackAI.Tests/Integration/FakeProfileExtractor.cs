using InternTrackAI.Services;

namespace InternTrackAI.Tests.Integration;

/// <summary>Scripted stand-in for the OpenAI extractor: returns <see cref="Next"/> and records every call.</summary>
public sealed class FakeProfileExtractor : IProfileExtractor
{
    public ProfileExtraction Next { get; set; } = new(true, "Alex Johnson", new() { "Python", "React", "SQL" }, new() { "Backend Developer Intern" }, null);
    public List<string> Texts { get; } = new();
    public int Calls => Texts.Count;

    public Task<ProfileExtraction> ExtractAsync(string resumeText, CancellationToken ct = default)
    {
        Texts.Add(resumeText);
        return Task.FromResult(Next);
    }
}
