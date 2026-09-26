// Measures the guided tour the way a person sees it: for the step on screen, how much of the spotlit
// target the card covers, whether the target sits in the usable part of the viewport (below the sticky
// nav, above a docked phone card), whether the card itself is on screen, and how long the step took to
// stop moving. This is the probe the Phase A/B tour work was measured with, committed so it is not
// rebuilt a fourth time.
//
// Used by e2e/tour.spec.mjs. Also a standalone tool for measuring a layout change by hand:
//   NODE_PATH=$HOME/.claude/skills/gstack/node_modules node e2e/lib/tour-measure.mjs 375x812 [tour] [path]
// which starts its own throwaway server (app-server.mjs), signs into the seeded demo, starts `tour`
// (default: overview, from the dashboard invitation) on `path`, and prints one line per step.
//
// Run it headless and unthrottled. The desktop browser pane and a covered browser window both throttle
// animation frames to a few per second, which made ~0.1-0.9 s settles look like several seconds.
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

/** The step on screen right now, or { closed: true } when no tour card is showing. */
export function snap(page) {
  return page.evaluate(() => {
    const tip = document.querySelector('.tour-tip');
    if (!tip || tip.hidden || !tip.getBoundingClientRect().height) return { closed: true, path: location.pathname };
    const spotEl = document.querySelector('.tour-spotlight');
    const s = !spotEl || spotEl.hidden ? null : spotEl.getBoundingClientRect();
    const t = tip.getBoundingClientRect();
    const navH = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--site-nav-h'), 10) || 53;
    const docked = tip.classList.contains('tour-tip--docked');
    const usableBottom = docked ? t.top : innerHeight;
    let covered = 0;
    if (s) {
      const ox = Math.max(0, Math.min(s.right, t.right) - Math.max(s.left, t.left));
      const oy = Math.max(0, Math.min(s.bottom, t.bottom) - Math.max(s.top, t.top));
      covered = Math.round((100 * ox * oy) / Math.max(1, s.width * s.height));
    }
    return {
      closed: false,
      path: location.pathname,
      count: document.querySelector('#tour-tip-count')?.textContent?.trim() || '',
      title: document.querySelector('#tour-tip-title')?.textContent?.trim() || '',
      spot: s && { top: Math.round(s.top), bottom: Math.round(s.bottom), height: Math.round(s.height) },
      tip: { top: Math.round(t.top), bottom: Math.round(t.bottom) },
      covered,
      docked,
      pinned: tip.classList.contains('tour-tip--pinned'),
      // A 16 px speck is what a display:none target used to produce; that is not "in view".
      inUsable: !s || (s.height > 16 && s.top >= -12 && s.bottom <= usableBottom + 12),
      // Separate from inUsable because the nav step spotlights the nav itself; every other step's target
      // tucked under the sticky nav is a defect.
      underNav: Boolean(s) && s.top < navH - 12,
      tipOnScreen: t.top >= 0 && t.bottom <= innerHeight + 1,
    };
  }).catch(() => null);
}

/**
 * Runs `action`, then polls until the tour card and spotlight stop moving (four identical samples in a
 * row) or `timeoutMs` passes. Returns { step, settledMs }, where settledMs is **when the last movement was
 * seen**, measured from the action — not when polling noticed it had stopped. The earlier "now minus four
 * sample intervals" differs from this by only the evaluate cost of those samples: measured on identical
 * samples (2026-09-26) that is 5-20 ms, because each evaluate takes 1-3 ms. It was kept as the definition
 * because it is the right one, not because it moved the numbers. The 1.63 s once read for Profile at 375 px
 * (a step that stopped moving at ~1.17 s) was the phone menu being opened *inside* the timed action — see
 * revealTourButton — not this. A tour that has not appeared yet is waited for — one started from the phone
 * menu starts only once the menu has closed.
 */
export async function settle(page, action, { timeoutMs = 8000, lateStartMs = 2000 } = {}) {
  const t0 = Date.now();
  await action();
  let last = null, stable = 0, step = null, changedAt = null;
  while (Date.now() - t0 < timeoutMs) {
    await new Promise((r) => setTimeout(r, 50));
    const seenAt = Date.now();
    const s = await snap(page);
    if (!s) continue;
    if (s.closed) { if (Date.now() - t0 > lateStartMs) return { step: s, settledMs: null }; continue; }
    const key = JSON.stringify([s.path, s.count, s.spot, s.tip]);
    if (key === last) stable++;
    else { stable = 0; changedAt = seenAt - t0; }
    last = key; step = s;
    if (stable >= 4) return { step, settledMs: changedAt };
  }
  return { step, settledMs: null };
}

/**
 * Opens the phone menu when the nav Tour button is inside it, as a phone user must. Call this *before*
 * timing anything: the menu opening is the user's action, not the tour's, and an earlier version that did
 * it inside the timed action charged the tour ~400 ms for it.
 */
export async function revealTourButton(page) {
  const btn = await page.$('#tour-btn');
  if (btn && !(await btn.isVisible()) && (await page.$('[data-tour="nav-toggle"]'))) {
    await tap(page, '[data-tour="nav-toggle"]');
    await page.waitForFunction(() => document.querySelector('.navbar-collapse.show') && !document.querySelector('.navbar-collapse.collapsing'));
  }
}

/**
 * Opens the menu if needed, then returns the timed action: tapping Tour where it is on screen. Not
 * page.click, here or for the menu button: Playwright scrolls its target into view first, and for a button
 * inside the sticky header that means scrolling the page back to where the header sits — the top. Every page-tour walk therefore
 * started at the top whatever the page had been scrolled to, and a check that "started scrolled away"
 * never had (found 2026-09-26, when the order check recorded no scrolls on a walk it had scrolled away).
 */
export async function tourButton(page) {
  await revealTourButton(page);
  return () => tap(page, '#tour-btn');
}

/** Clicks where the element is on screen, without Playwright's scroll-into-view (see tourButton). */
async function tap(page, selector) {
  const box = await page.locator(selector).boundingBox();
  if (!box) throw new Error(`${selector} is not on screen`);
  await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
}

/**
 * Records every scroll the tour itself makes, with which step the card was showing (`data-step`, written first
 * thing in tour.js's render) and which step it had last been laid out for (`data-placed-for`, written by
 * position). Position-then-scroll is right exactly when the two agree at every scroll. This checks the order
 * directly, at any viewport, where a coverage check can only infer it, and only on a screen where the wrong
 * order happens to cover something (Phase B's "0% at 375x812" was never evidence the order was right).
 *
 * Every scroll API is wrapped, not only the window.scrollTo tour.js uses today, so a regression to
 * scrollIntoView is recorded too. A call counts as the tour's when tour.js is on its stack. Call it before the
 * context's pages load anything; the returned array fills across navigations.
 */
export async function recordTourScrolls(context) {
  const scrolls = [];
  await context.exposeBinding('__reportTourScroll', (_src, entry) => { scrolls.push(entry); });
  await context.addInitScript(() => {
    const record = (api) => {
      if (!/\/js\/tour\.js/.test(new Error().stack || '')) return;
      const tip = document.querySelector('.tour-tip');
      const entry = { api, path: location.pathname, step: tip?.getAttribute('data-step') ?? null, placedFor: tip?.getAttribute('data-placed-for') ?? null };
      try { window.__reportTourScroll(entry); } catch { /* binding gone mid-navigation */ }
    };
    for (const [target, name] of [[window, 'scrollTo'], [window, 'scrollBy'], [window, 'scroll'],
      [Element.prototype, 'scrollIntoView'], [Element.prototype, 'scrollTo'], [Element.prototype, 'scrollBy'], [Element.prototype, 'scroll']]) {
      const original = target[name];
      if (typeof original !== 'function') continue;
      target[name] = function (...args) { record(name); return original.apply(this, args); };
    }
  });
  return scrolls;
}

/** Every recorded tour scroll made before its card was laid out for the step it scrolls to, one message each. */
export function outOfOrder(scrolls, where) {
  return scrolls.filter((s) => s.step !== s.placedFor)
    .map((s) => `${where}: ${s.api} on ${s.path} for step ${s.step} while the card was still placed for ${s.placedFor ?? 'nothing'}`);
}

/**
 * Scrolls the page just far enough that `selector` is out of view (its bottom a little above the top edge, or
 * the page back to the top when the target is lower down), so a walk's first step has to scroll. Just far
 * enough, not to the end of the page: the walk's timing limits are for a step, and a smooth scroll back from
 * the foot of a long page is a different cost. Returns false when the page is too short to hide the target;
 * a walk's own "at least one scroll" rule is what then keeps the order check from being vacuous.
 */
export async function scrollAwayFrom(page, selector) {
  // Wait for the page to stop changing height first. At 375px the Applications list is still settling after
  // load (5226 -> 3179 -> 2844 px measured), and a scroll made before that is undone as the page shrinks,
  // so the walk began with its first target back in view and the order check saw no scroll.
  await page.waitForFunction(() => {
    const h = document.documentElement.scrollHeight;
    const w = (window.__heightWatch ||= { h, since: performance.now() });
    if (w.h !== h) { w.h = h; w.since = performance.now(); }
    return performance.now() - w.since > 400;
  }, null, { polling: 100, timeout: 5000 }).catch(() => {});
  return page.evaluate((sel) => {
    const el = document.querySelector(sel);
    if (!el) return false;
    const r = el.getBoundingClientRect();
    const inView = () => { const b = el.getBoundingClientRect(); return b.bottom > 0 && b.top < innerHeight; };
    // Instant: Bootstrap's reboot makes :root scroll smoothly, so a plain scrollTo has not moved yet when
    // inView() asks.
    window.scrollTo({ top: inView() ? scrollY + r.bottom + 24 : 0, behavior: 'instant' });
    return !inView();
  }, selector);
}

/**
 * Replaces the Google Fonts stylesheet with an empty one and refuses the font files, so a timing check measures
 * the tour and not a third-party network fetch. Inter falls back to the system font stack; the layout checks
 * were confirmed to pass either way (2026-09-26). In production the stylesheet is render-blocking (CLAUDE.md §12).
 */
export async function withoutGoogleFonts(context) {
  await context.route('https://fonts.googleapis.com/**', (r) => r.fulfill({ status: 200, contentType: 'text/css', body: '' }));
  await context.route('https://fonts.gstatic.com/**', (r) => r.abort());
}

/** Walks the tour from its first step to Done, returning one { step, settledMs } per step. */
export async function walk(page, start, { maxSteps = 8 } = {}) {
  const steps = [];
  let r = await settle(page, start);
  steps.push(r);
  for (let i = 1; i < maxSteps && r.step && !r.step.closed; i++) {
    const label = await page.$eval('.tour-next', (b) => b.textContent.trim()).catch(() => null);
    if (!label || label === 'Done') break;
    r = await settle(page, () => page.click('.tour-next'));
    steps.push(r);
  }
  return steps;
}

export function describe({ step, settledMs }) {
  if (!step || step.closed) return `closed on ${step?.path}`;
  return `${step.count.padEnd(7)} ${step.title.slice(0, 36).padEnd(36)} ${step.path.padEnd(22)} settled=${String(settledMs).padStart(4)}ms covered=${step.covered}%${step.docked ? ' docked' : ''}${step.pinned ? ' pinned' : ''} inUsable=${step.inUsable} underNav=${step.underNav} tipOnScreen=${step.tipOnScreen}`;
}

// ---------------------------------------------------------------- standalone
// realpath: /tmp on macOS is a symlink to /private/tmp, and Node reports the main module's resolved path, so
// comparing against the path as typed silently skipped this block (the stub started nothing, 2026-09-26).
if (process.argv[1] && import.meta.url === pathToFileURL(fs.realpathSync(process.argv[1])).href) {
  const { chromium } = await import('./harness.mjs');
  const { startApp } = await import('./app-server.mjs');
  const [w, h] = (process.argv[2] || '1280x800').split('x').map(Number);
  const tour = process.argv[3] || 'overview';
  const path = process.argv[4] || '/Home/Dashboard';

  const browser = await chromium.launch({ channel: 'chrome' });
  const app = await startApp(browser, { name: 'tour-measure' });
  try {
    const context = await browser.newContext({ viewport: { width: w, height: h } });
    await withoutGoogleFonts(context);   // the same conditions the tour dimension's limits were set in
    const page = await context.newPage();
    await page.goto(`${app.base}/`);
    await page.click('text=Try the live demo');
    await page.waitForURL('**/Home/Dashboard');
    if (path !== '/Home/Dashboard') await page.goto(app.base + path);
    const start = tour === 'overview' && path === '/Home/Dashboard'
      ? () => page.click('[data-tour-prompt] [data-tour-start]')
      : await tourButton(page);
    console.log(`viewport ${w}x${h}, tour ${tour} from ${path}`);
    for (const s of await walk(page, start)) console.log(describe(s));
  } finally {
    await app.stop();
    await browser.close();
  }
}
