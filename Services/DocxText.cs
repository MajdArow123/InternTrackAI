using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace InternTrackAI.Services;

/// <summary>
/// Plain text out of a .docx, the DOCX twin of <see cref="ResumeMatcherService.ExtractPdfText"/>.
/// Called only by <see cref="ResumeTextService"/>, which stays the one way to read a resume's text.
/// </summary>
/// <remarks>
/// Word's model is paragraphs of runs, so unlike the PDF path (one line per page, words space-joined)
/// this gets real line breaks for free — which is also why a .docx resume parses more cleanly than
/// the same resume exported to PDF.
/// <para>
/// Table cells matter: a large share of resumes lay out their entire skills section as a table, and
/// walking only the body's paragraphs silently drops every one of them. <c>Descendants&lt;Paragraph&gt;</c>
/// covers both, and a table row is flattened to one line so the parser sees the cells together.
/// </para>
/// </remarks>
public static class DocxText
{
    /// <summary>
    /// Extracts the document body's text. Throws on a corrupt or non-Word package — the caller
    /// (<see cref="ResumeTextService"/>) turns that into <see cref="ResumeTextStatus.Unreadable"/>.
    /// </summary>
    public static string Extract(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var sb = new StringBuilder();

        foreach (var element in body.Elements())
        {
            switch (element)
            {
                case Paragraph paragraph:
                    AppendLine(sb, ParagraphText(paragraph));
                    break;

                case Table table:
                    // One line per row, cells separated, so "Skills | Python, SQL" stays on one line
                    // instead of becoming two orphaned fragments.
                    foreach (var row in table.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>()
                            .Select(c => string.Join(" ", c.Descendants<Paragraph>().Select(ParagraphText)))
                            .Select(t => t.Trim())
                            .Where(t => t.Length > 0);

                        AppendLine(sb, string.Join(" • ", cells));
                    }
                    break;
            }
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// One paragraph's text. <c>Text</c> runs are joined without separators (Word splits a single
    /// word across runs whenever formatting changes mid-word), while tabs and breaks become spaces.
    /// </summary>
    private static string ParagraphText(Paragraph paragraph)
    {
        var sb = new StringBuilder();

        foreach (var run in paragraph.Descendants<Run>())
        {
            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text text:   sb.Append(text.Text); break;
                    case TabChar:     sb.Append(' '); break;
                    case Break:       sb.Append(' '); break;
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static void AppendLine(StringBuilder sb, string line)
    {
        if (line.Length == 0) return;
        sb.Append(line).Append('\n');
    }
}
