// A local stand-in for the OpenAI Chat Completions endpoint, for e2e runs and for manual verification.
//
// Every AI service posts to OpenAiEndpoint.ChatCompletions, which honours OpenAI:BaseUrl, so pointing that
// setting here routes the whole app to this file and nothing reaches OpenAI. Replies are routed by the
// request's system prompt and shaped the way each service parses them. Every call is recorded, which is
// what lets a check assert "that cost zero model calls", and the next N calls can be told to fail.
//
// In the e2e harness:   const stub = await startStub(); ... stub.calls, stub.failNext(1), await stub.close()
// Standalone:           node e2e/lib/openai-stub.mjs [port]      (default 5999; logs one line per call)
//                       then run the app with OpenAI__ApiKey=sk-stub OpenAI__BaseUrl=http://127.0.0.1:5999
import http from 'node:http';
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

// Distinct topics and prompts, handed out in order across the whole run so no two generated practice
// questions share a hash or a topic — dedupe never swallows a batch the checks are counting on.
const SUBJECTS = [
  'idempotent payment retries', 'feature flag rollout', 'graceful degradation', 'log correlation',
  'zero-downtime schema migration', 'rate limiter design', 'cache stampede', 'connection pool sizing',
  'dead letter queues', 'optimistic locking', 'pagination cursors', 'secrets rotation',
  'blue green deploys', 'backpressure', 'circuit breakers', 'read replicas', 'webhook signatures',
  'clock skew', 'bulk import validation', 'audit logging', 'queue fan-out', 'cold start latency',
  'index selectivity', 'retry jitter', 'session fixation', 'content negotiation', 'etag caching',
  'batch job checkpoints', 'multi-tenant isolation', 'graceful shutdown', 'saga compensation',
  'hot partition keys', 'request coalescing', 'soft deletes', 'config drift', 'canary analysis',
];

// Interview prep: 12 questions handed out 4 per call, cycling — calls 1-3 each add four new questions and
// call 4 repeats call 1 exactly, which is the "regenerate stored nothing" path on the prep page.
const PREP = [
  ['Technical', 'How would you design an idempotent payments endpoint?', 'Mention keys and retries.', 'idempotent payment endpoints'],
  ['Technical', 'How do you find a slow query in Postgres?', 'EXPLAIN ANALYZE.', 'postgres query diagnosis'],
  ['Behavioral', 'Tell me about a time you disagreed with a reviewer.', 'Use STAR.', 'handling code review disagreement'],
  ['Company-Specific', 'Why do you want to work on merchant tooling?', 'Tie it to a merchant you know.', 'interest in merchant tooling'],
  ['Technical', 'How would you roll back a bad deploy?', 'Feature flags first.', 'deploy rollback'],
  ['Technical', 'What does a connection pool protect against?', 'Exhaustion.', 'connection pooling'],
  ['Behavioral', 'Describe a deadline you nearly missed.', 'What changed after.', 'recovering a slipping deadline'],
  ['Company-Specific', 'What would you change about the checkout flow?', 'Be specific.', 'checkout flow critique'],
  ['Technical', 'How do you make a background job safe to retry?', 'Idempotency again.', 'retry-safe background jobs'],
  ['Technical', 'When would you denormalise a table?', 'Read-heavy paths.', 'denormalisation trade-offs'],
  ['Behavioral', 'Tell me about teaching a teammate something.', 'Outcome.', 'mentoring a teammate'],
  ['Company-Specific', 'How would you explain the platform to a new merchant?', 'Plain words.', 'explaining the platform'],
];

const FEEDBACK = {
  score: 3,
  strengths: ['You named a specific tool.'],
  improvements: ['Say what the outcome was.', 'Give a number.'],
  missingPoints: ['No concrete anchor found.'],
  revisedOpening: 'At my last internship, I...',
};

function reply(system, user, state) {
  if (system.includes('interview coach')) {
    const k = state.prepCalls++ % 3;
    return ['interview-prep', JSON.stringify(PREP.slice(k * 4, k * 4 + 4).map(([category, question, tip, topic]) => ({ category, question, tip, topic })))];
  }
  if (user.includes('Every question needs a "topic"')) {
    const count = Number((user.match(/Write (\d+) interview/) || [])[1] || 5);
    const questions = [];
    for (let i = 0; i < count; i++) {
      const n = state.practiceSeq++;
      const topic = `${SUBJECTS[n % SUBJECTS.length]} ${Math.floor(n / SUBJECTS.length) || ''}`.trim();
      questions.push({ prompt: `[stub ${n}] How would you approach ${topic} in a production service?`, topic, modelHint: ['a concrete example', 'the trade-off'] });
    }
    return ['practice-generate', JSON.stringify({ questions })];
  }
  if (system.includes('scoring a practice answer')) return ['answer-feedback', JSON.stringify(FEEDBACK)];
  if (system.includes('job description parser')) return ['job-analyzer', JSON.stringify({ companyName: 'Stubco', roleTitle: 'Stub Intern', location: 'Toronto, ON', salary: '$30/hr', skills: ['Go', 'Postgres'], deadline: null, interviewDate: null })];
  if (system.includes('resume-to-job-description matcher')) return ['resume-matcher', JSON.stringify({ score: 64, matchingSkills: ['Go'], missingSkills: ['Kafka'], strengths: ['Backend work'], summary: 'Stub match.' })];
  if (system.includes('cover letter writer')) return ['cover-letter-generate', 'Dear hiring team,\n\nStub letter.\n\nThanks,\nQA'];
  if (system.includes('cover letter editor')) return ['cover-letter-improve', 'Dear hiring team,\n\nStub letter, improved.\n\nThanks,\nQA'];
  if (system.includes('compensation research')) return ['salary-insight', JSON.stringify({ range: '$28–$34/hr', note: 'Stub estimate.' })];
  // Anything unrecognised gets a feedback-shaped object: every parser in the app tolerates a wrong shape.
  return ['unrecognised', JSON.stringify(FEEDBACK)];
}

export async function startStub({ port = 0, log = false } = {}) {
  const state = { prepCalls: 0, practiceSeq: 0, failures: [] };
  const calls = [];

  const server = http.createServer((req, res) => {
    let raw = '';
    req.on('data', (c) => { raw += c; });
    req.on('end', () => {
      let system = '', user = '';
      try {
        const body = JSON.parse(raw || '{}');
        const messages = body.messages || [];
        system = messages.find((m) => m.role === 'system')?.content || '';
        user = messages.filter((m) => m.role === 'user').at(-1)?.content || '';
      } catch { /* a malformed request still gets an answer */ }

      const [kind, content] = reply(system, user, state);
      const failure = state.failures.shift();
      calls.push({ kind, failed: Boolean(failure), at: Date.now() });
      if (log) console.log(`stub ${failure ? `FAIL ${failure}` : 'ok  '} ${kind}`);

      if (failure) {
        res.writeHead(failure, { 'Content-Type': 'application/json' });
        res.end(JSON.stringify({ error: { message: 'stub failure', type: 'server_error' } }));
        return;
      }
      const out = JSON.stringify({ choices: [{ message: { content } }], usage: { total_tokens: 1 } });
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(out);
    });
  });

  await new Promise((resolve) => server.listen(port, '127.0.0.1', resolve));
  const actual = server.address().port;

  return {
    url: `http://127.0.0.1:${actual}`,
    calls,
    /** Calls of one kind, e.g. count('answer-feedback'). */
    count: (kind) => calls.filter((c) => !kind || c.kind === kind).length,
    /** The next n requests answer with this HTTP status instead of a completion. */
    failNext: (n = 1, status = 500) => { for (let i = 0; i < n; i++) state.failures.push(status); },
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}

// realpath: /tmp on macOS is a symlink to /private/tmp, and Node reports the main module's resolved path, so
// comparing against the path as typed silently skipped this block (the stub started nothing, 2026-09-26).
if (process.argv[1] && import.meta.url === pathToFileURL(fs.realpathSync(process.argv[1])).href) {
  const port = Number(process.argv[2] || 5999);
  const stub = await startStub({ port, log: true });
  console.log(`OpenAI stub listening on ${stub.url} — run the app with OpenAI__ApiKey=sk-stub OpenAI__BaseUrl=${stub.url}`);
}
