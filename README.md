# InternTrackAI

A full-stack internship application tracker built with ASP.NET Core 9 MVC. Track every application through the entire pipeline — from saved to offer — with AI-powered job analysis, resume matching, cover letter generation, interview prep, and a full career portfolio system.

## Screenshots

| Home | Dashboard |
|------|-----------|
| ![Home](docs/screenshots/home.png) | ![Dashboard](docs/screenshots/dashboard.png) |

| Applications | Add Application |
|---|---|
| ![Applications](docs/screenshots/applications.png) | ![Add Application](docs/screenshots/create.png) |

| Cover Letter | Interview Prep |
|---|---|
| ![Cover Letter](docs/screenshots/cover_letter.png) | ![Interview Prep](docs/screenshots/interview_prep.png) |

| Profile | Sign In |
|---|---|
| ![Profile](docs/screenshots/profile.png) | ![Sign In](docs/screenshots/login.png) |

| Register | |
|---|---|
| ![Register](docs/screenshots/register.png) | |

## Features

**Phase 1 — Core Tracker**
- Add, edit, and delete internship applications
- Track status through five stages: Saved, Applied, Interview, Offer, Rejected
- Color-coded status badges and per-status stat cards on the dashboard
- Company avatar initials, deadline tracking, work mode, salary, and notes
- Responsive Bootstrap 5 layout with dark navbar and Inter typography
- Split-screen login and register pages with ASP.NET Core Identity

**Phase 2 — AI Job Analyzer**
- Paste a job description **or a job posting URL** and click Analyze
- URLs are auto-detected — the page is fetched and parsed server-side before analysis
- GPT-4o-mini extracts company name, role title, location, salary, and up to 8 required skills
- Extracted data auto-fills the form with an animated highlight effect
- After analysis, automatically runs a resume match against the user's active resume (if uploaded) and shows an inline match score, recommendation, and skill breakdown

**Phase 3 — Career Portfolio (`/Profile`)**
- Personal info: full name, age, country, phone, profile photo
- Resume management: upload PDFs, full version history, set any version as Active, download or delete
- Cover letter management: same version history system as resumes
- Skills profile: tag-based input, saved to profile, used as AI context
- Target roles: save job types you are pursuing (e.g. Software Engineering Intern)
- Application stats: total count, per-status breakdown, success rate percentage
- AI Resume Score: sends active resume to GPT-4o-mini and returns a score, strengths, and specific improvement suggestions

**Phase 4 — AI Cover Letter Generator (`/CoverLetter/Generate`)**
- Select any tracked job application and click Generate — AI writes a personalized 3–4 paragraph cover letter
- Pulls context from the job's description, your active resume, your profile skills, target roles, and name
- Optional notes field to guide tone, length, or specific talking points
- Editable output textarea with live word count, copy to clipboard, and download as PDF (client-side via jsPDF)
- Save to profile with automatic version numbering and per-letter company/role snapshot
- Saved letter history with Load, Set Active, Download (.txt), and Delete actions
- "Cover Letter" button on every row of the Applications list for one-click access

**Phase 5 — Search, Filter, and Bulk Actions**
- Search applications by company name or role title
- Filter by status and work mode; sort by deadline, date applied, status, or company name
- Select multiple applications with checkboxes and a select-all header
- Bulk delete or bulk status change via a fixed action bar that appears at the bottom of the screen
- Export all applications to CSV with one click (company, role, location, mode, status, dates, salary, link)

**Phase 6 — AI Interview Prep (`/InterviewPrep/Prep/{appId}`)**
- Generate 8–10 interview questions tailored to the specific job description and your resume
- Questions are categorized into Technical, Behavioral, and Company-Specific
- Each question includes a tip on how to approach answering it
- Results are saved per application and can be regenerated at any time
- "Interview Prep" button on every row of the Applications list for one-click access

**Quality of Life**
- Duplicate detection on the Add Application form — warns before saving if the same company and role already exists, with an option to save anyway
- Toast notifications on all mutating actions (save, update, delete, bulk operations, cover letter save)
- Consistent empty states throughout: Applications list, Cover Letter history, Interview Prep

## Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core 9 MVC |
| Database | SQLite via Entity Framework Core 9 |
| Auth | ASP.NET Core Identity |
| AI | OpenAI API — GPT-4o-mini |
| PDF | PdfPig (server-side text extraction), jsPDF (client-side generation) |
| Frontend | Bootstrap 5, Vanilla JS (fetch) |
| Fonts | Inter (Google Fonts) |

## Getting Started

**Prerequisites:** .NET 9 SDK, an OpenAI API key

```bash
git clone https://github.com/MajdArow123/InternTrackAI.git
cd InternTrackAI
```

Add your OpenAI key to `appsettings.json` (this file is gitignored and must be created locally):

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=app.db"
  },
  "OpenAI": {
    "ApiKey": "sk-..."
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

Run the app:

```bash
dotnet run
```

Open [http://localhost:5240](http://localhost:5240). Register an account to get started.

> The AI features require billing credits on your OpenAI account. Add them at [platform.openai.com/settings/billing](https://platform.openai.com/settings/billing). GPT-4o-mini costs roughly $0.00015 per analysis.

## Project Structure

```
Controllers/
  HomeController.cs             # Homepage + dashboard
  JobApplicationsController.cs  # CRUD, search/filter, bulk actions, CSV export
  AnalyzerController.cs         # POST /Analyzer/Analyze — job description parser
  ProfileController.cs          # Full profile + document management + AI scoring
  CoverLetterController.cs      # AI cover letter generation, save, download, delete
  InterviewPrepController.cs    # AI interview prep generation + session storage

Models/
  JobApplication.cs             # Core application entity
  GeneratedCoverLetter.cs       # AI-generated cover letter entity (version history)
  InterviewPrepSession.cs       # AI interview prep session (one per application)
  InterviewQuestion.cs          # Interview question record (category, question, tip)
  UserProfile.cs                # Personal info, skills JSON, target roles JSON
  ResumeVersion.cs              # Resume file metadata + version history
  CoverLetterVersion.cs         # Cover letter file metadata + version history
  Enums/ApplicationStatus.cs    # Saved | Applied | Interview | Offer | Rejected
  Enums/WorkMode.cs             # Remote | Hybrid | OnSite
  ViewModels/                   # DashboardViewModel, CoverLetterGeneratorViewModel,
                                #   InterviewPrepViewModel, JobAnalysisResult,
                                #   ResumeMatchResult, ResumeScoreResult, ProfileViewModel

Services/
  JobAnalyzerService.cs         # URL fetch + HTML strip + OpenAI extraction
  ResumeMatcherService.cs       # PDF text extraction + OpenAI match scoring
  ResumeScoreService.cs         # OpenAI resume quality scoring + suggestions
  CoverLetterGeneratorService.cs # OpenAI cover letter generation
  InterviewPrepService.cs       # OpenAI interview question generation

Views/
  Home/Index.cshtml             # Hero landing page
  Home/Dashboard.cshtml         # Stats + recent applications table
  JobApplications/              # Index (filter bar + bulk actions), Create, Edit, Delete
  CoverLetter/Generate.cshtml   # AI cover letter generator + saved letter history
  InterviewPrep/Prep.cshtml     # AI interview prep generator + question cards
  Profile/Index.cshtml          # Career portfolio (all sections)
  Shared/_Layout.cshtml         # Main layout with dark navbar + toast notifications
  Shared/_AuthLayout.cshtml     # Split-screen auth layout

Data/
  ApplicationDbContext.cs       # EF Core + Identity DbContext
  Migrations/                   # SQLite migrations

uploads/                        # Server-side file storage (gitignored)
  resumes/{userId}/             # Resume PDFs — served via controller, not public
  coverletters/{userId}/        # Cover letter PDFs — served via controller
wwwroot/uploads/photos/         # Profile photos — served as static files
```

## Author

Majd Arow
