# InternTrackAI

**Your internship search, organized — with AI doing the busywork.**

[![CI](https://github.com/MajdArow123/InternTrackAI/actions/workflows/ci.yml/badge.svg)](https://github.com/MajdArow123/InternTrackAI/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![ASP.NET Core MVC](https://img.shields.io/badge/ASP.NET%20Core-MVC-512BD4?logo=dotnet)](https://learn.microsoft.com/aspnet/core)
[![OpenAI](https://img.shields.io/badge/AI-GPT--4o--mini-412991?logo=openai)](https://platform.openai.com/)
[![Deployed on Railway](https://img.shields.io/badge/Deployed%20on-Railway-0B0D0E?logo=railway)](https://interntrackai-production.up.railway.app)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

InternTrackAI is a full-stack internship application tracker built with ASP.NET Core 9 MVC. It replaces the typical spreadsheet with a real pipeline: paste a job posting and let AI fill out the form, see instantly how well your resume matches, track every application through five stages, and generate cover letters and interview prep on demand — all in one place, in a clean Apple-style interface with full dark mode.

![InternTrackAI landing page](docs/screenshots/home.png)

## 🚀 Live Demo

**[interntrackai-production.up.railway.app](https://interntrackai-production.up.railway.app)**

Click **Try the live demo** on the landing page to be signed straight into a demo account with a sample profile, a resume, and job applications spanning every match-score tier. No signup needed.

If you'd rather sign in manually:

| | |
|---|---|
| **Email** | `demo@interntrackai.com` |
| **Password** | `CvjOukfKXS8YaAR7!` |

> The demo account is shared and public — please don't change its password or delete its data. Feel free to add, edit, or delete job applications to explore the AI features.

## ✨ Key Features

**AI tools**

- 🧠 **Job analyzer** — paste a job description or URL and the form auto-fills with company, role, location, salary, and required skills
- 🎯 **Resume match scoring** — a 0–100 match ring for every role, with matched and missing skills side by side and a plain-language summary
- ✍️ **Cover letter generator** — a personalized letter written from the posting, your resume, and your profile; improve it with a one-line instruction, save versions, and download as PDF
- 🎤 **Interview prep** — technical, behavioral, and company-specific questions with tips, plus AI feedback on your written answers
- 💵 **Salary insight** — a typical pay range for the role, company, and location while you're filling in the form
- 📄 **Resume tools** — AI extracts your name, skills, and target roles from your resume, and scores it with strengths and improvements

**Tracking**

- 📋 **Five-stage pipeline** — Saved, Applied, Interview, Offer, Rejected, with status filter pills, search, and sorting
- 📂 **Detail drawer** — the full posting, AI analysis, key details, a status timeline, and a notes log without leaving the list
- ✅ **Bulk actions** — select rows to change status, delete, or compare up to three applications side by side
- 📊 **Dashboard** — headline stats, an applications-over-time chart, a pipeline funnel, top companies, follow-up reminders, and upcoming deadlines
- 🔁 **CSV import and export** — move your data in and out in one click

**Profile and account**

- 👤 **Profile** — photo, basic info, skill and target-role chips, versioned resumes and cover letters with an active version, and your public GitHub repos
- 🔗 **Public profile** — an optional read-only page with your name, skills, target roles, and stats, on a regenerable link
- 🌗 **Dark / light mode** — Apple-style design system with soft surfaces, pill buttons, and a frosted navbar; the toggle persists across sessions
- ⌨️ **Keyboard shortcuts** — press `?` anywhere for the list
- 🔐 **Account management** — register, sign in, forgot/reset password, display name, and change password via ASP.NET Core Identity

## 🛠️ Tech Stack

| Layer | Technology |
|---|---|
| Framework | ASP.NET Core 9 (C#) |
| Database | SQLite (local) / PostgreSQL (production), via Entity Framework Core 9 |
| Frontend | Razor views, Bootstrap 5 (restyled with a custom token-based design system), vanilla JavaScript, Chart.js, jsPDF — no SPA framework |
| AI | OpenAI API (GPT-4o-mini) |
| Auth | ASP.NET Core Identity |
| Hosting | Railway (Docker) |

## 📸 Screenshots

| Sign In | Dashboard |
|---|---|
| ![Sign In](docs/screenshots/login.png) | ![Dashboard](docs/screenshots/dashboard.png) |

| Applications | Detail Drawer |
|---|---|
| ![Applications](docs/screenshots/applications.png) | ![Detail Drawer](docs/screenshots/drawer.png) |

| Add Application — AI Analysis + Resume Match |
|---|
| ![Add Application](docs/screenshots/create.png) |

| Cover Letter | Interview Prep |
|---|---|
| ![Cover Letter](docs/screenshots/cover_letter.png) | ![Interview Prep](docs/screenshots/interview_prep.png) |

| Dark Mode | Profile |
|---|---|
| ![Dark Mode](docs/screenshots/dark_mode.png) | ![Profile](docs/screenshots/profile.png) |

## 💻 How to Run Locally

**Prerequisites:** .NET 9 SDK, an OpenAI API key

```bash
git clone https://github.com/MajdArow123/InternTrackAI.git
cd InternTrackAI
```

Set your OpenAI API key using .NET User Secrets (never stored in source files):

```bash
dotnet user-secrets set "OpenAI:ApiKey" "sk-..."
```

Run the app:

```bash
dotnet run
```

Open [http://localhost:5240](http://localhost:5240) and register an account to get started. Locally, the app uses a SQLite file (`app.db`) created automatically via EF Core migrations — no extra database setup needed. Uploaded files go to `./uploads` unless you set `UPLOADS_PATH`.

> AI features require billing credits on your OpenAI account, added at [platform.openai.com/settings/billing](https://platform.openai.com/settings/billing). GPT-4o-mini costs roughly $0.00015 per analysis.

Optional: to enable the one-click **Try the live demo** button locally, point it at any account you've registered:

```bash
dotnet user-secrets set "Demo:Email" "you@example.com"
dotnet user-secrets set "Demo:Password" "your-password"
```

## ☁️ Deployment

The live instance runs on [Railway](https://railway.app), built directly from the `Dockerfile` in this repo:

- **Database** — Railway-managed PostgreSQL. `Program.cs` detects the `DATABASE_URL` environment variable Railway injects and switches the EF Core provider from SQLite to Npgsql automatically.
- **Persistent storage** — uploaded resumes, cover letters, and profile photos are written to the directory named by `UPLOADS_PATH`, which points at a mounted Railway volume so files survive redeploys. Resumes and cover letters are never served as static files; only profile photos are public.
- **Data Protection keys** — persisted to the database so antiforgery tokens and cookies stay valid across container restarts.
- **Migrations** — applied automatically on startup, so deploying a new migration is just a `git push`.

To deploy your own copy: create a Railway project, add a PostgreSQL service, attach a volume (mounted at `/data`), point Railway at this repo, and set these variables on the service — `railway.toml` already configures the build and health check.

| Variable | Purpose |
|---|---|
| `OpenAI__ApiKey` | OpenAI API key for all AI features |
| `UPLOADS_PATH` | Upload root on the volume, e.g. `/data/uploads` |
| `Demo__Email` / `Demo__Password` | Optional. Credentials of the account behind the **Try the live demo** button; leave unset to hide it |

(`DATABASE_URL` and `PORT` are injected by Railway automatically.)

## License

Released under the [MIT License](LICENSE).

## Author

**Majd Arow** — [GitHub](https://github.com/MajdArow123) · [LinkedIn](https://www.linkedin.com/in/majd-arow-92b97719a)
