using System.Text.Json;
using InternTrackAI.Data;
using InternTrackAI.Models;
using InternTrackAI.Models.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace InternTrackAI.Services;

/// <summary>Outcome of one demo reseed, for logging and the admin page.</summary>
public sealed record DemoResetResult(bool UserFound, string? Email, int Applications, int Notes, int CoverLetters, int Suggestions, TimeSpan Elapsed)
{
    public static DemoResetResult NoUser(string? email) => new(false, email, 0, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>
/// Wipes the shared demo account back to a known, realistic state: every application, note,
/// generated cover letter, interview prep session, and any resume versions visitors uploaded
/// are removed (the <em>active</em> resume and the profile itself are kept so the sample resume
/// keeps powering match scores), then a fixed set of 15 applications
/// spanning every status, match tier, and Attention category (overdue, deadline soon, follow-up
/// due, upcoming interview) is recreated along with a few notes and one saved letter. Two resume
/// versions are guaranteed ("Backend focus", the active one, and "General") and the applications
/// are split between them so the dashboard's Resume performance card shows a real comparison. The two
/// <see cref="TargetRoles"/> are merged into the profile, and the seeded missing skills give the skill gap card a clear
/// top skill (Docker, 6 of 11), a mid tier (Go, C++) and a tail of singles spread across both roles.
/// Used by <see cref="DemoResetService"/> nightly and by <c>POST /Admin/ResetDemo</c> on demand.
/// </summary>
public class DemoSeeder
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<IdentityUser> _users;
    private readonly UserDataPurger _purger;
    private readonly UploadStorage _uploads;
    private readonly IConfiguration _config;
    private readonly ILogger<DemoSeeder> _logger;

    public DemoSeeder(ApplicationDbContext db, UserManager<IdentityUser> users, UserDataPurger purger,
                      UploadStorage uploads, IConfiguration config, ILogger<DemoSeeder> logger)
    {
        _db = db;
        _users = users;
        _purger = purger;
        _uploads = uploads;
        _config = config;
        _logger = logger;
    }

    public async Task<DemoResetResult> ResetAsync(CancellationToken ct = default)
    {
        var email = _config["Demo:Email"];
        if (string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning("Demo reset requested but Demo:Email is not configured.");
            return DemoResetResult.NoUser(null);
        }

        var user = await _users.FindByEmailAsync(email);
        if (user == null)
        {
            _logger.LogWarning("Demo reset requested but no account exists for the configured Demo:Email.");
            return DemoResetResult.NoUser(email);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.LogInformation("Demo reset starting for user {UserId}.", user.Id);

        await _purger.PurgeAsync(user.Id, keepProfile: true, keepActiveDocuments: true);

        var (primary, secondary) = await EnsureResumesAsync(user.Id, ct);
        await EnsureTargetRolesAsync(user.Id, ct);

        var apps = BuildApplications(user.Id, DateTime.UtcNow.Date);
        AssignResumes(apps, primary.Id, secondary.Id);
        _db.JobApplications.AddRange(apps);
        await _db.SaveChangesAsync(ct);

        var notes       = BuildNotes(user.Id, apps);
        var letters     = BuildCoverLetters(user.Id, apps);
        var suggestions = BuildSuggestions(user.Id, apps, DateTime.UtcNow);
        _db.ApplicationNotes.AddRange(notes);
        _db.GeneratedCoverLetters.AddRange(letters);
        _db.StatusSuggestions.AddRange(suggestions);
        await _db.SaveChangesAsync(ct);

        sw.Stop();
        _logger.LogInformation("Demo reset finished for user {UserId}: {Apps} applications, {Notes} notes, {Letters} cover letter(s), {Suggestions} inbox suggestions, resumes {Primary}/{Secondary} in {Ms} ms.",
            user.Id, apps.Count, notes.Count, letters.Count, suggestions.Count, primary.Id, secondary.Id, sw.ElapsedMilliseconds);

        return new DemoResetResult(true, email, apps.Count, notes.Count, letters.Count, suggestions.Count, sw.Elapsed);
    }

    // ── Target roles ─────────────────────────────────────────────────────────

    /// <summary>
    /// Target roles the skill gap card's role filter splits on. Seeded role titles are worded so each tag matches a
    /// few applications (whole words) and the rest land in "Other": Shopify/Duolingo/Google and Notion/Figma.
    /// </summary>
    public static readonly string[] TargetRoles = { "Software Engineering Intern", "Frontend Engineering Intern" };

    /// <summary>Adds <see cref="TargetRoles"/> to the kept profile (never removes the admin's own tags); creates the profile row if missing.</summary>
    private async Task EnsureTargetRolesAsync(string userId, CancellationToken ct)
    {
        var profile = await _db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);
        if (profile is null)
        {
            profile = new UserProfile { UserId = userId };
            _db.UserProfiles.Add(profile);
        }

        var (merged, added) = ProfileTags.Merge(ProfileTags.FromJson(profile.TargetRolesJson), TargetRoles);
        if (added.Count > 0)
        {
            profile.TargetRolesJson = ProfileTags.ToJson(merged);
            await _db.SaveChangesAsync(ct);
        }
    }

    // ── Resume versions ──────────────────────────────────────────────────────

    public const string PrimaryResumeLabel   = "Backend focus";   // the active resume, the one that "works"
    public const string SecondaryResumeLabel = "General";         // the older, weaker one (low-confidence row)

    /// <summary>Companies whose applications were "sent with" the secondary resume; everything else uses the primary.</summary>
    public static readonly string[] SecondaryResumeCompanies = { "Duolingo", "Snowflake", "Palantir" };

    /// <summary>
    /// After the purge only the active resume (if any) is left. Keep it as the primary version
    /// (labelled unless the admin already named it) and add a second version by copying its file,
    /// so each row has its own PDF and deleting one never breaks the other. A demo account that has
    /// never uploaded a resume gets a small generated placeholder PDF so downloads still work.
    /// </summary>
    private async Task<(ResumeVersion Primary, ResumeVersion Secondary)> EnsureResumesAsync(string userId, CancellationToken ct)
    {
        var existing = await _db.ResumeVersions.Where(r => r.UserId == userId).OrderBy(r => r.Id).ToListAsync(ct);
        var primary  = existing.FirstOrDefault(r => r.IsActive) ?? existing.FirstOrDefault();

        if (primary is null)
        {
            primary = await WriteResumeAsync(userId, "Demo_Resume.pdf", PlaceholderPdf("InternTrackAI demo resume"), version: 1, PrimaryResumeLabel);
            _db.ResumeVersions.Add(primary);
        }
        primary.IsActive = true;
        primary.Label ??= PrimaryResumeLabel;

        var secondary = existing.FirstOrDefault(r => r.Id != primary.Id);
        if (secondary is null)
        {
            var source = _uploads.Resolve(primary.StoredPath);
            var bytes  = File.Exists(source) ? await File.ReadAllBytesAsync(source, ct) : PlaceholderPdf("InternTrackAI demo resume (general)");
            secondary  = await WriteResumeAsync(userId, "Demo_Resume_general.pdf", bytes, version: primary.VersionNumber + 1, SecondaryResumeLabel);
            _db.ResumeVersions.Add(secondary);
        }
        secondary.IsActive = false;
        secondary.Label  ??= SecondaryResumeLabel;

        await _db.SaveChangesAsync(ct);
        return (primary, secondary);
    }

    private async Task<ResumeVersion> WriteResumeAsync(string userId, string originalName, byte[] bytes, int version, string label)
    {
        var dir    = _uploads.GetUserDirectory("resumes", userId);
        var stored = $"{Guid.NewGuid():N}.pdf";
        await File.WriteAllBytesAsync(Path.Combine(dir, stored), bytes);
        return new ResumeVersion
        {
            UserId           = userId,
            VersionNumber    = version,
            OriginalFileName = originalName,
            StoredPath       = UploadStorage.MakeStoredPath("resumes", userId, stored),
            FileSize         = bytes.Length,
            Label            = label,
            UploadedAt       = DateTime.UtcNow.AddDays(-60)
        };
    }

    /// <summary>Links every seeded application to a resume: the three <see cref="SecondaryResumeCompanies"/> to the secondary, the rest (Saved included) to the primary.</summary>
    public static void AssignResumes(IEnumerable<JobApplication> apps, int primaryId, int secondaryId)
    {
        foreach (var app in apps)
            app.ResumeVersionId = SecondaryResumeCompanies.Contains(app.CompanyName) ? secondaryId : primaryId;
    }

    /// <summary>A one-page, single-line PDF with a correct xref table (no packages needed).</summary>
    public static byte[] PlaceholderPdf(string text)
    {
        var safe    = text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
        var content = $"BT /F1 18 Tf 72 720 Td ({safe}) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── Seed data ────────────────────────────────────────────────────────────

    private static string Json(params string[] items) => JsonSerializer.Serialize(items);

    private static JobApplication App(string userId, DateTime today,
        string company, string role, string location, WorkMode mode, ApplicationStatus status,
        int? deadlineInDays, int? appliedDaysAgo, string? salary, string link, string description,
        int? score = null, string? summary = null, string[]? matching = null, string[]? missing = null,
        DateTime? interviewAt = null)
    {
        return new JobApplication
        {
            InterviewAt         = interviewAt,
            UserId              = userId,
            CompanyName         = company,
            RoleTitle           = role,
            Location            = location,
            WorkMode            = mode,
            Status              = status,
            Deadline            = deadlineInDays.HasValue ? today.AddDays(deadlineInDays.Value) : null,
            DateApplied         = appliedDaysAgo.HasValue ? today.AddDays(-appliedDaysAgo.Value) : null,
            Salary              = salary,
            JobLink             = link,
            JobDescription      = description,
            MatchScore          = score,
            MatchRecommendation = score.HasValue ? ResumeMatcherService.RecommendationFor(score.Value) : null,
            MatchSummary        = summary,
            MatchingSkillsJson  = matching != null ? Json(matching) : null,
            MissingSkillsJson   = missing  != null ? Json(missing)  : null
        };
    }

    /// <summary>15 applications: 4 Saved, 4 Applied, 3 Interview, 2 Offer, 2 Rejected; match scores in every tier plus a few un-analysed.</summary>
    public static List<JobApplication> BuildApplications(string userId, DateTime today) => new()
    {
        // ── Interview ──
        App(userId, today, "Stripe", "Backend Engineering Intern", "San Francisco, CA", WorkMode.Hybrid, ApplicationStatus.Interview,
            deadlineInDays: 12, appliedDaysAgo: 18, salary: "$52/hr", link: "https://stripe.com/jobs/listing/backend-engineering-intern",
            description: "Stripe's Payments Infrastructure team is hiring a summer intern to build and ship services that move billions of dollars a day. You will write production code in Java and Go, design APIs used by other engineering teams, and work closely with a mentor on a scoped project from design review to launch.\n\nRequirements: strong fundamentals in data structures and algorithms; experience with at least one statically typed language; familiarity with SQL and REST APIs; interest in distributed systems and reliability. Nice to have: Docker, Kubernetes, gRPC, observability tooling.",
            score: 84, summary: "Strong fit. Your Java and PostgreSQL experience maps directly onto the Payments Infrastructure stack, and your REST API project shows the design skills the posting asks for. Kubernetes and gRPC are the main gaps, but both are listed as nice-to-have.",
            matching: new[] { "Java", "SQL", "REST APIs", "Data Structures", "Git", "Distributed Systems" }, missing: new[] { "Go", "Kubernetes", "gRPC", "Docker" },
            interviewAt: today.AddDays(3).AddHours(14)),                       // upcoming interview on the dashboard/board

        App(userId, today, "Shopify", "Software Engineering Intern (Ruby/Rails)", "Toronto, ON", WorkMode.Remote, ApplicationStatus.Interview,
            deadlineInDays: 20, appliedDaysAgo: 25, salary: "CA$45/hr", link: "https://www.shopify.com/careers/engineering-intern",
            description: "Join a product team at Shopify building the tools millions of merchants use every day. Interns own a feature end to end in Ruby on Rails and React, participate in code review, and ship to production in their first month.\n\nWe look for: experience with a web framework (Rails, Django, Express, or similar); comfort with JavaScript and a component library; understanding of relational databases; experience shipping services in Docker containers; strong written communication for an async, remote-first team.",
            score: 71, summary: "Good fit. You have solid web fundamentals and React experience, and your Django project demonstrates MVC framework skills that transfer to Rails. Learning Ruby syntax before the interview would strengthen the technical round.",
            matching: new[] { "React", "JavaScript", "SQL", "MVC Frameworks", "Git" }, missing: new[] { "Ruby", "Ruby on Rails", "GraphQL", "Docker" }),

        App(userId, today, "Datadog", "Site Reliability Engineering Intern", "New York, NY", WorkMode.OnSite, ApplicationStatus.Interview,
            deadlineInDays: 9, appliedDaysAgo: 14, salary: "$48/hr", link: "https://careers.datadoghq.com/detail/sre-intern",
            description: "Datadog's SRE interns keep one of the largest observability platforms in the world running smoothly. You will automate operational work in Python and Go, improve deployment pipelines, and participate in a shadow on-call rotation with a senior engineer.\n\nRequirements: Linux fundamentals, scripting in Python or Bash, understanding of networking basics (TCP/IP, DNS, HTTP). Preferred: Docker, Terraform, Kubernetes, experience with monitoring tools.",
            score: 58, summary: "Moderate fit. Your Python scripting and Linux coursework cover the core requirements, but the role leans on infrastructure tooling (Terraform, Kubernetes) that does not appear on your resume. Highlight any deployment or CI work you have done.",
            matching: new[] { "Python", "Linux", "Bash", "Networking", "Git" }, missing: new[] { "Terraform", "Kubernetes", "Go", "Monitoring Tools", "Docker" },
            interviewAt: today.AddDays(6).AddHours(10).AddMinutes(30)),

        // ── Offer ──
        App(userId, today, "Notion", "Frontend Engineering Intern", "Remote", WorkMode.Remote, ApplicationStatus.Offer,
            deadlineInDays: -3, appliedDaysAgo: 41, salary: "$50/hr", link: "https://www.notion.so/careers/frontend-intern",
            description: "Notion is looking for a frontend intern to work on the core editor experience. You will build performant, accessible UI in TypeScript and React, collaborate with designers on interaction details, and measure the impact of your work with product analytics.\n\nYou should have: strong TypeScript/JavaScript skills, React experience, an eye for detail, and familiarity with browser performance profiling. Bonus: experience with rich text editors or collaborative software.",
            score: 91, summary: "Excellent fit. Your TypeScript and React work, including the collaborative whiteboard project, lines up almost exactly with the editor team's needs. Accessibility experience is a differentiator most candidates lack.",
            matching: new[] { "TypeScript", "React", "JavaScript", "Accessibility", "CSS", "Testing" }, missing: new[] { "Performance Profiling", "Rich Text Editors" }),

        App(userId, today, "Figma", "Frontend Engineering Intern", "New York, NY", WorkMode.Hybrid, ApplicationStatus.Offer,
            deadlineInDays: -10, appliedDaysAgo: 48, salary: "$49/hr", link: "https://www.figma.com/careers/product-engineering-intern",
            description: "Figma's frontend engineering interns ship user-facing features across the design tool and FigJam. Expect to work in TypeScript, React, and C++ (for the rendering engine), pair frequently with engineers, and demo your work to the whole company.\n\nRequirements: proficiency in TypeScript or JavaScript, comfort learning a large codebase, strong product sense. Nice to have: WebGL, C++, browser performance profiling, or experience building creative tools.",
            score: 77, summary: "Good fit. Your frontend skills cover most of the role, and your interest in creative tooling comes through in your portfolio. C++ and WebGL are gaps, but the posting treats them as bonuses.",
            matching: new[] { "TypeScript", "React", "JavaScript", "Product Thinking", "Git" }, missing: new[] { "C++", "WebGL", "Performance Profiling" }),

        // ── Applied ──
        App(userId, today, "Airbnb", "iOS Engineering Intern", "San Francisco, CA", WorkMode.Hybrid, ApplicationStatus.Applied,
            deadlineInDays: 30, appliedDaysAgo: 11, salary: "$51/hr", link: "https://careers.airbnb.com/positions/ios-intern",
            description: "Build the Airbnb guest experience on iOS. Interns join a product team, ship features in Swift and SwiftUI, write unit and snapshot tests, and learn our design-system-driven approach to mobile development.\n\nRequirements: Swift experience (coursework or personal apps), understanding of iOS app lifecycle and UIKit or SwiftUI, familiarity with Git. Preferred: published App Store apps, experience with Combine or async/await.",
            score: 63, summary: "Good fit. Your SwiftUI side project covers the essentials, and your testing habits will stand out. Deeper UIKit exposure and a published app would make the application stronger.",
            matching: new[] { "Swift", "SwiftUI", "Git", "Unit Testing" }, missing: new[] { "UIKit", "Combine", "App Store Publishing" }),

        App(userId, today, "Cloudflare", "Systems Engineering Intern", "Austin, TX", WorkMode.OnSite, ApplicationStatus.Applied,
            deadlineInDays: 16, appliedDaysAgo: 9, salary: "$47/hr", link: "https://www.cloudflare.com/careers/systems-intern",
            description: "Cloudflare's systems interns work on the edge network that serves a large fraction of the internet. Projects involve Rust and Go services, Linux networking, and performance work at scale.\n\nRequirements: systems programming experience (C, C++, Rust, or Go), understanding of networking and operating systems, Linux proficiency. Preferred: Docker and container networking, eBPF, DPDK, or kernel contributions.",
            score: 46, summary: "Moderate fit. Your operating systems coursework and C experience give you a foundation, but the team wants Rust or Go and deeper networking work. Consider a small Rust project before the interview stage.",
            matching: new[] { "C", "Linux", "Operating Systems", "Networking" }, missing: new[] { "Rust", "Go", "eBPF", "Performance Engineering", "Docker" }),

        App(userId, today, "Duolingo", "Software Engineering Intern, Learning Platform", "Pittsburgh, PA", WorkMode.OnSite, ApplicationStatus.Applied,
            deadlineInDays: 24, appliedDaysAgo: 6, salary: "$45/hr", link: "https://careers.duolingo.com/jobs/swe-intern",
            description: "Help build the backend that powers lessons for hundreds of millions of learners. You will work in Python and Java on high-throughput services, design experiments with data scientists, and ship to production every week.\n\nRequirements: Python or Java, SQL, understanding of REST services. Preferred: Docker, experience with A/B testing, Kafka, or AWS.",
            score: 79, summary: "Good fit. Python, Java, and SQL are all present on your resume, and your capstone shows experiment design. AWS exposure would round out the profile.",
            matching: new[] { "Python", "Java", "SQL", "REST APIs", "Data Analysis" }, missing: new[] { "AWS", "Kafka", "Docker" }),

        App(userId, today, "Snowflake", "Database Engineering Intern", "Bellevue, WA", WorkMode.Hybrid, ApplicationStatus.Applied,
            deadlineInDays: 40, appliedDaysAgo: 3, salary: "$55/hr", link: "https://careers.snowflake.com/us/en/job/db-intern",
            description: "Work on the query engine at the heart of the Snowflake Data Cloud. Interns take on well-scoped optimizer or execution engine projects in C++, backed by rigorous testing and performance benchmarking.\n\nRequirements: strong C++ skills, database internals coursework (query processing, storage, transactions), algorithms. Preferred: contributions to open-source database projects, experience with LLVM or SIMD.",
            score: 34, summary: "Weak fit. The role is heavily C++ and database-internals focused, while your background is mostly application-level Java and web development. A databases course project would help, but this is a stretch application.",
            matching: new[] { "Algorithms", "SQL" }, missing: new[] { "C++", "Query Optimization", "Storage Engines", "Performance Benchmarking" }),

        // ── Saved ──
        App(userId, today, "Anthropic", "Software Engineering Intern, Developer Platform", "San Francisco, CA", WorkMode.Hybrid, ApplicationStatus.Saved,
            deadlineInDays: 2, appliedDaysAgo: null, salary: "$60/hr", link: "https://www.anthropic.com/careers/swe-intern-platform",
            description: "Build the APIs, SDKs, and documentation that developers use to integrate Claude. You will work across a TypeScript and Python codebase, own a feature from design to launch, and talk to developers to understand how they build.\n\nRequirements: strong programming skills in Python or TypeScript, experience building or consuming web APIs, clear technical writing. Preferred: open-source SDK contributions, experience with LLM-based applications.",
            score: 88, summary: "Strong fit. Your Python and TypeScript experience, the REST API project, and the well-written README on your portfolio all speak directly to the developer platform role. Mention your LLM side project prominently.",
            matching: new[] { "Python", "TypeScript", "REST APIs", "Technical Writing", "Git", "Testing" }, missing: new[] { "SDK Design" }),

        App(userId, today, "Vercel", "Developer Experience Intern", "Remote", WorkMode.Remote, ApplicationStatus.Saved,
            deadlineInDays: 21, appliedDaysAgo: null, salary: "$46/hr", link: "https://vercel.com/careers/dx-intern",
            description: "Vercel's DX team makes Next.js and the Vercel platform delightful to use. As an intern you will build example apps and templates, improve error messages and docs, and contribute to open-source packages used by millions of developers.\n\nRequirements: React and Next.js experience, strong written communication, empathy for developers. Preferred: open-source contributions, Node.js tooling experience.",
            score: null),

        // Saved with a deadline that already passed → "Overdue" in the Attention card.
        App(userId, today, "Linear", "Full Stack Engineering Intern", "Remote", WorkMode.Remote, ApplicationStatus.Saved,
            deadlineInDays: -2, appliedDaysAgo: null, salary: null, link: "https://linear.app/careers/full-stack-intern",
            description: "Linear is a small team building the issue tracker of choice for fast-moving software companies. Interns work across the stack in TypeScript, React, Node.js, and PostgreSQL, with a focus on speed and craft.\n\nRequirements: TypeScript, React, familiarity with relational databases, an obsession with product quality. Preferred: experience with real-time sync or local-first architectures.",
            score: 66, summary: "Good fit. TypeScript, React, and PostgreSQL all match. Local-first sync is specialised knowledge that few interns have, so treat it as a learning opportunity rather than a gap.",
            matching: new[] { "TypeScript", "React", "Node.js", "PostgreSQL", "Git" }, missing: new[] { "Real-time Sync", "Local-first Architecture" }),

        App(userId, today, "SpaceX", "Flight Software Intern", "Hawthorne, CA", WorkMode.OnSite, ApplicationStatus.Saved,
            deadlineInDays: 45, appliedDaysAgo: null, salary: "$42/hr", link: "https://www.spacex.com/careers/flight-software-intern",
            description: "Write the software that flies Falcon and Starship. Flight software interns work in C++ on Linux, building and testing real-time control and telemetry systems with hardware-in-the-loop simulation.\n\nRequirements: strong C++ skills, understanding of real-time systems and embedded programming, Linux. Preferred: control theory, experience with hardware, ITAR-eligible.",
            score: 17, summary: "Poor fit. The role is embedded C++ and real-time control, with essentially no overlap with your web and data background. Unless you are pivoting toward embedded systems, your time is better spent on closer matches.",
            matching: new[] { "Linux" }, missing: new[] { "C++", "Embedded Systems", "Real-time Systems", "Control Theory", "Hardware Testing" }),

        // ── Rejected ──
        App(userId, today, "Google", "Software Engineering Intern, BS/MS", "Mountain View, CA", WorkMode.OnSite, ApplicationStatus.Rejected,
            deadlineInDays: -20, appliedDaysAgo: 62, salary: "$53/hr", link: "https://careers.google.com/jobs/swe-intern",
            description: "Google's software engineering interns work on projects across Search, Cloud, Android, and more. You will write code in C++, Java, Python, or Go, participate in design and code review, and present your project at the end of the summer.\n\nMinimum qualifications: currently pursuing a BS or MS in Computer Science or related field; experience in one or more general-purpose programming languages; experience with data structures and algorithms.",
            score: 74, summary: "Good fit on paper. Your fundamentals and language breadth meet the bar; the process is highly competitive and interview performance on algorithms is the deciding factor.",
            matching: new[] { "Java", "Python", "Data Structures", "Algorithms", "Git" }, missing: new[] { "C++", "Go", "Large-scale Systems" }),

        App(userId, today, "Palantir", "Forward Deployed Software Engineer Intern", "Denver, CO", WorkMode.OnSite, ApplicationStatus.Rejected,
            deadlineInDays: -35, appliedDaysAgo: 75, salary: "$50/hr", link: "https://www.palantir.com/careers/fdse-intern",
            description: "Forward Deployed Software Engineers embed with customers to solve their hardest data problems using Palantir Foundry. Interns build data pipelines and applications in Python, TypeScript, and SQL, and present directly to customer stakeholders.\n\nRequirements: strong programming and communication skills, comfort with ambiguity, willingness to travel. Preferred: experience with data engineering, Spark, Docker, or building customer-facing applications.",
            score: 27, summary: "Weak fit. Your technical skills partially overlap, but the role is primarily about data engineering at scale and customer-facing delivery, neither of which appears in your experience.",
            matching: new[] { "Python", "SQL", "Communication" }, missing: new[] { "Spark", "Data Engineering", "Customer Delivery", "TypeScript", "Docker" }),
    };

    private static List<ApplicationNote> BuildNotes(string userId, List<JobApplication> apps)
    {
        var now = DateTime.UtcNow;
        var stripe  = apps.First(a => a.CompanyName == "Stripe");
        var notion  = apps.First(a => a.CompanyName == "Notion");
        var airbnb  = apps.First(a => a.CompanyName == "Airbnb");
        var google  = apps.First(a => a.CompanyName == "Google");

        return new List<ApplicationNote>
        {
            new() { UserId = userId, JobApplicationId = stripe.Id, CreatedAt = now.AddDays(-6), Text = "Recruiter screen with Priya (30 min). Asked about the payments API project and why Stripe. Technical phone screen scheduled for next Tuesday." },
            new() { UserId = userId, JobApplicationId = stripe.Id, CreatedAt = now.AddDays(-2), Text = "Phone screen done: two coding questions (interval merging, LRU cache). Solved both, discussed trade-offs. Waiting on virtual onsite invite." },
            new() { UserId = userId, JobApplicationId = notion.Id, CreatedAt = now.AddDays(-4), Text = "Offer received: $50/hr, 12 weeks, remote with a two-week SF onsite. Deadline to respond is Friday. Asked about start-date flexibility." },
            new() { UserId = userId, JobApplicationId = airbnb.Id, CreatedAt = now.AddDays(-11), Text = "Applied via referral from Marcus (met at the iOS meetup). He said the team reviews referrals within two weeks." },
            new() { UserId = userId, JobApplicationId = google.Id, CreatedAt = now.AddDays(-20), Text = "Rejected after the second technical round. Feedback: solid coding, but slow on the graph problem. Practicing BFS/DFS variants before next cycle." },
        };
    }

    /// <summary>
    /// Three pending inbox suggestions so the dashboard card, drawer section and board dots are visible
    /// on the demo without a real Gmail connection: an interview invitation with a time (Airbnb, Applied),
    /// an offer (Shopify, Interview) and a rejection (Cloudflare, Applied). Message ids are fixed so a
    /// reseed never trips the (UserId, GmailMessageId) unique index after the purge.
    /// </summary>
    public static List<StatusSuggestion> BuildSuggestions(string userId, List<JobApplication> apps, DateTime nowUtc)
    {
        var airbnb     = apps.First(a => a.CompanyName == "Airbnb");
        var shopify    = apps.First(a => a.CompanyName == "Shopify");
        var cloudflare = apps.First(a => a.CompanyName == "Cloudflare");
        var interviewAt = nowUtc.Date.AddDays(5).AddHours(15);   // 11:00 AM Toronto in September

        return new List<StatusSuggestion>
        {
            new()
            {
                UserId = userId, ApplicationId = airbnb.Id, GmailMessageId = "demo-airbnb-interview",
                SuggestedStatus = ApplicationStatus.Interview, Confidence = 0.92, InterviewAt = interviewAt,
                Summary = "Airbnb's recruiting team invites you to a 45-minute technical phone screen for the iOS Engineering Intern role.",
                EmailSubject = "Airbnb iOS Engineering Intern — phone screen invitation", EmailFrom = "Airbnb Recruiting <recruiting@airbnb.com>",
                EmailDate = nowUtc.AddHours(-5), Status = SuggestionState.Pending, CreatedAt = nowUtc.AddHours(-4)
            },
            new()
            {
                UserId = userId, ApplicationId = shopify.Id, GmailMessageId = "demo-shopify-offer",
                SuggestedStatus = ApplicationStatus.Offer, Confidence = 0.88,
                Summary = "Shopify extends an offer for the Software Engineering Intern position and asks for a response within a week.",
                EmailSubject = "Your offer from Shopify", EmailFrom = "Shopify Talent <talent@shopify.com>",
                EmailDate = nowUtc.AddHours(-26), Status = SuggestionState.Pending, CreatedAt = nowUtc.AddHours(-4)
            },
            new()
            {
                UserId = userId, ApplicationId = cloudflare.Id, GmailMessageId = "demo-cloudflare-rejected",
                SuggestedStatus = ApplicationStatus.Rejected, Confidence = 0.95,
                Summary = "Cloudflare will not be moving forward with the Systems Engineering Intern application this cycle.",
                EmailSubject = "Update on your Cloudflare application", EmailFrom = "Cloudflare Careers <no-reply@cloudflare.com>",
                EmailDate = nowUtc.AddHours(-50), Status = SuggestionState.Pending, CreatedAt = nowUtc.AddHours(-4)
            }
        };
    }

    private static List<GeneratedCoverLetter> BuildCoverLetters(string userId, List<JobApplication> apps)
    {
        var stripe = apps.First(a => a.CompanyName == "Stripe");
        return new List<GeneratedCoverLetter>
        {
            new()
            {
                UserId           = userId,
                JobApplicationId = stripe.Id,
                CompanyName      = stripe.CompanyName,
                RoleTitle        = stripe.RoleTitle,
                IsActive         = true,
                VersionNumber    = 1,
                GeneratedAt      = DateTime.UtcNow.AddDays(-19),
                Content =
                    "Dear Stripe Hiring Team,\n\n" +
                    "I am applying for the Backend Engineering Intern position on the Payments Infrastructure team. As a third-year computer science student who has spent the last two years building and operating backend services, I am drawn to the scale and precision that moving money for millions of businesses demands.\n\n" +
                    "Last spring I designed and shipped a REST API in Java and PostgreSQL that handled scheduling for a 400-member student organization. Beyond the endpoints themselves, I owned the parts that matter in production: idempotent request handling, a migration strategy that let us evolve the schema without downtime, and structured logging that turned a vague \"it's slow\" report into a fixed N+1 query within an hour. That experience taught me to treat reliability as a feature, which is exactly how the Stripe engineering blog describes your approach to payments.\n\n" +
                    "I am also comfortable working in large, unfamiliar codebases. In my distributed systems course I contributed a Raft-based log replication implementation and spent as much time reading the existing test harness as writing new code, which is how I expect an internship on infrastructure to feel. I have not yet used Go or Kubernetes in production, and I would welcome the chance to learn both from engineers who use them at Stripe's scale.\n\n" +
                    "Thank you for considering my application. I would love to talk about how I can contribute to the Payments Infrastructure team this summer.\n\n" +
                    "Sincerely,\nThe InternTrackAI Demo"
            }
        };
    }
}
