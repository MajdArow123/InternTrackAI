using System.IO.Compression;

namespace InternTrackAI.Services;

/// <summary>The resume formats the app accepts. Determined from bytes, never from a filename.</summary>
public enum ResumeFormat
{
    Unknown = 0,
    Pdf = 1,
    Docx = 2
}

/// <summary>
/// Decides what an uploaded resume actually <em>is</em>, and the one place the two formats' file
/// extensions and media types are written down.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bytes, not the filename.</b> A browser's <c>accept</c> attribute is a hint, the client-sent
/// content type is whatever the client felt like sending, and an extension is a rename away — so an
/// <c>.exe</c> called <c>resume.pdf</c> has to fail here or it reaches the parser.
/// </para>
/// <para>
/// PDF is one magic-number check (<c>%PDF</c>). DOCX is harder: a .docx is a ZIP, so <c>PK\x03\x04</c>
/// alone would also accept any zip file, including one full of executables. <see cref="Detect"/>
/// therefore opens the archive and requires a <c>word/document.xml</c> entry — a renamed .exe fails
/// the magic number, a renamed .zip fails the content check, and only a real Word document passes both.
/// </para>
/// </remarks>
public static class ResumeFileType
{
    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>Enough bytes for both signatures; read once at the head of the stream.</summary>
    private const int SignatureLength = 4;

    private static readonly byte[] PdfSignature  = { 0x25, 0x50, 0x44, 0x46 };   // %PDF
    private static readonly byte[] ZipSignature  = { 0x50, 0x4B, 0x03, 0x04 };   // PK\x03\x04

    /// <summary>The entry every WordprocessingML document has; a plain zip does not.</summary>
    private const string DocxMarkerEntry = "word/document.xml";

    /// <summary>What the file input offers and what the copy says. Order matters: it is user-facing.</summary>
    public static readonly string[] Extensions = { ".pdf", ".docx" };

    /// <summary>Human list for UI copy — "PDF or Word (.docx)".</summary>
    public const string AcceptDescription = "PDF or Word (.docx)";

    public static string ExtensionFor(ResumeFormat format) => format switch
    {
        ResumeFormat.Docx => ".docx",
        _ => ".pdf"
    };

    /// <summary>
    /// Media type for serving a stored resume back. Derived from the stored path's extension, which
    /// this class wrote — never from the name the user uploaded under.
    /// </summary>
    public static string MediaTypeFor(string storedPath) =>
        FormatOf(storedPath) == ResumeFormat.Docx
            ? "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
            : "application/pdf";

    /// <summary>The format of an already-stored file, from the extension this class gave it.</summary>
    public static ResumeFormat FormatOf(string storedPath) =>
        Path.GetExtension(storedPath).Equals(".docx", StringComparison.OrdinalIgnoreCase)
            ? ResumeFormat.Docx
            : ResumeFormat.Pdf;

    /// <summary>
    /// Identifies an uploaded stream by its contents. Returns <see cref="ResumeFormat.Unknown"/> for
    /// anything that isn't a real PDF or a real .docx, including a zip renamed to .docx. The stream is
    /// left rewound so the caller can copy it to disk.
    /// </summary>
    public static async Task<ResumeFormat> DetectAsync(Stream stream, CancellationToken ct = default)
    {
        var head = new byte[SignatureLength];
        var read = await stream.ReadAsync(head.AsMemory(0, SignatureLength), ct);
        if (read < SignatureLength) return ResumeFormat.Unknown;

        if (head.AsSpan().SequenceEqual(PdfSignature)) return ResumeFormat.Pdf;
        if (!head.AsSpan().SequenceEqual(ZipSignature)) return ResumeFormat.Unknown;

        // A zip. Only a zip holding a Word document body counts.
        if (!stream.CanSeek) return ResumeFormat.Unknown;
        stream.Position = 0;

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var isDocx = archive.Entries.Any(e =>
                e.FullName.Equals(DocxMarkerEntry, StringComparison.OrdinalIgnoreCase));
            return isDocx ? ResumeFormat.Docx : ResumeFormat.Unknown;
        }
        catch (InvalidDataException)
        {
            // Claims to be a zip in its first four bytes but isn't one.
            return ResumeFormat.Unknown;
        }
    }
}
