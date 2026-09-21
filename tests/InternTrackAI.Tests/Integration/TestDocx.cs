using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Builds real .docx files with the same library the app reads them with — a hand-rolled zip would
/// only prove the test's own writer agrees with itself.
/// </summary>
internal static class TestDocx
{
    /// <summary>A document whose body is one paragraph per line.</summary>
    public static byte[] WithText(params string[] lines)
    {
        using var ms = new MemoryStream();

        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var body = new Body();
            foreach (var line in lines)
                body.Append(new Paragraph(new Run(new Text(line) { Space = SpaceProcessingModeValues.Preserve })));

            doc.AddMainDocumentPart().Document = new Document(body);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// A document whose skills sit in a table — the layout a large share of real resumes use, and the
    /// one a body-paragraphs-only extractor silently drops.
    /// </summary>
    public static byte[] WithSkillsTable(string heading, params string[] skills)
    {
        using var ms = new MemoryStream();

        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var body = new Body();
            body.Append(new Paragraph(new Run(new Text(heading))));

            var row = new TableRow();
            foreach (var skill in skills)
                row.Append(new TableCell(new Paragraph(new Run(new Text(skill)))));

            body.Append(new Table(row));
            doc.AddMainDocumentPart().Document = new Document(body);
        }

        return ms.ToArray();
    }

    /// <summary>A resume-looking .docx, the DOCX twin of <see cref="TestPdf.SampleResume"/>.</summary>
    public static byte[] SampleResume() => WithText(
        "Alex Johnson - Software Engineering Student",
        "Skills: Python, React, SQL, Git, Docker",
        "Experience: Backend intern at Example Corp building REST APIs in C# and PostgreSQL.",
        "Education: BSc Computer Science, 2027.");

    /// <summary>
    /// A plain zip with no <c>word/document.xml</c>. Renamed to .docx it passes the ZIP magic number
    /// and must still be rejected — the reason the check goes on to look inside the archive.
    /// </summary>
    public static byte[] PlainZip()
    {
        using var ms = new MemoryStream();

        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("notes.txt").Open());
            writer.Write("not a word document");
        }

        return ms.ToArray();
    }
}
