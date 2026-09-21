using System.Text;
using InternTrackAI.Services;
using InternTrackAI.Tests.Integration;

namespace InternTrackAI.Tests;

/// <summary>
/// What counts as a resume, decided from bytes. The rule this pins is that <b>the filename never
/// decides</b>: an extension, a browser's accept attribute and a client-sent content type are all a
/// rename away, so every one of them has to be irrelevant here.
/// </summary>
public class ResumeFileTypeTests
{
    private static async Task<ResumeFormat> Detect(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return await ResumeFileType.DetectAsync(ms);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public async Task A_real_pdf_is_a_pdf() =>
        Assert.Equal(ResumeFormat.Pdf, await Detect(TestPdf.SampleResume()));

    [Fact]
    public async Task A_real_docx_is_a_docx() =>
        Assert.Equal(ResumeFormat.Docx, await Detect(TestDocx.SampleResume()));

    [Fact]
    public async Task A_zip_with_no_word_document_inside_is_not_a_docx()
    {
        // It passes the PK\x03\x04 signature, which is why the check keeps going and looks for
        // word/document.xml. Stopping at the magic number would accept any zip on the internet.
        Assert.Equal(ResumeFormat.Unknown, await Detect(TestDocx.PlainZip()));
    }

    public static TheoryData<byte[], string> NotResumes() => new()
    {
        // Real signatures, not ASCII approximations of them: Encoding.ASCII turns any byte above
        // 0x7F into '?', so a string literal here would test a file that isn't the thing it names.
        { new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00 }, "a Windows executable (MZ)" },
        { new byte[] { 0x7F, 0x45, 0x4C, 0x46, 0x02, 0x01 }, "a Linux executable (ELF)" },
        { new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A }, "a PNG" },
        { Ascii("Plain text that says resume at the top"), "plain text" },
        { Ascii("<html><body>not a resume</body></html>"), "an HTML page" },
        { Ascii("%PD"), "a truncated PDF signature" },
        { Array.Empty<byte>(), "an empty file" },
    };

    [Theory]
    [MemberData(nameof(NotResumes))]
    public async Task Anything_else_is_unknown(byte[] content, string what) =>
        Assert.Equal(ResumeFormat.Unknown, await Detect(content));

    [Fact]
    public async Task The_stream_is_left_readable_so_the_caller_can_still_save_the_file()
    {
        // Detection happens before the copy to disk. A detector that consumed the stream would store
        // a truncated file, and the failure would only show up later at extraction time.
        var bytes = TestDocx.SampleResume();
        using var ms = new MemoryStream(bytes);

        await ResumeFileType.DetectAsync(ms);
        ms.Position = 0;

        using var copy = new MemoryStream();
        await ms.CopyToAsync(copy);
        Assert.Equal(bytes.Length, copy.Length);
    }

    [Theory]
    [InlineData("resumes/u/abc.pdf", ResumeFormat.Pdf)]
    [InlineData("resumes/u/abc.docx", ResumeFormat.Docx)]
    [InlineData("resumes/u/abc.DOCX", ResumeFormat.Docx)]
    public void A_stored_path_reports_the_format_it_was_saved_as(string storedPath, ResumeFormat expected) =>
        Assert.Equal(expected, ResumeFileType.FormatOf(storedPath));

    [Fact]
    public void Media_types_match_the_stored_extension()
    {
        Assert.Equal("application/pdf", ResumeFileType.MediaTypeFor("resumes/u/a.pdf"));
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ResumeFileType.MediaTypeFor("resumes/u/a.docx"));
    }

    [Fact]
    public void Every_accepted_extension_has_a_format_and_a_media_type()
    {
        // Adding a format means touching Extensions, ExtensionFor, FormatOf and MediaTypeFor. This
        // fails if one of them is forgotten.
        Assert.All(ResumeFileType.Extensions, ext =>
        {
            var format = ResumeFileType.FormatOf("resumes/u/file" + ext);
            Assert.NotEqual(ResumeFormat.Unknown, format);
            Assert.Equal(ext, ResumeFileType.ExtensionFor(format));
            Assert.NotEmpty(ResumeFileType.MediaTypeFor("resumes/u/file" + ext));
        });
    }
}

/// <summary>
/// Text out of a .docx. The table case is the one that matters: a large share of real resumes lay
/// their skills section out as a table, and an extractor that walks only body paragraphs drops every
/// skill without failing.
/// </summary>
public class DocxTextTests
{
    private static string Extract(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        return DocxText.Extract(ms);
    }

    [Fact]
    public void Paragraphs_come_out_as_lines()
    {
        var text = Extract(TestDocx.WithText("Alex Johnson", "Backend Developer", "Python, SQL"));

        Assert.Contains("Alex Johnson", text);
        Assert.Contains("Backend Developer", text);
        Assert.Contains("Python, SQL", text);
        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Table_cells_are_read_and_kept_on_one_line_per_row()
    {
        var text = Extract(TestDocx.WithSkillsTable("Skills", "Patient Assessment", "Wound Care", "IV Therapy"));

        Assert.Contains("Patient Assessment", text);
        Assert.Contains("Wound Care", text);
        Assert.Contains("IV Therapy", text);

        // One row, one line: the cells stay together so the parser reads them as a skills list.
        var rowLine = text.Split('\n').Single(l => l.Contains("Patient Assessment"));
        Assert.Contains("Wound Care", rowLine);
    }

    [Fact]
    public void An_empty_document_yields_an_empty_string() =>
        Assert.Equal("", Extract(TestDocx.WithText()));

    [Fact]
    public void A_zip_with_no_document_body_yields_nothing_rather_than_throwing()
    {
        // OpenXml opens any zip; it is MainDocumentPart that comes back null. Returning "" is the
        // right outcome — ResumeTextService classifies it as NoText and the user is told the file
        // had no readable text. Such a file can't be stored in the first place: ResumeFileType
        // rejects it at upload, which is where the real guard lives.
        using var ms = new MemoryStream(TestDocx.PlainZip());
        Assert.Equal("", DocxText.Extract(ms));
    }
}
