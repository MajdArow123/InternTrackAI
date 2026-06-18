namespace InternTrackAI.Models;

/// <summary>
/// A single AI-generated interview question. Not persisted on its own — instances are
/// serialized into <see cref="InterviewPrepSession.QuestionsJson"/> as a list.
/// </summary>
/// <param name="Category">e.g. "Technical", "Behavioral", or "Company-Specific".</param>
/// <param name="Question">The question text shown to the user.</param>
/// <param name="Tip">Guidance on how to approach answering the question.</param>
public record InterviewQuestion(string Category, string Question, string Tip);
