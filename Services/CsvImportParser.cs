using System.Globalization;
using System.Text;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;

namespace InternTrackAI.Services;

/// <summary>
/// Parses the CSV layout produced by <c>JobApplicationsController.Export</c>
/// (Company,Role,Location,Work Mode,Status,Deadline,Date Applied,Salary,Job Link) into
/// <see cref="JobApplication"/> rows. Pure and synchronous so it can be unit-tested without a
/// database: rows missing Company or Role are counted as skipped, unrecognised Work Mode / Status
/// values fall back to Remote / Saved, unparseable dates become null, and duplicates (same
/// company + role, case-insensitive, within the file or against the caller-supplied existing set)
/// are counted and dropped.
/// </summary>
public static class CsvImportParser
{
    public sealed class Result
    {
        public List<JobApplication> Applications { get; } = new();
        public int Skipped { get; set; }
        public int Duplicates { get; set; }
    }

    public static Result Parse(TextReader reader, string userId, IEnumerable<(string Company, string Role)>? existing = null)
    {
        var result = new Result();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing != null)
            foreach (var (company, role) in existing)
                seen.Add(Key(company, role));

        reader.ReadLine(); // header row

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var fields  = ParseLine(line);
            var company = (fields.ElementAtOrDefault(0) ?? "").Trim();
            var role    = (fields.ElementAtOrDefault(1) ?? "").Trim();

            if (company.Length == 0 || role.Length == 0)
            {
                result.Skipped++;
                continue;
            }

            if (!seen.Add(Key(company, role)))
            {
                result.Duplicates++;
                continue;
            }

            result.Applications.Add(new JobApplication
            {
                UserId      = userId,
                CompanyName = company,
                RoleTitle   = role,
                Location    = Optional(fields, 2),
                WorkMode    = Enum.TryParse<WorkMode>(fields.ElementAtOrDefault(3)?.Trim(), true, out var wm) ? wm : WorkMode.Remote,
                Status      = Enum.TryParse<ApplicationStatus>(fields.ElementAtOrDefault(4)?.Trim(), true, out var st) ? st : ApplicationStatus.Saved,
                Deadline    = ParseDate(fields.ElementAtOrDefault(5)),
                DateApplied = ParseDate(fields.ElementAtOrDefault(6)),
                Salary      = Optional(fields, 7),
                JobLink     = Optional(fields, 8)
            });
        }

        return result;
    }

    private static string Key(string company, string role) => company.Trim() + "\u001F" + role.Trim();

    private static string? Optional(List<string> fields, int i)
    {
        var v = fields.ElementAtOrDefault(i);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static DateTime? ParseDate(string? raw) =>
        DateTime.TryParse(raw?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>Splits one CSV line into fields, honouring double-quoted values that contain commas or escaped quotes.</summary>
    public static List<string> ParseLine(string line)
    {
        var fields  = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else if (c == '"') inQuotes = false;
                else current.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(c);
            }
        }
        fields.Add(current.ToString());
        return fields;
    }
}
