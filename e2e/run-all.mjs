// Entry point for the dd-web-full-test audit.
//   NODE_PATH=$HOME/.claude/skills/gstack/node_modules \
//   AXE_PATH=<path to axe.min.js> \
//   node e2e/run-all.mjs [functional visual a11y security compat perf applications-clickthrough]
import { flush, summary, ARTIFACTS } from './lib/harness.mjs';
import path from 'node:path';

const ALL = ['functional', 'visual', 'a11y', 'security', 'compat', 'perf'];
// In the default set on purpose. CLAUDE.md requires a click-through of every action on the
// Applications views whenever they change, and a suite you have to remember to ask for is one that
// gets forgotten; 25 checks and ~45 s is a cheap way to make that rule self-enforcing. It stays a
// separate dimension so it can still be run on its own while working on those views.
ALL.push('applications-clickthrough');
const wanted = process.argv.slice(2).filter((a) => ALL.includes(a));
const dims = wanted.length ? wanted : ALL;

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
