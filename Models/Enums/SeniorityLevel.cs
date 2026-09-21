namespace InternTrackAI.Models.Enums;

/// <summary>
/// How far into their career a user is, so AI output pitches at the right level — an intern and a
/// senior hire get different questions, different cover letters and different salary ranges.
/// Null means "not known yet".
/// </summary>
/// <remarks>
/// Stored as its underlying int with explicit values, same rule as <see cref="FieldCategory"/>:
/// append with the next unused number, never renumber. The order here is also the natural
/// ordering (Student is the least experienced), so comparisons are meaningful — another reason
/// not to reshuffle it.
/// </remarks>
public enum SeniorityLevel
{
    Student    = 1,
    EntryLevel = 2,
    Junior     = 3,
    Mid        = 4,
    Senior     = 5
}
