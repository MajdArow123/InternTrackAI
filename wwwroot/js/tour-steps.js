// ── Guided tour definitions ───────────────────────────────────────────────
// The only file to edit to change tour copy, targets or order. tour.js is the
// engine and knows nothing about any individual step.
//
// Two rules the test suite enforces (tests/Integration/TourTests.cs):
//   1. The object below is strict JSON — double-quoted keys, no trailing
//      commas, no comments inside it — so the tests can parse it and check
//      every selector against the page it declares.
//   2. A target is "#some-id" or "[data-tour=\"some-hook\"]". No class or
//      descendant selectors: those break silently when a view is restyled.
//
// Tour shape:
//   match:  exact paths (lower-cased on compare) where the nav "Tour" button
//           runs this tour. Empty for "overview", which is the fallback.
//   auto:   path that auto-starts this tour on a first visit, or null.
//   steps:  { view, target, pad, title, body, placement }
//           view      — path to navigate to first, or null to stay put
//           target    — element to spotlight, or null for a centred card
//           pad       — px of breathing room around the target
//           placement — "auto" | "top" | "bottom" (ignored when docked)
(function () {
    'use strict';

    window.TourSteps =
{
  "overview": {
    "label": "App overview",
    "match": [],
    "auto": "/Home/Dashboard",
    "steps": [
      {
        "view": "/Home/Dashboard",
        "target": null,
        "pad": 0,
        "title": "Welcome to InternTrackAI",
        "body": "A quick tour of how the app fits together. Escape ends it, and you can restart from Tour in the nav.",
        "placement": "auto"
      },
      {
        "view": null,
        "target": "[data-tour=\"nav\"]",
        "pad": 8,
        "title": "Getting around",
        "body": "Dashboard for the overview, Applications for the full list and the board, Cover Letter and Profile for your documents.",
        "placement": "bottom"
      },
      {
        "view": null,
        "target": "[data-tour=\"stats\"]",
        "pad": 10,
        "title": "Your pipeline at a glance",
        "body": "Every application sits in one of five stages — Saved, Applied, Interview, Offer, Rejected. These counts follow them as you move them along.",
        "placement": "auto"
      },
      {
        "view": null,
        "target": "#attention-card",
        "pad": 10,
        "title": "What needs you today",
        "body": "Deadlines about to pass, interviews coming up, and applications that have gone quiet long enough to chase. Mark contacted, snooze, or draft a follow-up without leaving this page.",
        "placement": "auto"
      },
      {
        "view": "/JobApplications",
        "target": "[data-tour=\"view-toggle\"]",
        "pad": 8,
        "title": "A list or a board",
        "body": "The same applications two ways: a filterable table, or a Kanban board where dragging a card between columns changes its status.",
        "placement": "auto"
      },
      {
        "view": "/JobApplications/Create",
        "target": "#analyzerCard",
        "pad": 10,
        "title": "Let the AI fill the form",
        "body": "Paste a job posting or its URL. The analyzer fills in the company, role, salary and dates, then scores your resume against it.",
        "placement": "auto"
      },
      {
        "view": "/Profile",
        "target": "#resumeHeroCard",
        "pad": 10,
        "title": "Your resume powers the rest",
        "body": "Upload a PDF here and every AI feature reads from it — match scores, cover letters, interview prep, follow-up drafts. That's the tour. Hit Tour in the nav to see it again.",
        "placement": "auto"
      }
    ]
  },

  "applications": {
    "label": "Applications tour",
    "match": ["/JobApplications", "/JobApplications/Board"],
    "auto": null,
    "steps": [
      {
        "view": null,
        "target": "[data-tour=\"view-toggle\"]",
        "pad": 8,
        "title": "A list or a board",
        "body": "List view is a filterable table; Board view is a Kanban with one column per stage. Both show the same applications, and the app remembers which you used last.",
        "placement": "auto"
      },
      {
        "view": null,
        "target": "[data-tour=\"stage-pills\"]",
        "pad": 8,
        "title": "Stages, at a glance",
        "body": "Every application is Saved, Applied, Interview, Offer or Rejected. Click a pill to see just that stage, or drag a card between columns on the board to move it.",
        "placement": "bottom"
      },
      {
        "view": null,
        "target": "[data-tour=\"filters\"]",
        "pad": 8,
        "title": "Find anything fast",
        "body": "Search by company or role, narrow by work mode, and sort. Needs attention pulls out the ones with a deadline, an interview, or an overdue follow-up.",
        "placement": "auto"
      }
    ]
  },

  "profile": {
    "label": "Profile tour",
    "match": ["/Profile"],
    "auto": null,
    "steps": [
      {
        "view": null,
        "target": "#resumeHeroCard",
        "pad": 10,
        "title": "Start with your resume",
        "body": "Drop a PDF here and the app reads it once. Everything downstream — match scores, cover letters, interview prep, follow-ups — works from the resume marked active.",
        "placement": "auto"
      },
      {
        "view": null,
        "target": "[data-tour=\"resume-versions\"]",
        "pad": 8,
        "title": "Keep more than one version",
        "body": "Upload a version per track — backend, frontend, data — and set the right one active before you apply. With two or more, the dashboard compares their response rates.",
        "placement": "auto"
      },
      {
        "view": null,
        "target": "#resumeAiRow",
        "pad": 8,
        "title": "Analyze vs Score",
        "body": "Analyze with AI reads the resume and fills in your name, skills and target roles. Score my resume grades the resume on its own, with strengths and improvements — no job posting needed.",
        "placement": "auto"
      }
    ]
  }
};
})();
