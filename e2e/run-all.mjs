// Entry point for the dd-web-full-test audit.
//   NODE_PATH=$HOME/.claude/skills/gstack/node_modules \
//   AXE_PATH=<path to axe.min.js> \
//   node e2e/run-all.mjs [functional visual a11y security compat perf applications-clickthrough]
// No arguments runs all seven; an unrecognised name is an error rather than a silent full run.
import { flush, summary, ARTIFACTS } from './lib/harness.mjs';
import path from 'node:path';

const ALL = ['functional', 'visual', 'a11y', 'security', 'compat', 'perf'];
// In the default set on purpose. CLAUDE.md requires a click-through of every action on the
// Applications views whenever they change, and a suite you have to remember to ask for is one that
// gets forgotten; 25 checks and ~45 s is a cheap way to make that rule self-enforcing. It stays a
// separate dimension so it can still be run on its own while working on those views.
ALL.push('applications-clickthrough');
// An unrecognised name is a usage error, not a reason to run everything. Filtering the arguments
// against ALL and falling back on an empty result meant a typo ("complat") silently ran the whole
// suite instead of the one dimension that was asked for — slow, and easy to mistake for a clean run
// of the thing you meant. Exit 2 is the usage-error code; the spec imports below never happen.
const args = process.argv.slice(2);
const unknown = args.filter((a) => !ALL.includes(a));
if (unknown.length) {
  console.error(`Unknown dimension${unknown.length > 1 ? 's' : ''}: ${unknown.join(', ')}`);
  console.error(`Valid names: ${ALL.join(', ')}`);
  console.error('Pass no arguments to run all of them.');
  process.exit(2);
}
const dims = args.length ? args : ALL;

for (const d of dims) {
  console.log(`\n===== ${d} =====`);
  const t = Date.now();
  try {
    const mod = await import(`./${d}.spec.mjs`);
    await mod.run();
  } catch (err) {
    console.error(`!! ${d} suite aborted: ${err.message}\n${err.stack?.split('\n').slice(1, 5).join('\n')}`);
  }
  console.log(`----- ${d} done in ${((Date.now() - t) / 1000).toFixed(1)}s`);
}

const out = path.join(ARTIFACTS, `results-${dims.join('-')}.json`);
flush(out);
console.log('\n==== SUMMARY ====');
console.log(JSON.stringify(summary()));
console.log('results:', out);
