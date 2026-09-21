namespace InternTrackAI.Models.Enums;

/// <summary>
/// How demanding a practice question is. The definitions live in the generator prompt (Step 2) and
/// matter there: without them a model treats difficulty as a tone adjective and produces three
/// near-identical tiers.
/// </summary>
/// <remarks>
/// Stored as its underlying int with explicit values (CLAUDE.md §7). The order is meaningful, so a
/// comparison is valid — another reason not to reshuffle it.
/// </remarks>
public enum PracticeDifficulty
{
    /// <summary>Recall and definitions. Answerable in one or two sentences after an intro course.</summary>
    Easy = 1,

    /// <summary>Application and trade-offs. A real first-round interview question.</summary>
    Medium = 2,

    /// <summary>Synthesis, edge cases and design under competing constraints.</summary>
    Hard = 3
}
