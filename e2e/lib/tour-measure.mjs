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
    await page.click('[data-tour="nav-toggle"]');
    await page.waitForFunction(() => document.querySelector('.navbar-collapse.show') && !document.querySelector('.navbar-collapse.collapsing'));
  }
}

/** Opens the menu if needed, then returns the timed action: tapping Tour. */
export async function tourButton(page) {
  await revealTourButton(page);
  return () => page.click('#tour-btn');
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
if (import.meta.url === pathToFileURL(process.argv[1] || '').href) {
  const { chromium } = await import('./harness.mjs');
  const { startApp } = await import('./app-server.mjs');
  const [w, h] = (process.argv[2] || '1280x800').split('x').map(Number);
  const tour = process.argv[3] || 'overview';
  const path = process.argv[4] || '/Home/Dashboard';

  const browser = await chromium.launch({ channel: 'chrome' });
  const app = await startApp(browser, { name: 'tour-measure' });
  try {
    const page = await (await browser.newContext({ viewport: { width: w, height: h } })).newPage();
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
