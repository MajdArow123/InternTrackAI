// Self-test dimension: one passing check.
import { record } from '../lib/harness.mjs';
export async function run() { record('selftest-pass', 'A check that passes', 'PASS', 'ok'); }
