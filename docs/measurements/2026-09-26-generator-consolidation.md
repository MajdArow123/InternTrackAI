# Generator consolidation re-measure — 2026-09-26

**Question.** If the interview-prep page used the practice generator (one call per category, with the application
attached) instead of its own, would prep questions get topics narrow enough to take part in `TopicKey` dedupe,
without the questions getting worse? CLAUDE.md §12, deferred item 1; §8, "Interview-prep topics are stored but never block".

**Decision rule, fixed before the run.** Consolidate only if **at least 80% of side B's topics pass the topic rule's
own test** *and* the maintainer judges side B's company-specific questions **no worse** than side A's.

**The test.** `PracticePrompt.TopicRule`: *"if you could write ten more different questions on the topic, it is
still too broad."* A topic passes when it names one specific thing, at the level of the rule's examples
("connection pooling", "telling a client about a missed deadline"); it fails at category level ("teamwork",
"databases"). Borderline cases are marked and counted both ways.

**Budget.** 8 calls, agreed with the maintainer, enforced by the probe's handler (it throws on a ninth).
Used: 8. No practice generation needed its top-up call.

**Re-running.** `tests/InternTrackAI.Tests/LiveConsolidationProbe.cs` — off unless `INTERNTRACK_LIVE_AI` is set and
`INTERNTRACK_PROBE_POSTINGS` names a JSON file of the postings (not committed: third-party ads, public repo; compare
the SHA-256 prefixes below to confirm the same text). Agree a call budget first (§8 working rules).

## Raw output (verbatim)

Model calls: 8 of 8. Model: gpt-4o-mini. Difficulty for side B: Medium (what prep rows store).

Inputs (posting text is not committed — public repo, third-party ads; hashes identify it):
- Shopify — Software Engineering Intern: 4446 chars, SHA-256 B4A491F5340F3591…
- CIBC — Cloud DevOps Intern: 1556 chars, SHA-256 C7F68796EECCC1BC…
- Resume: the synthetic fixture `LiveConsolidationProbe.SyntheticResume` (side A only; the practice generator takes no resume).

### Shopify — Software Engineering Intern

#### A. Interview-prep generator (one call)
- [Technical] topic: `REST API design` — Can you explain how you would design a REST API for a new feature in a Shopify application?
- [Technical] topic: `Performance optimization` — Describe your experience with performance optimization. How did you identify and resolve performance issues in your previous projects?
- [Technical] topic: `Software reliability` — What strategies would you use to ensure the reliability of the software you develop, particularly in a cloud environment like AWS?
- [Technical] topic: `CI/CD with Docker` — How would you implement CI/CD for a project that uses Docker? What steps are involved?
- [Behavioral] topic: `Team challenges` — Tell me about a time you faced a significant challenge while working on a team project. How did you handle it?
- [Behavioral] topic: `Learning new technologies` — Describe a situation where you had to learn a new technology quickly. What approach did you take?
- [Behavioral] topic: `Task prioritization` — How do you prioritize tasks when working on multiple projects with tight deadlines?
- [Company-Specific] topic: `Company mission` — What do you know about Shopify's mission and how do you think your role as an intern contributes to it?
- [Company-Specific] topic: `AI in e-commerce` — How do you see the role of AI in enhancing e-commerce platforms like Shopify?

#### B. Practice generator, per category, with this application attached
- [Technical] topic: `REST vs GraphQL APIs` — Can you explain the differences between REST and GraphQL APIs, and in what scenarios you would prefer one over the other?
- [Technical] topic: `Error handling in RESTful APIs` — Describe how you would implement error handling in a RESTful API built with Python. What best practices would you follow?
- [Technical] topic: `Optimizing Docker builds` — When using Docker, how would you optimize the build process for a multi-service application?
- [Technical] topic: `Designing responsive web applications in React` — How would you approach designing a responsive web application using React? What key considerations would you keep in mind?
- [Behavioral] topic: `backend service development` — Can you describe a project where you used Python to develop a backend service? What challenges did you face, and how did you address them?
- [Behavioral] topic: `collaboration with UX designers` — Reflect on a time when you collaborated with a UX designer on a project. How did you ensure that the technical implementation aligned with the user experience goals?
- [Behavioral] topic: `performance debugging` — Tell me about a time you had to debug a performance issue in an application. What steps did you take to identify and resolve the problem?
- [Company-Specific] topic: `RESTful APIs vs GraphQL` — Can you compare the advantages and disadvantages of using RESTful APIs versus GraphQL for building backend services at Shopify?
- [Company-Specific] topic: `application performance optimization` — How would you approach debugging an application that runs slowly under heavy load? What tools or methods might you use?

### CIBC — Cloud DevOps Intern

#### A. Interview-prep generator (one call)
- [Technical] topic: `CI/CD pipeline setup` — Can you explain how you would set up a CI/CD pipeline using GitHub Actions for a cloud-based application?
- [Technical] topic: `Infrastructure-as-Code advantages` — What are the key benefits of using Infrastructure-as-Code with Terraform compared to traditional infrastructure management?
- [Technical] topic: `Docker containerization` — Describe how you would containerize a Python application using Docker. What key files would you include?
- [Technical] topic: `Application monitoring` — How would you monitor the health of a cloud application deployed on Azure? What tools would you use?
- [Behavioral] topic: `Problem-solving under pressure` — Describe a time when you faced a significant challenge while working on a project. How did you approach the problem?
- [Behavioral] topic: `Team collaboration` — Can you give an example of how you collaborated with a team to improve a project's outcome?
- [Behavioral] topic: `Adaptability and learning` — Tell me about a time when you had to learn a new technology quickly to complete a project. How did you manage?
- [Company-Specific] topic: `Interest in CIBC and Cloud Engineering` — What interests you about working in the Cloud Engineering team at CIBC, and how do you see yourself contributing?
- [Company-Specific] topic: `Regulatory considerations in DevOps` — CIBC operates in a regulated industry. How do you think this affects the DevOps practices within the organization?

#### B. Practice generator, per category, with this application attached
- [Technical] topic: `CI/CD tools comparison` — Can you explain the trade-offs between using GitHub Actions and Azure DevOps for CI/CD pipelines in a cloud environment?
- [Technical] topic: `Containerization best practices` — When containerizing a legacy application using Docker, what considerations should you keep in mind to ensure a smooth transition?
- [Technical] topic: `Infrastructure-as-Code with Terraform` — Describe how you would use Terraform to manage infrastructure for a cloud-based application. What advantages does Infrastructure-as-Code provide?
- [Technical] topic: `Cloud security in regulated industries` — In the context of a regulated industry like banking, what security measures would you implement to safeguard cloud infrastructure?
- [Behavioral] topic: `CI/CD pipeline development` — Can you describe a time when you built a CI/CD pipeline? What tools did you use, and what challenges did you face during the process?
- [Behavioral] topic: `Deployment automation` — Tell me about a project where you automated deployment workflows. What technologies did you choose, and what were the trade-offs of your approach?
- [Behavioral] topic: `Incident response` — Give an example of a time you had to troubleshoot a production issue. What steps did you take to identify and resolve the problem?
- [Company-Specific] topic: `CI/CD tools comparison` — Can you explain the trade-offs between using Azure DevOps and GitHub Actions for CI/CD pipelines in the context of a cloud environment?
- [Company-Specific] topic: `Deployment automation` — How would you approach automating deployment workflows to reduce manual release steps while ensuring reliability in a regulated environment like banking?


## Classification — side B (practice generator), 18 topics

| posting | category | topic | passes? | why |
|---|---|---|---|---|
| Shopify | Technical | REST vs GraphQL APIs | ✓ | one named comparison |
| Shopify | Technical | Error handling in RESTful APIs | ✓ | one aspect of one thing |
| Shopify | Technical | Optimizing Docker builds | ✓ | one task |
| Shopify | Technical | Designing responsive web applications in React | ~ borderline | "responsive design" spans layout, images, breakpoints, performance |
| Shopify | Behavioral | backend service development | ✗ | category-level |
| Shopify | Behavioral | collaboration with UX designers | ✓ | one relationship, like the rule's "disagreeing with a senior colleague" |
| Shopify | Behavioral | performance debugging | ~ borderline | a whole discipline, but narrower than "debugging" |
| Shopify | Company-Specific | RESTful APIs vs GraphQL | ✓ | specific — but not company-specific, and repeats the Technical topic |
| Shopify | Company-Specific | application performance optimization | ✗ | category-level; nothing about Shopify |
| CIBC | Technical | CI/CD tools comparison | ✓ | one comparison (GitHub Actions vs Azure DevOps) |
| CIBC | Technical | Containerization best practices | ✗ | category-level |
| CIBC | Technical | Infrastructure-as-Code with Terraform | ✗ | a whole tool |
| CIBC | Technical | Cloud security in regulated industries | ✗ | category-level |
| CIBC | Behavioral | CI/CD pipeline development | ✗ | category-level |
| CIBC | Behavioral | Deployment automation | ✗ | category-level |
| CIBC | Behavioral | Incident response | ✗ | category-level |
| CIBC | Company-Specific | CI/CD tools comparison | ✓ | specific — but a near-copy of the Technical question above |
| CIBC | Company-Specific | Deployment automation | ✗ | category-level; repeats the Behavioral topic |

**Side B: 7 of 18 pass (39%) strictly; 9 of 18 (50%) counting both borderline topics as passes.**
Side A (prep generator), same test: 0 of 18 strictly — "REST API design", "Performance optimization", "Team
challenges", "Company mission", … are all category-level, matching the 2026-09-24 result (0 of 27).

## Result

**Do not consolidate.** Side B misses the 80% threshold by a wide margin (39–50%), so the first half of the rule
fails before the quality judgment is needed. The prep-topics decision in CLAUDE.md §8 stands: prep topics stay
descriptive and out of dedupe.

For the maintainer's side-by-side, the plain differences in the questions:

- **Company-specific questions are better on side A.** Side A asks about the company ("Shopify's mission",
  "AI in e-commerce", "CIBC operates in a regulated industry — how does that affect DevOps?", "the Cloud
  Engineering team at CIBC"). Side B's company-specific slot produced generic technical questions with the
  company's name attached ("…for building backend services at Shopify") or copies of its own Technical questions.
- **Behavioral questions are more role-grounded on side B** ("collaborated with a UX designer", "automated
  deployment workflows") where side A's are generic ("a time you faced a challenge", "learn a new technology
  quickly") — but side B's read as technical-experience questions, and two repeat topics from other categories.
- **Side A uses the resume** (AWS, Docker/CI/CD from the fixture); side B takes no resume at all.

## Findings that apply to the app today, not only to consolidation

1. **The practice generator's topics broaden when an application is attached.** In general practice
   (`LiveDedupeProbe`, 2026-09-21) topic granularity held across six batches; here, grounded in a posting, only
   39–50% pass. The practice page's **"Get more for this role"** uses exactly this path (`applicationId` set), so
   role-grounded practice questions are being stored with broad topics that feed `TopicKey` exclusions.
2. **Cross-category near-duplicates for one role are not caught.** CIBC's Technical and Company-Specific batches
   returned the same CI/CD comparison question (same topic, words reordered, "in the context of" added). Topic
   dedupe is scoped per (difficulty, category) on purpose, and `QuestionHash` catches reordering but not the added
   words — so a user who generates Technical and then Company-Specific questions for the same role can get the same
   question twice.
3. **The practice generator's Company-Specific category does not produce company-specific questions** when a posting
   is attached — it has no instruction or input about the company beyond its name and the posting.

Recorded here on 2026-09-26. **Finding 2 was fixed the same day** by a near-duplicate check on question text (`QuestionHash.IsNearDuplicate`, threshold 0.8, calibrated on the 630 pairs of this document's 36 questions: the duplicate above scored 0.89, the next pair 0.60). Finding 3 was taken up in the same PR, measured separately. Finding 1 is deferred (CLAUDE.md §12).
