using InternTrackAI.Models.ViewModels;
using InternTrackAI.Services;
using Microsoft.AspNetCore.Mvc;

namespace InternTrackAI.Controllers;

public class ResumeController : Controller
{
    private readonly ResumeMatcherService _matcher;

    public ResumeController(ResumeMatcherService matcher)
    {
        _matcher = matcher;
    }

    [HttpGet]
    public IActionResult Match() => View(new ResumeMatchViewModel());

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Match(IFormFile? resume, string? jobDescription)
    {
        var vm = new ResumeMatchViewModel { JobDescription = jobDescription };

        if (resume == null || resume.Length == 0)
        {
            ModelState.AddModelError(string.Empty, "Please upload a PDF resume.");
            return View(vm);
        }

        if (resume.Length > 5 * 1024 * 1024)
        {
            ModelState.AddModelError(string.Empty, "File must be under 5 MB.");
            return View(vm);
        }

        var ext = Path.GetExtension(resume.FileName);
        if (!ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(string.Empty, "Only PDF files are supported.");
            return View(vm);
        }

        if (string.IsNullOrWhiteSpace(jobDescription))
        {
            ModelState.AddModelError(string.Empty, "Please paste a job description.");
            return View(vm);
        }

        string resumeText;
        try
        {
            using var stream = resume.OpenReadStream();
            resumeText = ResumeMatcherService.ExtractPdfText(stream);
        }
        catch (Exception ex)
        {
            ModelState.AddModelError(string.Empty,
                "Could not read the PDF. Make sure it is a valid, text-based PDF (not a scanned image).");
            HttpContext.RequestServices
                .GetRequiredService<ILogger<ResumeController>>()
                .LogWarning(ex, "PDF extraction failed for file {Name}", resume.FileName);
            return View(vm);
        }

        if (string.IsNullOrWhiteSpace(resumeText) || resumeText.Length < 50)
        {
            ModelState.AddModelError(string.Empty,
                "No readable text found in the PDF. Make sure it is not a scanned image.");
            return View(vm);
        }

        vm.Result = await _matcher.MatchAsync(resumeText, jobDescription);
        return View(vm);
    }
}
