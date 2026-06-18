using InternTrackAI.Services;
using Microsoft.AspNetCore.Mvc;

namespace InternTrackAI.Controllers;

public class AnalyzerController : Controller
{
    private readonly JobAnalyzerService _analyzer;

    public AnalyzerController(JobAnalyzerService analyzer)
    {
        _analyzer = analyzer;
    }

    [HttpPost]
    [IgnoreAntiforgeryToken] // API endpoint called via AJAX from authenticated page; no state-changing operations
    public async Task<IActionResult> Analyze([FromBody] AnalyzeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.JobDescription))
            return BadRequest(new { success = false, error = "Job description is required." });

        var result = await _analyzer.AnalyzeAsync(request.JobDescription);
        return Json(result);
    }
}

public record AnalyzeRequest(string JobDescription);
