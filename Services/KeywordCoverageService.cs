using System.Text.RegularExpressions;
using InternTrackAI.Data;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>One term the posting uses, with where it came from and why it counts.</summary>
/// <param name="Term">The term as the posting spells it.</param>
/// <param name="Count">How many times it appears.</param>
/// <param name="InRequirements">Whether it appears under a requirements/qualifications/skills heading.</param>
/// <param name="Context">The posting line it first appeared on, for the chip's popover.</param>
/// <param name="NearMiss">
/// A longer resume word that contains this term ("PostgreSQL" for "SQL"), when that is the only reason it is
/// reported missing. Null otherwise. See <see cref="KeywordCoverageService.NearMiss"/>.
/// </param>
public sealed record KeywordTerm(string Term, int Count, bool InRequirements, string Context, string? NearMiss);

/// <summary>
/// The keyword-coverage answer for one posting/resume pair. <see cref="Available"/> false means there is
/// nothing worth showing and <see cref="Reason"/> says why in one line.
/// </summary>
public sealed record KeywordCoverage(
    bool Available,
    string? Reason,
    int Covered,
    int Total,
    IReadOnlyList<KeywordTerm> Missing)
{
    public static KeywordCoverage Unavailable(string reason) => new(false, reason, 0, 0, Array.Empty<KeywordTerm>());
}

/// <summary>
/// "Keyword coverage": which of a posting's literal terms the user's resume does not contain.
///
/// Deliberately **not** an AI feature, and deliberately a different question from the resume match score.
/// The match score asks whether the resume demonstrates equivalent experience; this asks whether the exact
/// words are on the page, because that is what an applicant tracking system and a skimming recruiter look for.
/// Extraction is deterministic (see <see cref="Extract"/>) — it was prototyped against real postings and
/// produced a usable list on every one, so there is no model call, no rate limit, no prompt-injection surface,
/// no demo carve-out, and nothing to cache or invalidate.
///
/// Matching is literal, on word boundaries: a posting asking for "SQL" is <em>not</em> covered by a resume that
/// only says "PostgreSQL". Substring matching would make "Java" match "JavaScript", and a false "you're
/// covered" is worse than a false "you're missing this" because the user acts on it by not fixing anything.
/// Real equivalences go through the explicit <see cref="SkillAliases"/> map instead. <see cref="NearMiss"/>
/// makes the strict rule legible where it bites.
/// </summary>
public class KeywordCoverageService
{
    // ── Limits and copy ──────────────────────────────────

    /// <summary>Most missing terms to list. The coverage denominator still counts every extracted term.</summary>
    public const int MaxMissing = 15;

    /// <summary>Below this many extracted terms the posting is too thin for the check to say anything.</summary>
    public const int MinTerms = 10;

    /// <summary>Longest posting considered, matching the job analyzer's own budget.</summary>
    public const int JobDescriptionBudget = 8000;

    /// <summary>Longest term kept; anything longer is a sentence fragment, not a keyword.</summary>
    public const int MaxTermChars = 60;

    public const string NoDescription    = "Add the job posting to see which of its terms your resume uses.";
    public const string NoResume         = "Upload a resume to check it against this posting's wording.";
    public const string UnreadableResume = "We couldn't read any text from your active resume.";
    public const string TooShort         = "This posting is too short for a useful keyword check.";

    // ── Extraction patterns ──────────────────────────────

    // Punctuated technology tokens (ASP.NET, Node.js, CI/CD, Vue.js, C++) and leading-dot ones (.NET).
    // Matched first and their spans blanked out, so the acronym and capitalised passes cannot re-split them
    // into "ASP" + "NET" or "CI" + "CD".
    private static readonly Regex Punctuated = new(
        @"(?<![A-Za-z0-9])\.[A-Za-z][A-Za-z0-9]*|\b[A-Za-z][A-Za-z0-9]*(?:[.#+/][A-Za-z0-9#+]{1,20})+|\b[A-Za-z]\+\+",
        RegexOptions.Compiled);

    // SQL, AWS, RTOS, I2C — and certification codes as one token (AZ-104), so "AZ" never stands alone.
    private static readonly Regex Acronym = new(@"\b[A-Z][A-Z0-9]{1,6}(?:-\d{2,4})?\b", RegexOptions.Compiled);

    // Entity Framework Core, Spring Boot, ARM Cortex.
    private static readonly Regex CapitalisedRun = new(
        @"\b[A-Z][a-zA-Z0-9]*(?:[\- ][A-Z][a-zA-Z0-9]*){0,2}\b", RegexOptions.Compiled);

    // "experience with Docker", "familiarity with SQL or NoSQL databases" — the phrases that introduce
    // lower-case requirements a capitalisation rule can never see.
    private static readonly Regex Cue = new(
        @"(?:experience (?:with|in|using|developing)|knowledge of|familiarity with|proficiency in|proficient in|understanding of|exposure to|skills in|such as)\s+(.{2,80}?)(?=[.,;:\n()]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CueSplit = new(@"\s+(?:and|or)\s+|,\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A lone capitalised word here is the first word of a sentence or bullet, so it is probably a verb
    // ("Develop and maintain…"), not a technology.
    private static readonly Regex SentenceStart = new(@"(?:^|[.!?:;]\s+|^\s*[*\-–—•]\s*)$", RegexOptions.Compiled);

    private static readonly Regex HeadingShape = new(
        @"^\s*(?:#+\s*)?([A-Za-z][A-Za-z0-9 &/'’\-]{2,60}?)\s*(:)?\s*$", RegexOptions.Compiled);

    private static readonly Regex RequirementsHeading = new(
        @"(requirement|qualification|what we'?re looking for|what you'?ll need|you (?:have|bring)|must have|skill|technical|nice to have|preferred|responsibilit|what you'?ll do|framework|tool|language|infrastructure)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    // ── Word lists ───────────────────────────────────────

    /// <summary>
    /// The words that introduce a requirement. Never a keyword themselves, and a line containing one is a
    /// requirement rather than a heading — which is what lets postings written without bullet glyphs work.
    /// </summary>
    private static readonly HashSet<string> CueWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "experience", "familiarity", "knowledge", "understanding", "exposure", "proficiency", "proficient",
        "skills", "skill", "required", "preferred", "requirements", "qualifications", "responsibilities"
    };

    /// <summary>
    /// Leading words that are never part of the term itself: determiners and qualifiers ("a regulated
    /// industry", "other IaC tools") and the connectives left behind when a list like "Zephyr, and Nomad"
    /// is split ("and Nomad").
    /// </summary>
    private static readonly HashSet<string> LeadingNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "other", "others", "their", "our", "your", "its", "this", "that", "some", "any",
        "all", "more", "most", "such", "various", "including", "include", "includes", "strong", "solid",
        "basic", "good", "excellent", "related", "previous", "personal", "modern", "new", "one", "two",
        "three", "several",
        "and", "or", "with", "using", "for", "to", "in", "on", "of", "from", "plus"
    };

    /// <summary>
    /// Generic nouns that turn a keyword into a phrase without adding anything: "NoSQL databases" is the
    /// keyword "NoSQL", "HTTP fundamentals" is "HTTP". Only stripped when what remains still looks like a
    /// name — an acronym, a capitalised word or a punctuated token — so "distributed systems" and "version
    /// control workflows" keep their trailing noun and stay meaningful phrases.
    /// </summary>
    private static readonly HashSet<string> TrailingGeneric = new(StringComparer.OrdinalIgnoreCase)
    {
        "databases", "database", "fundamentals", "tools", "technologies", "workflows", "platforms",
        "concepts", "practices", "principles", "methodology", "methodologies", "pipelines", "frameworks",
        "libraries", "services", "environments"
    };

    /// <summary>Short connectives allowed inside a heading ("Nice to Have", "Frameworks &amp; Tools").</summary>
    private static readonly HashSet<string> HeadingConnectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "of", "to", "the", "and", "for", "in", "with", "a", "an", "on", "or", "&"
    };

    /// <summary>
    /// Connectives that mark a candidate as prose rather than a name. A cue capture runs to the end of the
    /// clause, so "familiarity with Pulumi for infrastructure" yields the whole tail; the preposition inside
    /// it is what says "this is a sentence fragment" and lets the bare "Pulumi" stand instead. Real
    /// multi-word terms — "Entity Framework Core", "object-oriented programming", "digital signal
    /// processing" — contain none of these.
    /// </summary>
    private static readonly HashSet<string> InteriorConnectives = new(StringComparer.OrdinalIgnoreCase)
    {
        "for", "in", "on", "of", "to", "with", "and", "or", "at", "by", "from", "that", "which", "using",
        "across", "within", "into", "about", "such", "like"
    };

    private static readonly HashSet<string> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        "january", "february", "march", "april", "may", "june",
        "july", "august", "september", "october", "november", "december"
    };

    /// <summary>
    /// Posting boilerplate: hiring vocabulary, perks, logistics, and the generic verbs that open a bullet.
    /// A candidate made only of these is dropped. Kept deliberately broad — a missed junk chip costs one slot
    /// out of fifteen, while dropping a real technology costs the user the thing they came for, so nothing
    /// that could name a technology belongs in here.
    /// </summary>
    private static readonly HashSet<string> Boilerplate = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "of", "for", "to", "in", "on", "with", "at", "by", "from", "as", "is",
        "are", "be", "we", "you", "your", "our", "their", "it", "its", "will", "can", "may", "must", "who",
        "what", "when", "where", "how", "than", "then", "there", "here", "about", "into", "over", "under",
        "out", "up", "down", "off", "per", "each", "every", "not", "no", "only", "own", "same", "so", "too",
        "very", "job", "role", "position", "company", "team", "teams", "work", "working", "opportunity",
        "opportunities", "candidate", "candidates", "applicant", "apply", "employer", "employee", "hire",
        "hiring", "year", "years", "month", "months", "summer", "fall", "winter", "spring", "intern",
        "interns", "internship", "student", "students", "program", "school", "university", "college",
        "degree", "bachelor", "bachelors", "master", "masters", "diploma", "graduate", "salary",
        "compensation", "benefits", "pay", "hour", "hourly", "competitive", "flexible", "hybrid", "remote",
        "onsite", "location", "duration", "deadline", "committed", "diverse", "inclusive", "workplace",
        "encourage", "individuals", "backgrounds", "experiences", "equal", "ability", "able", "learn",
        "quickly", "independently", "help", "join", "contribute", "participate", "collaborate", "build",
        "develop", "design", "maintain", "ensure", "improve", "solve", "support", "looking", "seeking",
        "field", "offer", "offers", "plus", "nice", "have", "has", "had", "also", "well", "within", "across",
        "during", "after", "before", "mentorship", "networking", "events", "community", "activities",
        "access", "resources", "training", "internal", "workshops", "learning", "currently", "returning",
        "enrolled", "hands", "millions", "potential", "through", "open", "code", "monitor", "do", "write",
        "document", "assist", "perform", "implement", "present", "analyze", "debug", "automate",
        "containerize", "security", "cloud", "infrastructure", "engineering", "application", "applications",
        "following", "troubleshoot", "optimize", "review", "reviews", "discussions", "practices", "best",
        "complex", "challenges", "production", "features", "projects", "project", "teamwork", "communication",
        "problem", "solving", "analytical", "environment", "environments", "day", "days", "full", "time"
    };

    private readonly ApplicationDbContext _db;
    private readonly ResumeTextService _resumeText;

    public KeywordCoverageService(ApplicationDbContext db, ResumeTextService resumeText)
    {
        _db = db;
        _resumeText = resumeText;
    }

    // ── Entry points ─────────────────────────────────────

    /// <summary>
    /// Coverage for one of the user's applications. Returns null when the application is not theirs or does
    /// not exist, so the caller can 404 without distinguishing the two.
    /// </summary>
    public async Task<KeywordCoverage?> GetAsync(int applicationId, string userId, CancellationToken ct = default)
    {
        var app = await _db.JobApplications.AsNoTracking()
            .Where(a => a.Id == applicationId && a.UserId == userId)
            .Select(a => new { a.JobDescription, a.CompanyName, a.RoleTitle, a.Location })
            .FirstOrDefaultAsync(ct);
        if (app is null) return null;

        return await BuildForAsync(app.JobDescription, app.CompanyName, app.RoleTitle, app.Location, userId, ct);
    }

    /// <summary>
    /// Coverage for a posting the user is still typing on the Create form, which has no id yet. The company,
    /// role and location come from the form's own fields so the count matches what the saved application
    /// will report — without them the employer's own name counts as a missing term.
    /// </summary>
    public Task<KeywordCoverage> GetForDescriptionAsync(string? description, string? company, string? role,
                                                        string? location, string userId, CancellationToken ct = default)
        => BuildForAsync(description, company, role, location, userId, ct);

    private async Task<KeywordCoverage> BuildForAsync(string? description, string? company, string? role,
                                                      string? location, string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(description))
            return KeywordCoverage.Unavailable(NoDescription);

        var resume = await _resumeText.GetActiveAsync(userId, ct);
        if (resume.Status is ResumeTextStatus.NoResume or ResumeTextStatus.FileMissing)
            return KeywordCoverage.Unavailable(NoResume);
        if (!resume.Ok)
            return KeywordCoverage.Unavailable(UnreadableResume);

        return Build(description, resume.Text!, company, role, location);
    }

    // ── The whole algorithm, pure ────────────────────────

    /// <summary>
    /// Extracts the posting's terms and reports which the resume does not literally contain. Pure: the entire
    /// behaviour of this feature is testable through here with no DI, no host and no network.
    /// </summary>
    public static KeywordCoverage Build(string? jobDescription, string? resumeText,
                                        string? company = null, string? role = null, string? location = null)
    {
        if (string.IsNullOrWhiteSpace(jobDescription)) return KeywordCoverage.Unavailable(NoDescription);
        if (string.IsNullOrWhiteSpace(resumeText))     return KeywordCoverage.Unavailable(NoResume);
        if (resumeText.Length < ResumeTextService.MinUsefulChars)
            return KeywordCoverage.Unavailable(UnreadableResume);

        var terms = Extract(jobDescription, company, role, location);
        if (terms.Count < MinTerms) return KeywordCoverage.Unavailable(TooShort);

        var missing = terms.Where(t => !Present(t.Term, resumeText)).ToList();

        // Ordering: how often the posting says it, plus a boost for the requirements section. Ties break
        // alphabetically so the same posting always produces the same list.
        var ranked = missing
            .OrderByDescending(t => t.Count + (t.InRequirements ? 2 : 0))
            .ThenBy(t => t.Term, StringComparer.OrdinalIgnoreCase)
            .Take(MaxMissing)
            .Select(t => t with { NearMiss = NearMiss(t.Term, resumeText) })
            .ToList();

        // The denominator counts everything extracted, not just what fits in the list.
        return new KeywordCoverage(true, null, terms.Count - missing.Count, terms.Count, ranked);
    }

    /// <summary>
    /// The posting's candidate terms. Deterministic and structure-tolerant: postings written without bullet
    /// glyphs work (a line containing a cue word is a requirement, not a heading), and a posting flattened to
    /// a single line still yields its technologies — it just loses the requirements boost, so ordering falls
    /// back to raw frequency.
    /// </summary>
    public static IReadOnlyList<KeywordTerm> Extract(string jobDescription, string? company = null,
                                                     string? role = null, string? location = null)
    {
        var text = jobDescription.Length > JobDescriptionBudget
            ? jobDescription[..JobDescriptionBudget]
            : jobDescription;

        // The company, the role and the city are not things a resume is missing.
        var own = new HashSet<string>(
            Regex.Matches($"{company} {role} {location}", @"[A-Za-z]{3,}").Select(m => m.Value),
            StringComparer.OrdinalIgnoreCase);

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inRequirements = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var context = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var headings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var underRequirements = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            var heading = HeadingOf(line);
            if (heading is not null)
            {
                headings.Add(heading);
                underRequirements = RequirementsHeading.IsMatch(heading);
                continue;
            }

            // A term counts once per line it appears on. The acronym and capitalised passes legitimately
            // find the same token, and a cue capture finds it a third time, so counting raw hits would
            // triple an acronym's apparent prominence against a word found only once.
            var seenOnLine = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in Candidates(raw))
            {
                var term = Normalize(candidate);
                if (!IsUsable(term, own)) continue;
                if (!seenOnLine.Add(term)) continue;

                counts[term] = counts.TryGetValue(term, out var n) ? n + 1 : 1;
                // Prefer the capitalised spelling when the posting uses both.
                if (!display.TryGetValue(term, out var shown) || (char.IsUpper(term[0]) && !char.IsUpper(shown[0])))
                    display[term] = term;
                if (underRequirements) inRequirements.Add(term);
                if (!context.ContainsKey(term)) context[term] = line;
            }
        }

        // A heading that also reads like a term ("Technical Skills") is a heading.
        foreach (var h in headings) counts.Remove(h);

        var kept = Desubsume(counts);

        return kept
            .Select(term => new KeywordTerm(
                display.TryGetValue(term, out var shown) ? shown : term,
                counts[term],
                inRequirements.Contains(term),
                context.TryGetValue(term, out var c) ? c : "",
                null))
            .ToList();
    }

    /// <summary>
    /// True when <paramref name="term"/> appears in the resume as a whole word, or as any spelling
    /// <see cref="SkillAliases"/> treats as the same thing.
    /// </summary>
    public static bool Present(string term, string? resumeText)
    {
        if (string.IsNullOrWhiteSpace(resumeText) || string.IsNullOrWhiteSpace(term)) return false;

        foreach (var spelling in Spellings(term))
            if (Regex.IsMatch(resumeText, BoundaryPattern(spelling), RegexOptions.IgnoreCase))
                return true;

        return false;
    }

    /// <summary>
    /// The longer resume word that contains <paramref name="term"/>, when containment is the only reason the
    /// term is reported missing — "PostgreSQL" for a posting asking for "SQL". Null when there is no such word.
    ///
    /// This exists to make the word-boundary rule legible. Without it a user whose resume says PostgreSQL sees
    /// "SQL" listed as missing and reads it as a bug, rather than as the point: a scanner matching the literal
    /// string "SQL" will not find it inside "PostgreSQL".
    /// </summary>
    public static string? NearMiss(string term, string? resumeText)
    {
        if (string.IsNullOrWhiteSpace(resumeText) || string.IsNullOrWhiteSpace(term)) return null;
        if (term.Contains(' ')) return null;                       // phrases are not hidden inside single words
        if (Present(term, resumeText)) return null;

        // Only single-token terms can sit inside a word, and a two-letter one would match far too much.
        if (term.Length < 3) return null;

        var match = Regex.Match(resumeText, $@"[A-Za-z0-9]*{Regex.Escape(term)}[A-Za-z0-9]*", RegexOptions.IgnoreCase);
        while (match.Success)
        {
            var word = match.Value;
            if (!word.Equals(term, StringComparison.OrdinalIgnoreCase) && word.Length > term.Length)
                return word;
            match = match.NextMatch();
        }
        return null;
    }

    // ── Internals ────────────────────────────────────────

    /// <summary>
    /// The heading text of a section header line, or null when the line is content.
    ///
    /// A line is a heading when it ends in a colon, or is at most five capitalised/connective words. It is
    /// never a heading when it contains a cue word: "Experience with Docker or Kubernetes" is a requirement
    /// line in a posting written without bullet glyphs, and treating it as a heading silently swallows every
    /// term on it. That was a real failure on a real posting, so the rule is load-bearing.
    /// </summary>
    private static string? HeadingOf(string line)
    {
        var m = HeadingShape.Match(line);
        if (!m.Success) return null;

        var heading = m.Groups[1].Value.Trim();
        var words = heading.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // A line that *opens* with a cue word is a requirement, however capitalised it looks:
        // "Experience with Docker or Kubernetes" is five capitalised/connective words and would otherwise
        // pass the shape test below, taking every term on it with it. A cue word anywhere else is fine —
        // "Technical Skills" is a heading.
        if (words.Length > 0 && CueWords.Contains(words[0]) && !m.Groups[2].Success) return null;

        var endsWithColon = m.Groups[2].Success;
        if (endsWithColon && words.Length <= 6) return heading;
        if (words.Length <= 5 && words.All(w => char.IsUpper(w[0]) || HeadingConnectives.Contains(w))) return heading;
        return null;
    }

    /// <summary>Every raw candidate string on one line, punctuated tokens claimed first.</summary>
    private static IEnumerable<string> Candidates(string raw)
    {
        var masked = raw.ToCharArray();
        foreach (Match m in Punctuated.Matches(raw))
        {
            yield return m.Value;
            for (var i = m.Index; i < m.Index + m.Length; i++) masked[i] = ' ';
        }

        var rest = new string(masked);
        foreach (Match m in Acronym.Matches(rest)) yield return m.Value;
        foreach (Match m in CapitalisedRun.Matches(rest))
        {
            // Skip a single capitalised word that opens a sentence or bullet — almost always a verb.
            if (!m.Value.Contains(' ') && SentenceStart.IsMatch(rest[..m.Index])) continue;
            yield return m.Value;
        }

        foreach (Match m in Cue.Matches(raw))
            foreach (var part in CueSplit.Split(m.Groups[1].Value))
                yield return part;
    }

    private static string Normalize(string candidate)
    {
        var term = Whitespace.Replace(candidate, " ").Trim(' ', '\t', '*', '-', '–', '—', '•', ',', ';', ':', '.', '(', ')', '[', ']', '\'', '’', '"');
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (words.Count > 0 && (LeadingNoise.Contains(words[0]) || CueWords.Contains(words[0])))
            words.RemoveAt(0);

        // "NoSQL databases" → "NoSQL", but "distributed systems" stays whole (see TrailingGeneric).
        if (words.Count > 1 && TrailingGeneric.Contains(words[^1]) && LooksLikeAName(words[^2]))
            words.RemoveAt(words.Count - 1);

        return string.Join(' ', words);
    }

    /// <summary>A word that reads as a technology's name rather than an ordinary adjective or noun.</summary>
    private static bool LooksLikeAName(string word)
        => char.IsUpper(word[0]) || word.Any(c => c is '.' or '#' or '+' or '/');

    private static bool IsUsable(string term, HashSet<string> own)
    {
        if (term.Length < 2 || term.Length > MaxTermChars) return false;

        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Length > 3) return false;
        if (own.Contains(words[0]) || Months.Contains(words[0])) return false;

        // A connective inside the candidate means it is a clause, not a name.
        if (words.Skip(1).Any(w => InteriorConnectives.Contains(w))) return false;

        // Something made only of boilerplate, the employer's own name or a month says nothing about skills.
        return !words.All(w => Boilerplate.Contains(w) || own.Contains(w) || Months.Contains(w));
    }

    /// <summary>
    /// Drops a term that is wholly contained in a longer term that the posting uses at least as often —
    /// "Azure" inside "Azure DevOps" when both appear twice. The count test keeps a term that stands on its
    /// own more often than the longer one does ("Azure" five times, "Azure DevOps" twice keeps both).
    /// </summary>
    private static List<string> Desubsume(Dictionary<string, int> counts)
    {
        var byLength = counts.Keys.OrderByDescending(k => k.Length).ToList();
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < byLength.Count; i++)
        {
            var shorter = byLength[i];
            foreach (var longer in byLength.Take(i))
            {
                if (dropped.Contains(longer)) continue;
                if (Regex.IsMatch(longer, BoundaryPattern(shorter), RegexOptions.IgnoreCase) &&
                    counts[longer] >= counts[shorter])
                {
                    dropped.Add(shorter);
                    break;
                }
            }
        }

        return counts.Keys.Where(k => !dropped.Contains(k)).ToList();
    }

    /// <summary>
    /// A term plus every alias spelling of it, lower-cased. Plurals and possessives are added here rather
    /// than stemmed: a scanner matching "test" does not match "testing", so only the forms that really are
    /// the same word are folded.
    /// </summary>
    private static IEnumerable<string> Spellings(string term)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spelling in SkillAliases.Spellings(term).Append(term.Trim().ToLowerInvariant()))
        {
            if (!seen.Add(spelling)) continue;
            yield return spelling;

            // "APIs" covers "API", "developer's" covers "developer".
            foreach (var form in Inflections(spelling))
                if (seen.Add(form)) yield return form;
        }
    }

    private static IEnumerable<string> Inflections(string term)
    {
        if (term.EndsWith("'s", StringComparison.OrdinalIgnoreCase) ||
            term.EndsWith("’s", StringComparison.OrdinalIgnoreCase))
            yield return term[..^2];

        if (term.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && term.Length > 4)
            yield return term[..^3] + "y";
        else if (term.EndsWith("es", StringComparison.OrdinalIgnoreCase) && term.Length > 3)
            yield return term[..^2];

        if (term.EndsWith('s') && !term.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && term.Length > 2)
            yield return term[..^1];
        else if (!term.EndsWith('s'))
            yield return term + "s";
    }

    /// <summary>
    /// A whole-word match for one spelling. The leading boundary is only applied when the term starts with an
    /// alphanumeric: without that exception ".NET" could never be covered by "ASP.NET Core", which is how
    /// almost every .NET resume writes it. The trailing boundary always applies, so "SQL" stays uncovered by
    /// "PostgreSQL" — see the class summary for why that asymmetry is the right one.
    /// </summary>
    private static string BoundaryPattern(string spelling)
    {
        var lead = spelling.Length > 0 && char.IsLetterOrDigit(spelling[0]) ? @"(?<![A-Za-z0-9])" : "";
        return lead + Regex.Escape(spelling) + @"(?![A-Za-z0-9])";
    }
}
