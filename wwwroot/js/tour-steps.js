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
//           runs this tour. A page no tour claims gets an offer card instead —
//           the Tour button never navigates away on its own.
//   steps:  either one target —
//             { view, target, pad, title, body, placement }
//           or an ordered list of fallbacks, best first —
//             { view, pad, placement, targets: [ { target, title, body, view?, when? } ] }
//           view      — path to navigate to first, or null to stay put
//           target    — element to spotlight, or null for a centred card
//           when      — a flag from the dashboard's #tourContextData that must be
//                       true for this entry to be tried (e.g. "pendingDraft")
//           pad       — px of breathing room around the target
//           placement — "auto" | "top" | "bottom" (ignored when docked)
//   Nothing auto-runs: the dashboard invites ([data-tour-prompt] and the
//   onboarding card's link), and tour.js remembers the answer per browser.
(function () {
    'use strict';

    window.TourSteps =
{
  "overview": {
    "label": "App overview",
    "match": ["/Home/Dashboard"],
    "steps": [
      {
        "pad": 8,
        "placement": "auto",
        "targets": [
          {
            "when": "pendingDraft",
            "view": "/Profile/ReviewResume",
            "target": "[data-tour=\"review-skill\"]",
            "title": "Start with your resume",
            "body": "The AI reads your resume and shows what it found, each skill beside the line it came from. Nothing reaches your profile until you press Apply — and every other AI feature works from what you keep."
          },
          {
            "view": "/Profile",
            "target": "#resumeAiRow",
            "title": "Start with your resume",
            "body": "Analyze with AI reads your active resume and shows what it found, each skill beside the line it came from. You choose what to keep, and every other AI feature works from it."
          },
          {
            "view": "/Profile",
            "target": "[data-tour=\"resume-upload\"]",
            "title": "Start with your resume",
            "body": "Upload a PDF or Word resume and the AI shows you what it found before anything is saved. Match scores, cover letters and interview prep all work from it."
          }
        ]
      },
      {
        "view": "/Home/Dashboard",
        "pad": 8,
        "placement": "bottom",
        "targets": [
          {
            "target": "[data-tour=\"nav\"]",
            "title": "Getting around",
            "body": "Dashboard for the overview, Applications for the list and the board, Practice for interview questions scored as you answer, Cover Letter and Profile for your documents."
          },
          {
            "target": "[data-tour=\"nav-toggle\"]",
            "title": "Getting around",
            "body": "Everything lives in this menu: the Dashboard, Applications as a list or a board, Practice for interview questions scored as you answer, Cover Letter and Profile."
          }
        ]
      },
      {
        "view": "/Home/Dashboard",
        "pad": 8,
        "placement": "auto",
        "targets": [
          {
            "target": "[data-tour=\"inbox-suggestion\"]",
            "title": "Your inbox, read for you",
            "body": "With Gmail connected, the app reads recruiter emails and suggests the status change — an interview invite, an offer, a rejection. Accept or dismiss: nothing moves on its own."
          }
        ]
      },
      {
        "view": "/Home/Dashboard",
        "pad": 8,
        "placement": "auto",
        "targets": [
          {
            "target": "[data-tour=\"attention-item\"]",
            "title": "What needs you today",
            "body": "Deadlines about to pass, interviews coming up, and applications that have gone quiet long enough to chase. Draft a follow-up, mark it contacted or snooze it here — and an upcoming interview gets a Practice for this interview button."
          },
          {
            "target": "[data-tour=\"onboarding-add\"]",
            "title": "Start with one application",
            "body": "Add an application and this page starts working for you: deadlines, interviews and follow-ups due all surface here on their own."
          }
        ]
      },
      {
        "view": "/Practice",
        "pad": 8,
        "placement": "auto",
        "targets": [
          {
            "target": "[data-tour=\"practice-answered\"]",
            "title": "Practice for the interview you have",
            "body": "Practice for this interview writes questions from that posting. Every answer comes back scored out of 5, with what worked, exactly two things to change and a stronger opening line. That's the tour — run it again any time from Tour on the dashboard."
          },
          {
            "target": "[data-tour=\"practice-question\"]",
            "title": "Practice for the interview you have",
            "body": "Questions written for your field and level, or from a posting when you have an interview coming. Type an answer and it comes back scored out of 5, with what worked and exactly two things to change. That's the tour — run it again any time from Tour on the dashboard."
          },
          {
            "target": "[data-tour=\"practice-example\"]",
            "title": "Practise before the interview",
            "body": "An example question for your field. Get your own set, or questions from a posting once you have an interview, and every answer comes back scored out of 5 with exactly two things to change. That's the tour — run it again any time from Tour on the dashboard."
          },
          {
            "target": "#practiceGenerateBtn",
            "title": "Practise before the interview",
            "body": "Press this for five questions in your field, at the difficulty you pick. Every answer comes back scored out of 5, with what worked and exactly two things to change. That's the tour — run it again any time from Tour on the dashboard."
          }
        ]
      }
    ]
  },

  "applications": {
    "label": "Applications tour",
    "match": ["/JobApplications", "/JobApplications/Board"],
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

  "practice": {
    "label": "Practice tour",
    "match": ["/Practice"],
    "steps": [
      {
        "view": null,
        "target": "[data-tour=\"practice-filters\"]",
        "pad": 8,
        "title": "Pick a level and a kind of question",
        "body": "These do two jobs: they filter the list below, and they decide what Get more questions writes next.",
        "placement": "auto"
      },
      {
        "view": null,
        "target": "#practiceGenerateBtn",
        "pad": 8,
        "title": "Five questions at a time",
        "body": "Written for your field and level. Each batch is checked against the questions you already have, so repeats are dropped.",
        "placement": "auto"
      },
      {
        "view": null,
        "pad": 8,
        "placement": "auto",
        "targets": [
          {
            "target": "[data-tour=\"practice-answered\"]",
            "title": "Every answer, scored",
            "body": "A score out of 5, what worked, exactly two things to change and a stronger opening line. Retry, and your attempts sit side by side."
          },
          {
            "target": "[data-tour=\"practice-question\"]",
            "title": "Answer in your own words",
            "body": "Type an answer and it comes back scored out of 5, with what worked and exactly two things to change. Type into several and a bar appears to score them all at once."
          },
          {
            "target": "[data-tour=\"practice-example\"]",
            "title": "What you would practise",
            "body": "An example question for your field. Get your own set and every answer you type comes back scored out of 5, with exactly two things to change."
          }
        ]
      },
      {
        "view": null,
        "target": "#practiceProgress",
        "pad": 8,
        "title": "Your progress",
        "body": "How many you have answered and your average, your weakest topic once it has three answers, and how much of each difficulty you have covered.",
        "placement": "auto"
      }
    ]
  },

  "profile": {
    "label": "Profile tour",
    "match": ["/Profile"],
    "steps": [
      {
        "view": null,
        "target": "[data-tour=\"resume-upload\"]",
        "pad": 8,
        "title": "Start with your resume",
        "body": "Drop a PDF or Word file here and the app reads it once. Everything downstream — match scores, cover letters, interview prep, follow-ups — works from the resume marked active.",
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
        "body": "Analyze with AI reads the resume and shows you what it found, each skill beside the line it came from — you choose what to keep before anything is saved. Score my resume grades it on its own, with strengths and improvements.",
        "placement": "auto"
      }
    ]
  }
};
})();
