namespace InternTrackAI.Models.Enums;

/// <summary>
/// Which generator wrote a <see cref="PracticeQuestion"/>. Both write into the one store; this is how the
/// practice generator tells their topics apart.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists: interview-prep topics are stored but must not block practice generation.</b>
/// Prep topics were measured (2026-09-24, three live calls across two postings) to come back
/// category-level — "Collaboration", "Automated testing", "Docker and Kubernetes" — even with
/// <c>PracticePrompt.TopicRule</c> in the prompt verbatim and the topic asked for after the question.
/// The questions themselves are broad coverage questions, so an honest topic for one is broad too.
/// <see cref="Services.TopicKey"/> treats a short topic as colliding with every longer topic containing
/// its words, so a prep topic of "collaboration" would silently suppress every narrow practice question
/// that mentions it. <c>PracticeQuestionService.ExclusionsAsync</c> therefore skips
/// <see cref="InterviewPrep"/> rows; the topics still feed the progress card's weakest-topic grouping,
/// where breadth does no harm.
/// </para>
/// <para>
/// Nullable on the row: null is a question stored before this column existed, from either generator.
/// Legacy prep rows all carry <c>Topic = ""</c>, which collides with nothing, so treating null as
/// practice is exact rather than approximate. Stored as an int with explicit values and no zero
/// (CLAUDE.md §7); append, never renumber.
/// </para>
/// </remarks>
public enum QuestionSource
{
    Practice      = 1,
    InterviewPrep = 2
}
