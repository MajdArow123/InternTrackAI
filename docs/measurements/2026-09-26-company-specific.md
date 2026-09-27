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
