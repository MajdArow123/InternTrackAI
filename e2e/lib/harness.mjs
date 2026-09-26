// Minimal test harness for the dd-web-full-test audit.
// The repo has no npm project (CLAUDE.md: "vendored in wwwroot/lib, no npm"), so these
// specs run on a Playwright install borrowed via NODE_PATH instead of a dev dependency.
// Run with: node e2e/run-all.mjs   (see that file for the NODE_PATH it expects)
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';

const require = createRequire(import.meta.url);

function loadPlaywright() {
  const candidates = [
    'playwright',
    process.env.PLAYWRIGHT_PATH,
    `${process.env.HOME}/.claude/skills/gstack/node_modules/playwright`,
  ].filter(Boolean);
  for (const c of candidates) {
    try { return require(c); } catch { /* try next */ }
  }
  throw new Error('playwright not resolvable; set PLAYWRIGHT_PATH or NODE_PATH');
}

export const { chromium, firefox, webkit, devices } = loadPlaywright();
export const BASE = process.env.BASE_URL || 'http://localhost:5240';
export const ARTIFACTS = process.env.ARTIFACTS_DIR || path.join(process.cwd(), 'e2e', 'artifacts');

export function axeSource() {
  const candidates = [
    process.env.AXE_PATH,
    path.join(process.cwd(), 'node_modules', 'axe-core', 'axe.min.js'),
  ].filter(Boolean);
  for (const c of candidates) {
    if (fs.existsSync(c)) return fs.readFileSync(c, 'utf8');
  }
  return null;
}

// ---------------------------------------------------------------- results

const results = [];

export function record(dimension, name, status, evidence = '') {
  const row = { dimension, name, status, evidence: String(evidence).slice(0, 4000) };
  results.push(row);
  const mark = status === 'PASS' ? '  ok  ' : status === 'FAIL' ? ' FAIL ' : ' skip ';
  console.log(`[${mark}] ${dimension} :: ${name}${status === 'PASS' ? '' : `\n         ${row.evidence.split('\n').slice(0, 6).join('\n         ')}`}`);
  return row;
}

export async function check(dimension, name, fn) {
  try {
    const evidence = await fn();
    return record(dimension, name, 'PASS', evidence ?? '');
  } catch (err) {
    if (err && err.__skip) return record(dimension, name, 'SKIP', err.message);
    return record(dimension, name, 'FAIL', err && err.stack ? `${err.message}\n${err.stack.split('\n').slice(1, 4).join('\n')}` : String(err));
  }
}

export function skip(message) {
  const e = new Error(message);
  e.__skip = true;
  throw e;
}

export function assert(condition, message) {
  if (!condition) throw new Error(message);
}

export function assertEqual(actual, expected, message) {
  if (actual !== expected) throw new Error(`${message}\n  expected: ${JSON.stringify(expected)}\n  actual:   ${JSON.stringify(actual)}`);
}

export function flush(file) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(results, null, 2));
  return results;
}

export function summary() {
  const s = { PASS: 0, FAIL: 0, SKIP: 0 };
  for (const r of results) s[r.status]++;
  return s;
}

// ---------------------------------------------------------------- app helpers

export function uniqueEmail(tag = 'qa') {
  return `qa.${tag}.${Date.now().toString(36)}.${Math.random().toString(36).slice(2, 7)}@example.test`;
}

export const PASSWORD = 'QaAudit!2026x';

/**
 * A distinct synthetic client address, one per call. Registration is limited per client
 * (RegistrationLimiter), and the app trusts X-Forwarded-For because Railway's proxy is the only
 * thing in front of it (KnownProxies/KnownNetworks are cleared on purpose - see Program.cs). Without
 * this every request in the suite arrives from 127.0.0.1 and shares one bucket, so the accounts
 * these specs need would start being refused for reasons that have nothing to do with the check
 * being run. 198.18.0.0/15 is the reserved benchmarking range, so a header built from it can never
 * collide with a real client.
 */
// Starts at a random point: every process used to begin at 198.18.0.1, so a second run inside the hour reused
// the first run's addresses and hit the 5-per-hour registration limit (2026-09-26). The /15 has 131,072
// addresses; a random start makes a collision between runs vanishingly unlikely.
let clientCounter = Math.floor(Math.random() * 120000);
export function syntheticClient() {
  clientCounter += 1;
  return { 'X-Forwarded-For': `198.${18 + ((clientCounter >> 16) & 1)}.${(clientCounter >> 8) & 0xff}.${clientCounter & 0xff}` };
}

/**
 * Registers a throwaway account through the real Register page (never touches the DB).
 * `base` and `password` default to the suite-wide ones; the throwaway servers in app-server.mjs pass
 * their own.
 */
export async function registerThroughUi(context, email, displayName = 'QA Audit', { base = BASE, password = PASSWORD } = {}) {
  const page = await context.newPage();
  // Each throwaway account is its own visitor, so one spec's registrations never spend another's
  // registration allowance. Set on the page, not the context: everything after registration goes on
  // being plain localhost traffic.
  await page.setExtraHTTPHeaders(syntheticClient());
  await page.goto(`${base}/Identity/Account/Register`, { waitUntil: 'domcontentloaded' });
  await page.fill('#Input_DisplayName', displayName).catch(() => {});
  await page.fill('#Input_Email', email);
  await page.fill('#Input_Password', password);
  await page.fill('#Input_ConfirmPassword', password);
  await Promise.all([
    page.waitForLoadState('networkidle'),
    page.click('button[type=submit]'),
  ]);
  const url = page.url();
  await page.close();
  if (/Register/i.test(url)) throw new Error(`registration did not complete, still at ${url}`);
  return url;
}

export async function newSignedInContext(browser, tag = 'main', { base = BASE, viewport = { width: 1440, height: 900 } } = {}) {
  const context = await browser.newContext({ viewport });
  const email = uniqueEmail(tag);
  await registerThroughUi(context, email, 'QA Audit', { base });
  return { context, email };
}

/**
 * True for the console noise the app's report-only CSP produces. The policy is
 * Content-Security-Policy-Report-Only on purpose (see SecurityHeaders.cs), so these messages mean
 * "this inline block would be blocked if the policy were enforced" - the working list, not a
 * failure. Chromium logs them below error level; Gecko and WebKit log them as errors, plus two
 * WebKit notices about a report-only policy having no frame-ancestors and no report-to. Filtering
 * them is what makes "no console errors" mean the same thing on all three engines.
 */
export function isCspReportNoise(text) {
  const t = String(text);
  return /Content[- ]Security[- ]Policy/i.test(t) || /\[Report Only\]/i.test(t);
}

/** Reads the antiforgery token out of a rendered page so raw fetch() posts are accepted. */
export async function antiforgery(page, url) {
  await page.goto(`${BASE}${url}`, { waitUntil: 'domcontentloaded' });
  return page.evaluate(() => {
    const el = document.querySelector('input[name="__RequestVerificationToken"]');
    return el ? el.value : null;
  });
}

/** POSTs a form from inside the page so the session cookie rides along. */
export async function postForm(page, url, fields, token) {
  return page.evaluate(async ({ url, fields, token }) => {
    const body = new URLSearchParams();
    for (const [k, v] of Object.entries(fields)) body.append(k, v);
    if (token) body.append('__RequestVerificationToken', token);
    const res = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
      body: body.toString(),
    });
    let text = '';
    try { text = await res.text(); } catch { /* opaque */ }
    return { status: res.status, url: res.url, redirected: res.redirected, contentType: res.headers.get('content-type'), body: text.slice(0, 2000) };
  }, { url, fields, token });
}

export async function postJson(page, url, payload, token) {
  return page.evaluate(async ({ url, payload, token }) => {
    const headers = { 'Content-Type': 'application/json' };
    if (token) headers['RequestVerificationToken'] = token;
    const res = await fetch(url, { method: 'POST', headers, body: JSON.stringify(payload) });
    let text = '';
    try { text = await res.text(); } catch { /* opaque */ }
    return { status: res.status, url: res.url, contentType: res.headers.get('content-type'), body: text.slice(0, 3000) };
  }, { url, payload, token });
}

/** Creates an application through the Create form and returns its id. */
export async function createApplication(page, fields, { base = BASE } = {}) {
  await page.goto(`${base}/JobApplications/Create`, { waitUntil: 'domcontentloaded' });
  await page.fill('#CompanyName', fields.CompanyName);
  await page.fill('#RoleTitle', fields.RoleTitle);
  if (fields.Location) await page.fill('#Location', fields.Location);
  if (fields.JobLink) await page.fill('#JobLink', fields.JobLink);
  if (fields.JobDescription) await page.fill('#JobDescription', fields.JobDescription);
  if (fields.Status) await page.selectOption('#Status', fields.Status);
  if (fields.DateApplied) await page.fill('#DateApplied', fields.DateApplied);
  if (fields.Deadline) await page.fill('#Deadline', fields.Deadline);
  if (fields.forceCreate) await page.evaluate(() => { document.querySelector('#forceCreate').value = 'true'; });
  await Promise.all([
    page.waitForLoadState('networkidle'),
    page.click('#appForm button[type=submit]'),
  ]);
  return page.url();
}

export async function listedApplicationIds(page) {
  await page.goto(`${BASE}/JobApplications?view=list`, { waitUntil: 'networkidle' });
  return page.evaluate(() => Array.from(document.querySelectorAll('[data-app-id]')).map((e) => e.getAttribute('data-app-id')));
}

export async function shot(page, name) {
  const file = path.join(ARTIFACTS, 'screenshots', `${name}.png`);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  await page.screenshot({ path: file, fullPage: true, animations: 'disabled' });
  return path.relative(process.cwd(), file);
}

export async function freeze(page) {
  await page.addStyleTag({
    content: `*,*::before,*::after{animation-duration:0s!important;animation-delay:0s!important;transition-duration:0s!important;transition-delay:0s!important;caret-color:transparent!important}`,
  }).catch(() => {});
}

/** Fails loudly if the context has silently lost its auth cookie. */
export async function assertSignedIn(page, where) {
  await page.goto(BASE + '/Home/Dashboard', { waitUntil: 'domcontentloaded' });
  if (/Identity\/Account\/Login/i.test(page.url())) {
    throw new Error(`session lost before "${where}" (dashboard redirected to Login)`);
  }
}
