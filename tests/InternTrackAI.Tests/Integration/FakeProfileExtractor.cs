using System.Text.Json;
using InternTrackAI.Models.Enums;
using InternTrackAI.Services;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Scripted stand-in for the OpenAI resume parser: returns <see cref="Next"/> and records every call.
/// The default is a software parse with one low-confidence skill, so the common test asserts both the
/// checked and the unchecked rendering without every case having to script its own.
/// </summary>
public sealed class FakeProfileExtractor : IProfileExtractor
{
    /// <summary>The skill the default parse marks low-confidence; it must render unchecked.</summary>
    public const string LowConfidenceSkill = "Kubernetes";

    public static ParsedProfile DefaultParse() => new()
    {
        FullName        = "Alex Johnson",
        Field           = "Software Engineering",
        Category        = FieldCategory.Technology,
        Seniority       = SeniorityLevel.Student,
        YearsExperience = 1,
        Location        = "Toronto, ON",
        Summary         = "Computer programming student with backend project experience.",
        TargetRoles     = new() { "Backend Developer Intern" },
        Education       = new() { new ParsedEducation("George Brown College", "Advanced Diploma, Computer Programming", "2027-04") },
        Skills = new()
        {
            new ParsedSkill("Python", "Built an ETL pipeline in Python processing 2M rows/day", SkillConfidence.High),
            new ParsedSkill("React",  "Built the dashboard front end in React",                 SkillConfidence.High),
            new ParsedSkill("SQL",    "Wrote reporting queries against PostgreSQL",             SkillConfidence.Medium),
            new ParsedSkill(LowConfidenceSkill, "Mentioned container orchestration in a course project", SkillConfidence.Low)
        }
    };

    public ProfileExtraction Next { get; set; } = Success(DefaultParse());

    public List<string> Texts { get; } = new();
    public int Calls => Texts.Count;

    /// <summary>Builds a successful extraction whose RawJson round-trips through the real reader.</summary>
    public static ProfileExtraction Success(ParsedProfile profile) =>
        ProfileExtraction.Ok(profile, ToJson(profile));

    public Task<ProfileExtraction> ExtractAsync(string resumeText, CancellationToken ct = default)
    {
        Texts.Add(resumeText);
        return Task.FromResult(Next);
    }

    /// <summary>
    /// Serialises to the wire shape the real model produces, so a test's scripted parse is stored and
    /// re-read through exactly the same path as a live one — a fake that wrote its own shape would let
    /// a break in <see cref="ParsedProfile.FromJson"/> pass the suite.
    /// </summary>
    public static string ToJson(ParsedProfile p) => JsonSerializer.Serialize(new
    {
        fullName        = p.FullName,
        field           = p.Field,
        fieldCategory   = p.Category?.ToString(),
        seniority       = p.Seniority?.ToString(),
        yearsExperience = p.YearsExperience,
        location        = p.Location,
        skills          = p.Skills.Select(s => new { name = s.Name, evidence = s.Evidence, confidence = s.Confidence.ToString().ToLowerInvariant() }),
        targetRoles     = p.TargetRoles,
        education       = p.Education.Select(e => new { institution = e.Institution, credential = e.Credential, endDate = e.EndDate }),
        summary         = p.Summary
    });
}
