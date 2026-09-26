// Reads a server log for Error and Fatal lines. Its own module so the runner's self-test
// (e2e/selftest/run.mjs) can check it without loading Playwright.
import fs from 'node:fs';

/** Error and Fatal lines in a server log (Serilog's console template: "[HH:mm:ss ERR] ..."), each with its exception lines. */
export function errorLines(logFile) {
  if (!fs.existsSync(logFile)) return [];
  const lines = fs.readFileSync(logFile, 'utf8').split('\n');
  const out = [];
  for (let i = 0; i < lines.length; i++) {
    if (!/^\[\d\d:\d\d:\d\d (ERR|FTL)\]/.test(lines[i])) continue;
    const block = [lines[i]];
    for (let j = i + 1; j < lines.length && j <= i + 6 && !/^\[\d\d:\d\d:\d\d [A-Z]{3}\]/.test(lines[j]); j++) block.push(lines[j]);
    out.push(block.join('\n'));
  }
  return out;
}

/** The message of an error block: its first line without the "[HH:mm:ss ERR] " prefix. */
export const messageOf = (block) => block.split('\n')[0].replace(/^\[\d\d:\d\d:\d\d [A-Z]{3}\] /, '');

/**
 * Checks error blocks against an allowlist of exact messages, each expected a known number of times.
 * Allowed by message, never by count: a count-based allowance passes when a different error appears and the
 * deliberate one doesn't. And each allowed message must appear exactly as often as expected, so a check that
 * provokes an error on purpose — and the logging behind it — is verified too, not silently skipped.
 * Returns human-readable problems (empty when the log is as expected).
 */
export function unexpectedErrors(blocks, expected = {}) {
  const problems = [];
  const seen = new Map();
  for (const b of blocks) {
    const m = messageOf(b);
    if (Object.hasOwn(expected, m)) seen.set(m, (seen.get(m) || 0) + 1);
    else problems.push(`unexpected: ${b}`);
  }
  for (const [m, n] of Object.entries(expected)) {
    const got = seen.get(m) || 0;
    if (got !== n) problems.push(`expected "${m}" ${n} time(s), logged ${got}`);
  }
  return problems;
}
