using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>
/// Reads a resume into a <see cref="ParsedProfile"/> via GPT-4o-mini. The output is a
/// <b>proposal</b>: it is stored as a <see cref="Models.ParsedResume"/> draft and shown on
/// <c>/Profile/ReviewResume</c>, and nothing reaches the profile until the user confirms it.
/// The endpoint is <c>OpenAI:BaseUrl</c> (default api.openai.com) so local verification can stub it.
/// </summary>
/// <remarks>
/// <para>
/// <b>No field context here.</b> Every other AI prompt carries the user's field block (CLAUDE.md §8),
/// and this one deliberately does not: the parser is what <i>produces</i> the field. Feeding the
/// stored one back would make it self-confirming and would stop a career change ever being detected —
/// a nurse retraining into software would keep parsing as a nurse forever.
/// </para>
/// <para>
/// Resume text is untrusted, so it goes into a tagged section through <see cref="PromptData"/> with
/// tag look-alikes stripped and the data-only rule in the system prompt, the same treatment
/// <see cref="FollowUpService"/> gives a job description.
/// </para>
/// <para>
/// This is the one service on <c>json_schema</c> (strict) rather than <c>json_object</c>: the reply
/// is deep and nested, and repairing a malformed one would be guesswork.
/// </para>
/// </remarks>
public class ProfileExtractorService : IProfileExtractor
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private readonly ILogger<ProfileExtractorService> _logger;

    /// <summary>Longest resume text sent. Beyond this a resume is a portfolio, and the tail is publications.</summary>
    public const int MaxResumeChars = 15000;

    private const string ResumeTag = "resume";
    private static readonly Regex TagLookAlike = PromptData.TagPattern(new[] { ResumeTag });

    private static readonly JsonSerializerOptions _camel = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProfileExtractorService(HttpClient http, IConfiguration config, ILogger<ProfileExtractorService> logger)
    {
        _http = http;
        _apiKey = config["OpenAI:ApiKey"] ?? string.Empty;
        _endpoint = (config["OpenAI:BaseUrl"]?.TrimEnd('/') ?? "https://api.openai.com") + "/v1/chat/completions";
        _logger = logger;
    }

    /// <summary>
    /// The system prompt. Public so a test can assert the field-neutrality rules are still in it —
    /// they are the difference between a nursing resume parsing as nursing and parsing as software.
    /// </summary>
    public static string SystemPrompt =>
        "You extract structured profile data from a resume. The person may work in ANY field — technology, " +
        "healthcare, trades, business, education, design, law, or anything else. Never assume a technology " +
        "background, and never use another field's vocabulary for their work.\n\n" +
        PromptData.DataRule(new[] { ResumeTag }, "the JSON object described below") + "\n\n" +
        "Rules:\n" +
        "- Extract only what is explicitly supported by the resume text. Do not infer, embellish, or add " +
        "skills the person did not list or demonstrate.\n" +
        "- For every skill, include a short verbatim-ish snippet from the resume as evidence. If you cannot " +
        "point to evidence, omit the skill.\n" +
        "- confidence is \"high\" when the resume states the skill plainly, \"medium\" when it is implied by " +
        "described work, and \"low\" when you are reading between the lines. Be honest: a low mark is useful, " +
        "a wrong high mark is not.\n" +
        "- field is the person's own words for what they do (\"Registered Nursing\", \"Millwright\"), not a job title.\n" +
        $"- fieldCategory must be exactly one of: {string.Join(", ", Enum.GetNames<FieldCategory>())}.\n" +
        $"- seniority must be exactly one of: {string.Join(", ", Enum.GetNames<SeniorityLevel>())}, or null.\n" +
        "- targetRoles: suggest 3-6 roles this person is realistically positioned for, based on their field " +
        "and level, using the job titles their own field uses. These are suggestions, not extractions.\n" +
        "- Use null for anything the resume does not state. Never guess to fill a field.";

    /// <summary>
    /// The reply contract, as an OpenAI strict JSON schema. Strict mode requires every property in
    /// <c>required</c> and <c>additionalProperties: false</c>, so optional values are typed as
    /// nullable rather than omitted.
    /// </summary>
    private static object ResponseSchema() => new
    {
        type = "json_schema",
        json_schema = new
        {
            name = "resume_profile",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "fullName", "field", "fieldCategory", "seniority", "yearsExperience", "location", "skills", "targetRoles", "education", "summary" },
                properties = new Dictionary<string, object>
                {
                    ["fullName"]        = Nullable("string", "The candidate's full name as written on the resume."),
                    ["field"]           = Nullable("string", "The person's field in their own words, e.g. \"Registered Nursing\"."),
                    ["fieldCategory"]   = NullableEnum(Enum.GetNames<FieldCategory>()),
                    ["seniority"]       = NullableEnum(Enum.GetNames<SeniorityLevel>()),
                    ["yearsExperience"] = Nullable("integer", "Whole years of relevant experience, or null if not stated."),
                    ["location"]        = Nullable("string", "City and region, e.g. \"Toronto, ON\"."),
                    ["skills"] = new
                    {
                        type = "array",
                        description = "Skills the resume demonstrates. Omit any skill you cannot quote evidence for.",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            required = new[] { "name", "evidence", "confidence" },
                            properties = new Dictionary<string, object>
                            {
                                ["name"]       = new { type = "string" },
                                ["evidence"]   = new { type = "string", description = "A short near-verbatim snippet from the resume supporting this skill." },
                                ["confidence"] = new { type = "string", @enum = new[] { "high", "medium", "low" } }
                            }
                        }
                    },
                    ["targetRoles"] = new { type = "array", items = new { type = "string" } },
                    ["education"] = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            additionalProperties = false,
                            required = new[] { "institution", "credential", "endDate" },
                            properties = new Dictionary<string, object>
                            {
                                ["institution"] = new { type = "string" },
                                ["credential"]  = Nullable("string", "e.g. \"BScN, Nursing\"."),
                                ["endDate"]     = Nullable("string", "\"YYYY-MM\" or \"YYYY\", or null.")
                            }
                        }
                    },
                    ["summary"] = Nullable("string", "One neutral sentence describing the candidate.")
                }
            }
        }
    };

    private static object Nullable(string type, string description) =>
        new { type = new[] { type, "null" }, description };

    private static object NullableEnum(string[] values) =>
        new { type = new[] { "string", "null" }, @enum = values.Append(null).ToArray() };

    public async Task<ProfileExtraction> ExtractAsync(string resumeText, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "your-openai-api-key-here")
            return ProfileExtraction.Failed("OpenAI API key is not configured.");

        var cleaned = PromptData.Clean(resumeText, MaxResumeChars, TagLookAlike);
        if (cleaned.Length == 0)
            return ProfileExtraction.Failed("No readable text found in the resume.");

        var body = new
        {
            model = "gpt-4o-mini",
            response_format = ResponseSchema(),
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = PromptData.Section(ResumeTag, cleaned) }
            },
            max_tokens  = 2000,
            temperature = 0.1
        };

        var json = JsonSerializer.Serialize(body, _camel);
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.SendAsync(request, ct);
            var raw      = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                // Status only: an OpenAI error body can echo parts of the request, and the request here
                // is somebody's resume. Same rule ResumeRewriteService follows.
                _logger.LogError("Resume parse failed: OpenAI returned {Status}.", (int)response.StatusCode);
                return ProfileExtraction.Failed((int)response.StatusCode switch
                {
                    401 => "Invalid API key.",
                    429 => "OpenAI quota exceeded. Add credits at platform.openai.com/settings/billing.",
                    _   => $"OpenAI returned {(int)response.StatusCode}."
                });
            }

            using var doc = JsonDocument.Parse(raw);
            var content = PromptData.UnwrapFence(doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}");

            var profile = ParsedProfile.FromJson(content);
            _logger.LogInformation("Resume parsed: {Skills} skills, {Roles} roles, field set: {HasField}.",
                profile.Skills.Count, profile.TargetRoles.Count, profile.Field is not null);

            return ProfileExtraction.Ok(profile, content);
        }
        catch (JsonException)
        {
            // Never log the content: it is the model's reading of a resume.
            _logger.LogWarning("Resume parse returned malformed JSON.");
            return ProfileExtraction.Failed("The resume analysis came back in an unexpected format. Try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling OpenAI API for resume parsing.");
            return ProfileExtraction.Failed("Request to OpenAI failed. Check your network connection.");
        }
    }

    /// <summary>
    /// The fixed parse handed to the shared demo account — no model call, no rate-limit permit, the
    /// same pattern as <see cref="FollowUpService.DemoDraft"/> and <see cref="ResumeRewriteService.DemoVariants"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately a <b>nursing</b> resume rather than a software one. The review screen is where a
    /// visitor sees that this app is not IT-only, and a demo that detects Healthcare and suggests
    /// clinical roles makes that argument in one screen — a software parse would just look like the
    /// default. The two <see cref="SkillConfidence.Low"/> entries exist so the unchecked-by-default
    /// rendering and the "low confidence" tag are visible on screen rather than only in the code;
    /// `ResumeReviewTests` pins that they arrive unchecked.
    /// </remarks>
    public static ParsedProfile DemoParse() => new()
    {
        FullName        = "Demo User",
        Field           = "Registered Nursing",
        Category        = FieldCategory.Healthcare,
        Seniority       = SeniorityLevel.Student,
        YearsExperience = 1,
        Location        = "Toronto, ON",
        Summary         = "Fourth-year nursing student with acute-care placement experience and a research assistantship.",
        TargetRoles     = new() { "Nursing Student Placement", "Clinical Research Intern", "Public Health Intern" },
        Education       = new()
        {
            new ParsedEducation("Toronto Metropolitan University", "BScN, Nursing", "2027-04"),
            new ParsedEducation("George Brown College", "Certificate, Medical Terminology", "2023-08")
        },
        Skills = new()
        {
            new ParsedSkill("Patient Assessment",   "Completed head-to-toe assessments for a 6-patient load on a medical-surgical unit", SkillConfidence.High),
            new ParsedSkill("Medication Administration", "Administered oral and subcutaneous medications under preceptor supervision", SkillConfidence.High),
            new ParsedSkill("Vital Signs Monitoring", "Recorded and escalated vital signs on a telemetry floor", SkillConfidence.High),
            new ParsedSkill("Electronic Health Records", "Charted assessments and care plans in Epic during placement", SkillConfidence.High),
            new ParsedSkill("Wound Care",           "Assisted with dressing changes on post-operative patients", SkillConfidence.Medium),
            new ParsedSkill("Patient Education",    "Explained discharge instructions to patients and families", SkillConfidence.Medium),
            new ParsedSkill("IV Therapy",           "Observed peripheral IV insertion during clinical rotation", SkillConfidence.Low),
            new ParsedSkill("Team Leadership",      "Coordinated a group project for a community health course", SkillConfidence.Low)
        }
    };
}
