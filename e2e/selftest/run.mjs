// The failure-path test for the e2e runner itself. run-all.mjs is what the merge gate trusts, and it failed
// once in exactly the way the checks it runs can fail: a dimension that aborted before recording anything
// was reported as "0 new failures", and a run that finished its summary then hung for 19 hours. This
// drives run-all against deliberately broken dimensions (e2e/selftest/*.spec.mjs, only reachable with
// E2E_SELFTEST=1) and checks the exit code, the gate report and how long it took.
//
//   node e2e/selftest/run.mjs        (no browser, no server; ~15 s)
//
// Run it after any change to run-all.mjs or to the known-failures handling.
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const ROOT = process.cwd();
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'e2e-selftest-'));
const known = (entries) => { const f = path.join(tmp, `known-${Math.random().toString(36).slice(2)}.json`); fs.writeFileSync(f, JSON.stringify({ entries })); return f; };
const today = new Date().toISOString().slice(0, 10);
const old = new Date(Date.now() - 45 * 86400000).toISOString().slice(0, 10);

function runAll(dims, env = {}) {
  const t0 = Date.now();
  const r = spawnSync('node', ['e2e/run-all.mjs', ...dims], {
    cwd: ROOT, encoding: 'utf8', timeout: 120_000,
    env: { ...process.env, E2E_SELFTEST: '1', E2E_KNOWN_FAILURES: known([]), ARTIFACTS_DIR: tmp, ...env },
  });
  return { code: r.status, out: (r.stdout || '') + (r.stderr || ''), secs: (Date.now() - t0) / 1000, timedOut: r.error?.code === 'ETIMEDOUT' };
}

const scenarios = [
  ['a passing dimension exits 0 and reports no new failures', () => {
    const r = runAll(['selftest-pass']);
    return [r.code === 0, /0 new failure\(s\)/.test(r.out), r];
  }],
  ['a failing check exits 1 and is reported as NEW', () => {
    const r = runAll(['selftest-fail']);
    return [r.code === 1, /1 new failure\(s\)/.test(r.out) && /NEW\s+selftest-fail :: A check that fails/.test(r.out), r];
  }],
  ['an aborted dimension is a failure, not "0 new failures"', () => {
    const r = runAll(['selftest-abort']);
    return [r.code === 1, /1 new failure\(s\)/.test(r.out) && /Dimension ran to completion/.test(r.out), r];
  }],
  ['a dimension that leaves a handle open does not keep the run alive', () => {
    const r = runAll(['selftest-leak']);
    return [r.code === 0 && !r.timedOut, r.secs < 20, r];
  }],
  ['a hung run is stopped by the watchdog, reported, and exits 1', () => {
    const r = runAll(['selftest-pass', 'selftest-hang'], { E2E_RUN_TIMEOUT_MIN: '0.1' });   // 6 s
    return [r.code === 1 && !r.timedOut, /Run finished within 0\.1 minutes/.test(r.out) && /1 new failure\(s\)/.test(r.out) && r.secs < 30, r];
  }],
  ['a listed failure does not block, and is counted as known', () => {
    const r = runAll(['selftest-fail'], { E2E_KNOWN_FAILURES: known([{ dimension: 'selftest-fail', name: 'A check that fails', reason: 'test', owner: 'selftest', added: today }]) });
    return [r.code === 0, /0 new failure\(s\), 1 known failure\(s\)/.test(r.out), r];
  }],
  ['an entry older than 30 days is called out as STALE', () => {
    const r = runAll(['selftest-fail'], { E2E_KNOWN_FAILURES: known([{ dimension: 'selftest-fail', name: 'A check that fails', reason: 'test', owner: 'selftest', added: old }]) });
    return [r.code === 0, /STALE 45 days: selftest-fail :: A check that fails/.test(r.out), r];
  }],
  ['a listed failure that now passes is reported for deletion', () => {
    const r = runAll(['selftest-pass'], { E2E_KNOWN_FAILURES: known([{ dimension: 'selftest-pass', name: 'A check that passes', reason: 'test', owner: 'selftest', added: today }]) });
    return [r.code === 0, /NOW PASSING: selftest-pass :: A check that passes — delete its entry/.test(r.out), r];
  }],
  ['an entry without a reason, owner or date fails the run', () => {
    const r = runAll(['selftest-pass'], { E2E_KNOWN_FAILURES: known([{ dimension: 'selftest-pass', name: 'x' }]) });
    return [r.code === 1, /INVALID ENTRY/.test(r.out), r];
  }],
  ['selftest dimensions cannot be run without E2E_SELFTEST=1', () => {
    const r = runAll(['selftest-abort'], { E2E_SELFTEST: '0' });
    return [r.code === 2, /Unknown dimension/.test(r.out), r];
  }],
];

let failed = 0;
for (const [name, fn] of scenarios) {
  const [exitOk, reportOk, r] = fn();
  const ok = exitOk && reportOk;
  if (!ok) failed++;
  console.log(`${ok ? ' ok ' : 'FAIL'}  ${name}  (exit ${r.code}, ${r.secs.toFixed(1)} s)`);
  if (!ok) console.log(r.out.split('\n').slice(-14).map((l) => '        ' + l).join('\n'));
}
fs.rmSync(tmp, { recursive: true, force: true });
console.log(`\n${scenarios.length - failed}/${scenarios.length} runner failure-path checks passed`);
process.exit(failed ? 1 : 0);
