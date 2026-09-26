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
