// Self-test dimension: passes, then leaves an open handle behind, as a spec that skips its cleanup does.
import { record } from '../lib/harness.mjs';
export async function run() { record('selftest-leak', 'A check that passes', 'PASS', 'ok'); setInterval(() => {}, 1000); }
