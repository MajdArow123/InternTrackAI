namespace InternTrackAI.Models.Enums;

/// <summary>
/// Broad career field a user works in, used to keep AI output and role suggestions out of the
/// software-engineering default. Set from the profile page or inferred from an uploaded resume;
/// null means "not known yet", which every reader must tolerate.
/// </summary>
/// <remarks>
/// Stored as its underlying int, like every other enum in this app (CLAUDE.md §7). Unlike the
/// older ones, the values are written out explicitly: this is the enum most likely to gain
/// members, and an explicit number means a new member can be appended anywhere in the list
/// without shifting what is already in the database. <b>Append with the next unused number;
/// never renumber or reuse one.</b>
/// </remarks>
public enum FieldCategory
{
    Technology   = 1,
    Engineering  = 2,
    Business     = 3,
    Finance      = 4,
    Healthcare   = 5,
    Education    = 6,
    Design       = 7,
    Science      = 8,
    Legal        = 9,
    Trades       = 10,
    Media        = 11,
    PublicSector = 12,
    Other        = 13
}
