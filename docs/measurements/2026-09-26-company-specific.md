# Company-Specific practice questions for a posting — 2026-09-26

**Defect.** On the practice page, asking for Company-Specific questions with an application attached produces
questions about the posting's *requirements*, with the company's name attached. The category is meant to ask
about motivation, fit and knowledge of the employer. Cause: `PracticePrompt.Build` adds "Derive the questions
from the responsibilities and requirements this posting actually lists" whenever a posting is attached, and for
this category that overrides the category rule.

## Decision rule — written and committed before any call was made

**Definitions**, so the verdict is a lookup, not an argument:

- **Company-specific (passes).** The question asks about the candidate's *motivation for*, *fit with* or
  *knowledge of* this employer, **and** refers to something the posting states about the employer beyond its
  name — its product, customers, industry, team, mission, or how it works. Test: it could not be asked unchanged
  at a different company.
- **Requirements question (fails, and counts against the zero-tolerance half).** The question asks whether or how
  the candidate can do something the posting lists as a responsibility or requirement — a skill, tool, technique
  or task — **whether or not it names the company**. "How would you set up CI/CD at CIBC?" is a requirements
  question.
- **Generic (fails, not a requirements question).** Motivation or fit with nothing specific to this employer:
  "Why do you want this internship?"

**Rule.** Ship the prompt change only if, across the 4 batches below (8 questions), **at least 7 of 8 are
company-specific and none is a requirements question.** Otherwise the change does not ship; this document is
committed with the result either way.

**Run.** 4 calls, agreed with the maintainer: Company-Specific, Medium, 2 questions per batch, the same two
postings × 2 repeats, through `PracticeQuestionService` with the application attached (the practice page's own
path), each batch on a fresh database so no exclusion list or near-duplicate check shapes the output.
`LiveCompanySpecificProbe`, hard cap 4.

## Before: the current prompt

The same two postings as `2026-09-26-generator-consolidation.md` — **confirmed identical**: the SHA-256 prefixes
match (Shopify SWE `B4A491F5340F3591…`, CIBC Cloud DevOps `C7F68796EECCC1BC…`), with the same model, difficulty,
profile context and prompt code (`PracticePrompt` has not changed since that run). Its side B, Company-Specific
rows, are the "before" for this document:

| posting | question (verbatim) | verdict |
|---|---|---|
| Shopify | Can you compare the advantages and disadvantages of using RESTful APIs versus GraphQL for building backend services at Shopify? | requirements |
| Shopify | How would you approach debugging an application that runs slowly under heavy load? What tools or methods might you use? | requirements |
| CIBC | Can you explain the trade-offs between using Azure DevOps and GitHub Actions for CI/CD pipelines in the context of a cloud environment? | requirements |
| CIBC | How would you approach automating deployment workflows to reduce manual release steps while ensuring reliability in a regulated environment like banking? | requirements |

**Before: 0 of 4 company-specific, 4 requirements questions.**

## After: the prompt that was tried

For Company-Specific questions with a posting attached, the generic posting instruction was replaced with
(for the other categories, and with no posting, the prompt was unchanged):

> This category is about {company}, not about the job's requirements. Use what the posting says about {company}
> itself — its product, customers, industry, team, mission or how it works — and ask about the candidate's
> motivation for, fit with, or knowledge of {company}. Every question must be one you could not ask unchanged at
> another company: it names {company} and something the posting says about it. Never ask whether or how the
> candidate can do a skill, tool or task the posting lists — that is a Technical question, not this category.

Run: 4 of 4 calls, inputs confirmed by the same hashes (Shopify `B4A491F5340F3591…`, CIBC `C7F68796EECCC1BC…`).

| posting | batch | question (verbatim) | verdict |
|---|---|---|---|
| Shopify | 1 | Can you explain how Shopify's mission to make commerce better for everyone aligns with your personal values and professional goals as a software engineer? | company-specific |
| Shopify | 1 | Shopify serves millions of merchants globally. How do you think this scale impacts the design and development of software solutions compared to a startup environment? | borderline — about Shopify's situation, but an engineering-opinion question; counted both ways below |
| Shopify | 2 | How do you think Shopify's mission to make commerce better for everyone impacts the way software is developed and delivered to merchants? | company-specific |
| Shopify | 2 | Given that Shopify supports millions of merchants worldwide, what strategies would you suggest for ensuring the scalability and reliability of the software systems you develop? | **requirements** — asks how the candidate would do a task the posting lists, with the company as a preamble |
| CIBC | 1 | CIBC emphasizes digital banking platforms in its mission. How do you think the role of a Cloud DevOps Intern contributes to enhancing customer experience in this context? | company-specific |
| CIBC | 1 | Given that CIBC operates in a regulated industry, what specific challenges do you think a Cloud DevOps Intern might face compared to a similar role in a non-regulated industry? | company-specific |
| CIBC | 2 | Can you describe how CIBC's mission to provide exceptional digital banking experiences aligns with your personal values and career aspirations in software engineering? | company-specific |
| CIBC | 2 | CIBC emphasizes collaboration with development teams to improve deployment reliability. How do you believe cross-functional collaboration can enhance the CI/CD process, and can you provide an example from your experience? | **requirements** — the "emphasis" is a listed responsibility; the question is about doing CI/CD |

## Result

**5 of 8 company-specific (6 counting the borderline one), 2 requirements questions. The rule needed at least 7 of
8 and none, so the prompt change does not ship.** The near-duplicate fix in the same PR was independent and
landed regardless.

It is a real improvement — from 0 of 4 to 5–6 of 8 — and the misses have one shape worth recording for a later
attempt: **the model turns a responsibility the posting phrases as the company's emphasis** ("CIBC emphasizes
collaboration with development teams to improve deployment reliability", "supports millions of merchants") **into
a question about doing that work, with the company as a preamble.** The instruction "never ask whether or how the
candidate can do a skill, tool or task the posting lists" did not catch that framing. Recorded in CLAUDE.md §12's
deferred list; any retry needs its own budget and its own pre-registered rule.
