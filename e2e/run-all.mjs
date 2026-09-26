// Entry point for the dd-web-full-test audit.
//   NODE_PATH=$HOME/.claude/skills/gstack/node_modules \
//   AXE_PATH=<path to axe.min.js> \
//   node e2e/run-all.mjs [functional visual a11y security compat perf applications-clickthrough practice tour]
// No arguments runs all nine; an unrecognised name is an error rather than a silent full run.
import { flush, summary, record, ARTIFACTS } from './lib/harness.mjs';
import path from 'node:path';
import fs from 'node:fs';
import { execSync } from 'node:child_process';

const ALL = ['functional', 'visual', 'a11y', 'security', 'compat', 'perf'];
// In the default set on purpose. CLAUDE.md requires a click-through of every action on the
// Applications views whenever they change, and a suite you have to remember to ask for is one that
// gets forgotten; 25 checks and ~45 s is a cheap way to make that rule self-enforcing. It stays a
// separate dimension so it can still be run on its own while working on those views.
ALL.push('applications-clickthrough');
// Practice and tour run on a throwaway server of their own (lib/app-server.mjs): practice needs a model,
// which the in-process stub provides, and tour needs the seeded demo account. Both are in the default set
// for the same reason as the click-through — three bug classes in one week were invisible to everything
// else: two stale progress cards, an autosave ReferenceError that a catch swallowed, and every finding of
// the tour audit.
ALL.push('practice', 'tour');
// An unrecognised name is a usage error, not a reason to run everything. Filtering the arguments
// against ALL and falling back on an empty result meant a typo ("complat") silently ran the whole
// suite instead of the one dimension that was asked for — slow, and easy to mistake for a clean run
// of the thing you meant. Exit 2 is the usage-error code; the spec imports below never happen.
const args = process.argv.slice(2);
// The runner's own failure-path test (e2e/selftest/run.mjs) drives deliberately broken dimensions that
// must never be part of a real run, so they exist only when E2E_SELFTEST=1.
const SELFTEST = process.env.E2E_SELFTEST === '1';
const isSelftest = (a) => SELFTEST && /^selftest-[a-z]+$/.test(a);
const unknown = args.filter((a) => !ALL.includes(a) && !isSelftest(a));
if (unknown.length) {
  console.error(`Unknown dimension${unknown.length > 1 ? 's' : ''}: ${unknown.join(', ')}`);
  console.error(`Valid names: ${ALL.join(', ')}`);
  console.error('Pass no arguments to run all of them.');
  process.exit(2);
}
const dims = args.length ? args : ALL;

// ---------------------------------------------------------------- watchdog
// A run that stops making progress is ended and reported, not left for someone to find. The 2026-09-25
// full run printed its summary and then stayed alive for 19 hours because nothing was watching. A full run
// takes ~7-8 minutes; 30 is about four times that — generous enough for a slow machine or a cold publish,
// short enough that a hang is reported the same half hour rather than days later. It covers the whole run
// rather than each dimension so one budget holds however many dimensions are added.
const RUN_TIMEOUT_MIN = Number(process.env.E2E_RUN_TIMEOUT_MIN || 30);
let current = null;
setTimeout(() => {
  console.error(`\n!! run exceeded ${RUN_TIMEOUT_MIN} minutes while running "${current}" — stopping it`);
  record(current || 'run', `Run finished within ${RUN_TIMEOUT_MIN} minutes`, 'FAIL',
    `still running "${current}" after ${RUN_TIMEOUT_MIN} minutes; results up to that point are kept`);
  finish();
  process.exit(1);
}, RUN_TIMEOUT_MIN * 60_000).unref();

for (const d of dims) {
  current = d;
  console.log(`\n===== ${d} =====`);
  const t = Date.now();
  try {
    const mod = await import(isSelftest(d) ? `./selftest/${d.slice('selftest-'.length)}.spec.mjs` : `./${d}.spec.mjs`);
    await mod.run();
  } catch (err) {
    console.error(`!! ${d} suite aborted: ${err.message}\n${err.stack?.split('\n').slice(1, 5).join('\n')}`);
    // An abort is a failure. Recorded as one so the summary and the merge gate see it — before this, a
    // dimension that never ran reported "0 new failures" (2026-09-26).
    record(d, 'Dimension ran to completion', 'FAIL', `aborted: ${err.message}`);
  }
  console.log(`----- ${d} done in ${((Date.now() - t) / 1000).toFixed(1)}s`);
}

current = null;
finish();

/** Writes the results, the summary and the merge-gate report. Also called by the watchdog. */
function finish() {
  const out = path.join(ARTIFACTS, `results-${dims.join('-')}.json`);
  const results = flush(out);
  console.log('\n==== SUMMARY ====');
  console.log(JSON.stringify(summary()));
  console.log('results:', out);

  // ---------------------------------------------------------------- merge gate (CLAUDE.md §10)
  // A failure listed in known-failures.json does not block a merge; anything else does. The list is meant to
  // stay empty, so it is read strictly: an entry missing its reason, owner or date is an error, entries older
  // than STALE_DAYS are called out on every run, and a listed failure that now passes is reported so its entry
  // gets deleted rather than carried.
  const STALE_DAYS = 30;
  const knownFile = process.env.E2E_KNOWN_FAILURES || path.join(path.dirname(new URL(import.meta.url).pathname), 'known-failures.json');
  const known = JSON.parse(fs.readFileSync(knownFile, 'utf8')).entries || [];
  const invalid = known.filter((e) => !e.dimension || !e.name || !e.reason || !e.owner || !/^\d{4}-\d{2}-\d{2}$/.test(e.added || ''));
  const isKnown = (r) => known.some((e) => e.dimension === r.dimension && e.name === r.name);
  const failed = results.filter((r) => r.status === 'FAIL');
  const fresh = failed.filter((r) => !isKnown(r));
  console.log(`\n==== MERGE GATE ====`);
  console.log(`${fresh.length} new failure(s), ${failed.length - fresh.length} known failure(s), ${known.length} entr${known.length === 1 ? 'y' : 'ies'} in known-failures.json`);
  for (const r of fresh) console.log(`  NEW   ${r.dimension} :: ${r.name}`);
  for (const e of invalid) console.log(`  INVALID ENTRY (needs dimension, name, reason, owner, added): ${JSON.stringify(e)}`);
  for (const e of known) {
    const age = Math.floor((Date.now() - Date.parse(e.added)) / 86400000);
    if (age > STALE_DAYS) console.log(`  STALE ${age} days: ${e.dimension} :: ${e.name} (owner ${e.owner}) — fix it or say why it is still listed`);
    if (results.some((r) => r.dimension === e.dimension && r.name === e.name && r.status === 'PASS')) {
      console.log(`  NOW PASSING: ${e.dimension} :: ${e.name} — delete its entry`);
    }
  }
  const sha = (() => { try { return execSync('git rev-parse --short HEAD', { encoding: 'utf8' }).trim(); } catch { return 'unknown'; } })();
  const dirty = (() => { try { return execSync('git status --porcelain', { encoding: 'utf8' }).trim().length > 0; } catch { return false; } })();
  const s = summary();
  console.log(`\nFor the PR description:\n  E2E ${dims.length === ALL.length ? 'full run' : `partial run (${dims.join(', ')})`} at ${sha}${dirty ? ' + uncommitted changes' : ''}: ${s.PASS} passed, ${s.FAIL} failed (${fresh.length} new), ${s.SKIP} skipped`);
  if (fresh.length || invalid.length) process.exitCode = 1;
}

// Exit explicitly. A spec that throws before its own cleanup leaves a browser (or a throwaway server) open,
// and that kept this process alive after the summary had printed — once for 19 hours (2026-09-26).
process.exit(process.exitCode ?? 0);
