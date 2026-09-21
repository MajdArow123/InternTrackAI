using System.Text;
using System.Text.RegularExpressions;
using InternTrackAI.Data;
using InternTrackAI.Models;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>
/// Builds the "USER PROFILE CONTEXT" block that every AI prompt carries, so the model knows what
/// field the person is actually in.
/// </summary>
public interface IUserContextBuilder
{
    /// <summary>
    /// The block for one user, or <see cref="string.Empty"/> when there is no profile row at all.
    /// Callers pass the result straight into a prompt service and do nothing else with it.
    /// </summary>
    Task<string> BuildAsync(string userId, CancellationToken ct = default);
}

/// <summary>
/// The single place the field context is assembled (spec §1.2). Every AI caller builds the block
/// here and passes it down, so there is one definition of what the model is told about the user and
/// one place to change it — rather than nine prompts each interpolating a different subset of the
/// profile and drifting apart.
/// </summary>
/// <remarks>
/// <para>
/// Two rules the shape depends on. <b>No empty labels</b>: a line is emitted only when it has a
/// value, because "Location:" with nothing after it reads to the model as "the location is blank"
/// rather than "unknown". And when <see cref="UserProfile.Field"/> is unset the block is the single
/// "unspecified" line and nothing else — a half-filled context with skills but no field is what
/// pushes the model back to its software-engineering default, which is the whole problem this
/// exists to fix.
/// </para>
/// <para>
/// Everything in here is text the user typed, going into a system prompt, so it goes through
/// <see cref="PromptData.Clean"/> against the tag look-alikes used by the prompts that embed it —
/// the same treatment <see cref="FollowUpService"/> gives a job description.
/// </para>
/// </remarks>
public class UserContextBuilder : IUserContextBuilder
{
    private readonly ApplicationDbContext _db;

    public const string Heading = "USER PROFILE CONTEXT";

    /// <summary>What the model is told when the user has not set a field. Deliberately an instruction, not a blank.</summary>
    public const string UnspecifiedFieldLine = "Field: unspecified — infer from the job description provided";

    // Budgets. The block rides along with every call, so it stays small: a long "field" is a paste,
    // and a hundred skills add tokens to every request for no gain. The text budget matches the
    // length ProfileFields already clamps Field/Location to, so nothing is truncated twice.
    private const int TextBudget = ProfileFields.MaxTextLength;
    private const int MaxSkills = 25;
    private const int MaxTargetRoles = 8;

    /// <summary>
    /// Tag look-alikes stripped from the user's own text. The union of the section tags used by the
    /// prompts this block is embedded in, so a profile field can't close a section it sits inside.
    /// </summary>
    private static readonly Regex TagLookAlike = PromptData.TagPattern(new[]
    {
        "application", "job_description", "bullet", "resume", "notes", "cover_letter", "answer", "question"
    });

    public UserContextBuilder(ApplicationDbContext db) => _db = db;

    public async Task<string> BuildAsync(string userId, CancellationToken ct = default)
    {
        var profile = await _db.UserProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return profile is null ? string.Empty : Build(profile);
    }

    /// <summary>
    /// How a prompt service embeds the block: the context followed by a blank line, or nothing at
    /// all when there is none. One helper so all seven call sites format it identically and a prompt
    /// can't end up with a stray blank line for a user who has no profile row.
    /// </summary>
    public static string Prefix(string? profileContext) =>
        string.IsNullOrWhiteSpace(profileContext) ? "" : profileContext.Trim() + "\n\n";

    /// <summary>
    /// The pure half, so the shape can be tested without a database. <paramref name="profile"/> is
    /// read, never written.
    /// </summary>
    public static string Build(UserProfile profile)
    {
        var sb = new StringBuilder();
        sb.Append(Heading);

        var field = Clean(profile.Field);

        if (field is null)
        {
            // No field: say so and stop. See the class remarks — a partial block is worse than this line.
            sb.Append('\n').Append(UnspecifiedFieldLine);
            return sb.ToString();
        }

        sb.Append('\n').Append("Field: ").Append(ProfileFields.Label(field, profile.FieldCategory));

        if (Seniority(profile) is { } seniority)
            sb.Append('\n').Append("Seniority: ").Append(seniority);

        if (Clean(profile.Location) is { } location)
            sb.Append('\n').Append("Location: ").Append(location);

        if (Tags(profile.SkillsJson, MaxSkills) is { } skills)
            sb.Append('\n').Append("Skills: ").Append(skills);

        if (Tags(profile.TargetRolesJson, MaxTargetRoles) is { } roles)
            sb.Append('\n').Append("Target roles: ").Append(roles);

        return sb.ToString();
    }

    /// <summary>"Student, ~1 year experience" / "Student" / "~3 years experience" / null when neither is set.</summary>
    private static string? Seniority(UserProfile profile)
    {
        var level = profile.Seniority is { } s ? ProfileFields.Display(s) : null;
        var years = profile.YearsExperience is { } y && y > 0
            ? $"~{y} year{(y == 1 ? "" : "s")} experience"
            : null;

        return (level, years) switch
        {
            (null, null) => null,
            (not null, null) => level,
            (null, not null) => years,
            _ => $"{level}, {years}"
        };
    }

    private static string? Clean(string? value)
    {
        var cleaned = PromptData.OneLine(PromptData.Clean(value, TextBudget, TagLookAlike));
        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }

    private static string? Tags(string? json, int max)
    {
        var tags = ProfileTags.FromJson(json)
            .Select(Clean)
            .Where(t => t is not null)
            .Take(max)
            .ToList();

        return tags.Count == 0 ? null : string.Join(", ", tags);
    }
}
