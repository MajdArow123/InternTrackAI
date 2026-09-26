// Self-test dimension: one failing check, named so a known-failures entry can match it.
import { record } from '../lib/harness.mjs';
export async function run() { record('selftest-fail', 'A check that fails', 'FAIL', 'deliberately'); }
