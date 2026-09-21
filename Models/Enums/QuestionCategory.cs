namespace InternTrackAI.Models.Enums;

/// <summary>
/// What kind of interview question this is. <b>The app has exactly one question-type vocabulary</b> —
/// these three values, which the interview-prep prompt has always used.
/// </summary>
/// <remarks>
/// <para>
/// Previously a bare string on a record serialised into <c>InterviewPrepSession.QuestionsJson</c>.
/// Promoting it to an enum is not a second vocabulary: the display and wire spelling is unchanged
/// (see <see cref="QuestionCategories.Display"/>), so the prompt contract and the JSON the Prep page's
/// script reads are byte-identical to before.
/// </para>
/// <para>
/// Stored as its underlying int with explicit values (CLAUDE.md §7): append with the next unused
/// number, never renumber. <c>Technical</c> means role-specific knowledge in the candidate's own
/// field, whatever that field is — clinical for a nurse, code for a developer — not software.
/// </para>
/// </remarks>
public enum QuestionCategory
{
    Technical = 1,
    Behavioral = 2,
    CompanySpecific = 3
}

/// <summary>The one place a <see cref="QuestionCategory"/> is spelled for humans and for the wire.</summary>
public static class QuestionCategories
{
    /// <summary>
    /// Display form, which is also the wire form. <c>CompanySpecific</c> is "Company-Specific" — the
    /// spelling the prompt asks the model for, the JSON the Prep page's script groups on, and the text
    /// on the badge. Changing it means changing all three.
    /// </summary>
    public static string Display(QuestionCategory category) => category switch
    {
        QuestionCategory.CompanySpecific => "Company-Specific",
        _ => category.ToString()
    };

    /// <summary>
    /// Reads the model's spelling back. Tolerant on purpose — a hyphen, a space or different casing all
    /// resolve — and anything unrecognised is <see cref="QuestionCategory.Technical"/> rather than a
    /// throw, because a category typo must not cost the user a whole generated question.
    /// </summary>
    public static QuestionCategory Parse(string? value)
    {
        var normalised = (value ?? "").Replace("-", "").Replace(" ", "").Replace("_", "").Trim();

        return normalised.ToLowerInvariant() switch
        {
            "behavioral" or "behavioural" => QuestionCategory.Behavioral,
            "companyspecific" or "company" => QuestionCategory.CompanySpecific,
            _ => QuestionCategory.Technical
        };
    }

    /// <summary>Pipeline order, used by the Prep page's section order and the filter pills.</summary>
    public static readonly QuestionCategory[] InOrder =
    {
        QuestionCategory.Technical, QuestionCategory.Behavioral, QuestionCategory.CompanySpecific
    };
}
