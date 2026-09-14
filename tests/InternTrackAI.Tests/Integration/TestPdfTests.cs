using InternTrackAI.Services;

namespace InternTrackAI.Tests.Integration;

public class TestPdfTests
{
    [Fact]
    public void Sample_pdf_yields_readable_text()
    {
        using var ms = new MemoryStream(TestPdf.SampleResume());
        var text = ResumeMatcherService.ExtractPdfText(ms);
        Assert.Contains("Alex Johnson", text);
        Assert.True(text.Length >= 50, $"only {text.Length} chars: '{text}'");
    }
}
