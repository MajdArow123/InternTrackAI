using InternTrackAI.Services;
using Microsoft.AspNetCore.Mvc;

namespace InternTrackAI.Controllers;

/// <summary>
/// Exposes the AI job description analyzer as a small JSON API consumed via fetch() from the
/// Create Application page (see Views/JobApplications/Create.cshtml). It does not render any views itself.
/// </summary>
public class AnalyzerController : Controller
{
    private readonly JobAnalyzerService _analyzer;

    public AnalyzerController(JobAnalyzerService analyzer)
    {
        _analyzer = analyzer;
    }

    /// <summary>
    /// Accepts a raw job posting (pasted text or a URL, detected and handled by
    /// <see cref="JobAnalyzerService"/>) and runs it through OpenAI to extract structured fields
    /// (company, role, location, salary, required skills).
    /// </summary>
    /// <param name="request">JSON body containing the job description text or URL.</param>
    /// <returns>
    /// A JSON-serialized <c>JobAnalysisResult</c> (camelCase keys, per ASP.NET Core's default
    /// naming policy) on success, or a 400 with an error payload if the description is missing.
    /// </returns>
    [HttpPost]
    [IgnoreAntiforgeryToken] // Called via fetch() from an authenticated page; the request performs no
                              // state-changing writes of its own (it only returns extracted text), so
                              // skipping the antiforgery check avoids needing to thread the token through
                              // the AJAX call.
    public async Task<IActionResult> Analyze([FromBody] AnalyzeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.JobDescription))
            return BadRequest(new { success = false, error = "Job description is required." });

        var result = await _analyzer.AnalyzeAsync(request.JobDescription);
        return Json(result);
    }
}

/// <summary>Request payload for <see cref="AnalyzerController.Analyze"/> — the raw pasted text or URL.</summary>
public record AnalyzeRequest(string JobDescription);
