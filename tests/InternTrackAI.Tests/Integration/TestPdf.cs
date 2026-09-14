using System.Text;

namespace InternTrackAI.Tests.Integration;

/// <summary>
/// Builds a small but valid text-based PDF (Helvetica, one page) so the upload endpoint's magic-byte
/// check passes and PdfPig can extract real words from it. Enough for the auto-fill pipeline; not a
/// general PDF writer.
/// </summary>
internal static class TestPdf
{
    public static byte[] WithText(params string[] lines)
    {
        var content = new StringBuilder("BT /F1 12 Tf 72 720 Td 14 TL\n");
        foreach (var line in lines)
            content.Append('(').Append(Escape(line)).Append(") Tj T*\n");
        content.Append("ET\n");
        var stream = content.ToString();

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        var xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A resume-looking PDF with comfortably more than the 50 characters the extractor needs.</summary>
    public static byte[] SampleResume() => WithText(
        "Alex Johnson - Software Engineering Student",
        "Skills: Python, React, SQL, Git, Docker",
        "Experience: Backend intern at Example Corp building REST APIs in C# and PostgreSQL.",
        "Education: BSc Computer Science, 2027.");

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
