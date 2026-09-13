using System.Text.RegularExpressions;

namespace InternTrackAI.Services.Gmail;

/// <summary>
/// Guesses which sender domains belong to a company so the sync can match inbox mail to an
/// application. Two sources: the company name, normalised to <c>company.com</c>, and any domains
/// already present in the application's link or notes (careers.stripe.com → stripe.com). The set is
/// computed per sync run and never stored.
/// </summary>
public static class CompanyDomains
{
    private static readonly string[] LegalSuffixes =
    {
        "incorporated", "inc", "limited", "ltd", "corporation", "corp", "company", "co", "llc", "plc", "gmbh", "sa", "ag", "llp", "lp"
    };

    private static readonly Regex Email  = new(@"[A-Za-z0-9._%+\-]+@([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+)", RegexOptions.Compiled);
    private static readonly Regex NonDomainChars = new(@"[^a-z0-9\-]", RegexOptions.Compiled);
    private static readonly string[] HostPrefixes = { "www", "careers", "jobs", "boards", "apply", "mail", "email", "hr", "talent", "recruiting" };

    /// <summary>"Stripe, Inc." → "stripe.com"; "Coca-Cola Company" → "coca-cola.com"; null when nothing usable is left.</summary>
    public static string? FromCompanyName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var words = name.ToLowerInvariant()
            .Replace("&", "and")
            .Split(new[] { ' ', ',', '.', '(', ')', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        // Trailing legal forms only ("Acme Corp" → acme; "Corp Solutions" keeps its first word).
        while (words.Count > 1 && LegalSuffixes.Contains(words[^1]))
            words.RemoveAt(words.Count - 1);

        var label = NonDomainChars.Replace(string.Concat(words), "").Trim('-');
        return label.Length == 0 ? null : label + ".com";
    }

    /// <summary>Host of an http(s) URL plus its registrable part: "https://careers.stripe.com/x" → { careers.stripe.com, stripe.com }.</summary>
    public static IEnumerable<string> FromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) yield break;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) yield break;
        foreach (var d in Expand(uri.Host)) yield return d;
    }

    /// <summary>Domains of every email address found in free text (notes).</summary>
    public static IEnumerable<string> FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (Match m in Email.Matches(text))
            foreach (var d in Expand(m.Groups[1].Value))
                yield return d;
    }

    /// <summary>A host, its host with common job-board prefixes stripped, and its last two labels.</summary>
    private static IEnumerable<string> Expand(string host)
    {
        host = host.ToLowerInvariant().Trim().TrimEnd('.');
        if (host.Length == 0 || !host.Contains('.')) yield break;
        yield return host;

        var labels = host.Split('.');
        if (labels.Length > 2 && HostPrefixes.Contains(labels[0]))
            yield return string.Join('.', labels.Skip(1));
        if (labels.Length > 2)
            yield return string.Join('.', labels.Skip(labels.Length - 2));
    }

    /// <summary>Every candidate domain for an application (name + link + notes), de-duplicated, lower-case.</summary>
    public static HashSet<string> For(string companyName, string? jobLink, IEnumerable<string>? notes)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FromCompanyName(companyName) is { } fromName) set.Add(fromName);
        foreach (var d in FromUrl(jobLink)) set.Add(d);
        if (notes is not null)
            foreach (var note in notes)
                foreach (var d in FromText(note)) set.Add(d);
        set.RemoveWhere(IsGenericMailbox);
        return set;
    }

    /// <summary>True when <paramref name="senderDomain"/> is one of the candidates or a subdomain of one.</summary>
    public static bool Matches(string? senderDomain, IReadOnlyCollection<string> candidates)
    {
        if (string.IsNullOrEmpty(senderDomain)) return false;
        foreach (var c in candidates)
        {
            if (senderDomain.Equals(c, StringComparison.OrdinalIgnoreCase)) return true;
            if (senderDomain.EndsWith("." + c, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Personal-mail and job-board domains that would match every applicant's inbox.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "yahoo.com", "icloud.com", "me.com", "proton.me", "protonmail.com",
        "linkedin.com", "indeed.com", "glassdoor.com", "lever.co", "greenhouse.io", "workday.com", "myworkdayjobs.com", "smartrecruiters.com",
        "jobvite.com", "icims.com", "ashbyhq.com", "example.com"
    };

    public static bool IsGenericMailbox(string domain) => Generic.Contains(domain);
}
