// The guided tour as a visitor meets it, at the two widths that found every tour defect so far (1280 and
// 375). None of this has any other automated coverage: TourTests checks markup, not layout, and every
// finding of the tour audit — a card over its own target, a spotlight on a 16 px speck, a phone menu left
// open behind the overlay, a step that silently vanished — was a layout or timing fact.
//
// Runs on its own throwaway server (lib/app-server.mjs) because it needs the seeded demo account.
// Measurements come from lib/tour-measure.mjs, the probe the Phase A/B work was measured with.
import { chromium, check, assert, assertEqual, newSignedInContext, isCspReportNoise } from './lib/harness.mjs';
import { startApp } from './lib/app-server.mjs';
import { settle, walk, tourButton, describe } from './lib/tour-measure.mjs';

const D = 'Tour';
const SETTLE_MS = 1500;
const WIDTHS = [{ width: 1280, height: 800 }, { width: 375, height: 812 }];
// The overview also runs on a short phone (still 375 wide). Scroll order only matters once a target and the
// docked card compete for height, and at 375x812 today's targets are short enough that centring them in the
// whole screen happens to clear the card — putting the pre-Phase-B scroll-first code back leaves every step
// there at 0%. At 375x667 it covers the practice step (2%, every run). Without this height the fix that
// matters most on phones has nothing guarding it.
const OVERVIEW_VIEWPORTS = [...WIDTHS, { width: 375, height: 667 }];
const OVERVIEW = ['Start with your resume', 'Getting around', 'Your inbox, read for you', 'What needs you today', 'Practice for the interview you have'];
const NAV_STEP = 'Getting around';

/** Every layout rule a step must meet, as one message per breach (empty when the step is fine). */
function breaches({ step, settledMs }, where) {
  if (!step || step.closed) return [`${where}: the tour closed instead of showing a step`];
  const out = [];
  if (step.covered > 0) out.push(`${where} "${step.title}": the card covers ${step.covered}% of its own target`);
  if (!step.inUsable) out.push(`${where} "${step.title}": target outside the usable area ${JSON.stringify(step.spot)}`);
  if (step.underNav && step.title !== NAV_STEP) out.push(`${where} "${step.title}": target is under the sticky nav ${JSON.stringify(step.spot)}`);
  if (!step.tipOnScreen) out.push(`${where} "${step.title}": the card is off screen ${JSON.stringify(step.tip)}`);
  if (settledMs === null || settledMs > SETTLE_MS) out.push(`${where} "${step.title}": took ${settledMs ?? 'forever'} ms to settle (limit ${SETTLE_MS})`);
  return out;
}

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });
  const app = await startApp(browser, { name: 'tour' });
  const B = app.base;

  const errors = [];
  const watch = (page) => {
    page.on('console', (m) => { if (m.type() === 'error' && !isCspReportNoise(m.text())) errors.push(`${page.url().replace(B, '')}: ${m.text().slice(0, 160)}`); });
    page.on('pageerror', (e) => errors.push(`${page.url().replace(B, '')}: pageerror ${e.message.slice(0, 160)}`));
  };

  /** A fresh browser signed into the seeded demo, with the tour's memory cleared. */
  const demo = async (viewport = WIDTHS[0]) => {
    const ctx = await browser.newContext({ viewport });
    const page = await ctx.newPage();
    watch(page);
    await page.goto(`${B}/`);
    await page.click('text=Try the live demo');
    await page.waitForURL('**/Home/Dashboard');
    return { ctx, page };
  };

  try {
    // ---------------------------------------------------------------- nothing auto-runs
    await check(D, 'Nothing auto-runs on a first dashboard visit; the invitation shows instead', async () => {
      const { ctx, page } = await demo();
      await page.waitForTimeout(1500);
      const s = await page.evaluate(() => ({
        tip: Boolean(document.querySelector('.tour-tip') && document.querySelector('.tour-tip').getBoundingClientRect().height),
        prompt: !document.querySelector('[data-tour-prompt]')?.hidden,
      }));
      await ctx.close();
      assert(!s.tip, 'a tour card is showing without anyone asking for it');
      assert(s.prompt, 'the dashboard invitation is not visible');
      return 'no card after 1.5 s; invitation visible';
    });

    await check(D, 'Dismissing the invitation is remembered across a reload', async () => {
      const { ctx, page } = await demo();
      await page.click('[data-tour-dismiss]');
      await page.reload();
      await page.waitForTimeout(500);
      const hidden = await page.evaluate(() => document.querySelector('[data-tour-prompt]')?.hidden !== false);
      await ctx.close();
      assert(hidden, 'the invitation came back after being dismissed');
      return 'stays hidden';
    });

    // ---------------------------------------------------------------- the overview, both widths
    for (const vp of OVERVIEW_VIEWPORTS) {
      await check(D, `Overview on the demo at ${vp.width}x${vp.height}: five steps in order, every one clear of its card`, async () => {
        const { ctx, page } = await demo(vp);
        const steps = await walk(page, () => page.click('[data-tour-prompt] [data-tour-start]'));
        await ctx.close();
        const titles = steps.map((s) => s.step?.title);
        assertEqual(JSON.stringify(titles), JSON.stringify(OVERVIEW), 'step titles and order');
        steps.forEach((s, i) => assertEqual(s.step.count, `${i + 1} of 5`, `count on step ${i + 1}`));
        assertEqual(steps.at(-1).step.path, '/Practice', 'the overview ends on the practice page');
        const bad = steps.flatMap((s) => breaches(s, `${vp.width}x${vp.height}`));
        assert(bad.length === 0, bad.join('\n'));
        return steps.map(describe).join('\n');
      });
    }

    await check(D, 'Phone: the nav step spotlights the menu button, not a hidden link', async () => {
      const { ctx, page } = await demo(WIDTHS[1]);
      const steps = await walk(page, () => page.click('[data-tour-prompt] [data-tour-start]'), { maxSteps: 2 });
      const nav = steps[1];
      const onToggle = await page.evaluate(() => {
        const s = document.querySelector('.tour-spotlight').getBoundingClientRect();
        const t = document.querySelector('[data-tour="nav-toggle"]').getBoundingClientRect();
        return Math.abs(s.left + s.width / 2 - (t.left + t.width / 2)) < 12 && Math.abs(s.top + s.height / 2 - (t.top + t.height / 2)) < 12;
      });
      await ctx.close();
      assertEqual(nav.step.title, NAV_STEP, 'second step');
      assert(nav.step.spot.height > 16, `spotlight is a ${nav.step.spot.height}px speck`);
      assert(onToggle, 'the spotlight is not centred on the menu button');
      return describe(nav);
    });

    await check(D, 'Phone: starting the tour from the open menu closes the menu first', async () => {
      // A page tour, not the overview: the overview's first step loads another page, and a page load
      // closes the menu whatever the tour did — which let this check pass with the fix removed.
      const { ctx, page } = await demo(WIDTHS[1]);
      await page.goto(`${B}/Practice`);
      const r = await settle(page, await tourButton(page));
      const menuOpen = await page.evaluate(() => Boolean(document.querySelector('.navbar-collapse.show, .navbar-collapse.collapsing')));
      await ctx.close();
      assert(!menuOpen, 'the phone menu is still open behind the tour');
      const bad = breaches(r, '375px');
      assert(bad.length === 0, bad.join('\n'));
      return describe(r);
    });

    // ---------------------------------------------------------------- a brand-new account
    await check(D, 'A new account\'s overview counts only reachable steps and lands every fallback', async () => {
      const { context } = await newSignedInContext(browser, 'tournew', { base: B, viewport: WIDTHS[0] });
      const page = await context.newPage();
      watch(page);
      await page.goto(`${B}/Home/Dashboard`);
      const steps = await walk(page, () => page.click('[data-tour-start]'));
      await context.close();
      assert(steps.length === 4, `expected 4 reachable steps (no inbox on a new account), walked ${steps.length}:\n${steps.map(describe).join('\n')}`);
      steps.forEach((s, i) => assertEqual(s.step.count, `${i + 1} of 4`, `count on step ${i + 1}`));
      const bad = steps.flatMap((s) => breaches(s, 'new account'));
      assert(bad.length === 0, bad.join('\n'));
      return steps.map(describe).join('\n');
    });

    // ---------------------------------------------------------------- page tours, both widths
    const PAGE_TOURS = [
      { path: '/JobApplications', steps: 3 },
      { path: '/Practice', steps: 4 },
      { path: '/Profile', steps: 3 },
    ];
    for (const vp of WIDTHS) {
      for (const t of PAGE_TOURS) {
        await check(D, `Page tour ${t.path} at ${vp.width}px: every step clear of its card and in view`, async () => {
          const { ctx, page } = await demo(vp);
          await page.goto(B + t.path);
          const steps = await walk(page, await tourButton(page));
          const path = page.url().replace(B, '').split('?')[0];
          await ctx.close();
          assertEqual(steps.length, t.steps, 'steps walked');
          assertEqual(path, t.path, 'a page tour never leaves its page');
          const bad = steps.flatMap((s) => breaches(s, `${vp.width}px`));
          assert(bad.length === 0, bad.join('\n'));
          return steps.map(describe).join('\n');
        });
      }
    }

    // ---------------------------------------------------------------- the offer card
    await check(D, 'An unclaimed page offers the app tour instead of navigating away', async () => {
      const { ctx, page } = await demo();
      await page.goto(`${B}/CoverLetter/Generate`);
      await page.click('#tour-btn');
      await page.waitForTimeout(300);
      const offer = await page.evaluate(() => ({ title: document.querySelector('#tour-tip-title')?.textContent, path: location.pathname }));
      assertEqual(offer.title, 'No tour for this page', 'offer card title');
      assertEqual(offer.path, '/CoverLetter/Generate', 'still on the page');
      await page.click('text=Not now');
      await page.waitForTimeout(300);
      const closed = await page.evaluate(() => !document.querySelector('.tour-tip')?.getBoundingClientRect().height);
      assert(closed, '"Not now" did not close the card');

      await page.click('#tour-btn');
      await page.waitForTimeout(300);
      const r = await settle(page, () => page.click('text=Take the app tour'), { timeoutMs: 10000 });
      await ctx.close();
      assertEqual(r.step?.title, OVERVIEW[0], 'taking the offer starts the overview at its first step');
      return describe(r);
    });

    // ---------------------------------------------------------------- the unsaved-input guard
    await check(D, 'Unsaved typing in the Create form is asked about; Cancel keeps the page and the text', async () => {
      const { ctx, page } = await demo();
      await page.goto(`${B}/JobApplications/Create`);
      await page.fill('#CompanyName', 'Typed But Not Saved Inc');
      await page.click('#tour-btn');
      await page.waitForTimeout(300);
      await page.click('text=Take the app tour');
      await page.waitForTimeout(400);
      const asked = await page.evaluate(() => document.getElementById('app-confirm')?.classList.contains('active'));
      await page.click('#app-confirm-cancel');
      await page.waitForTimeout(400);
      const after = await page.evaluate(() => ({ path: location.pathname, company: document.querySelector('#CompanyName')?.value }));
      await ctx.close();
      assert(asked, 'no confirm dialog before navigating away from typed input');
      assertEqual(after.path, '/JobApplications/Create', 'Cancel stays on the page');
      assertEqual(after.company, 'Typed But Not Saved Inc', 'Cancel keeps the typed text');
      return 'asked, stayed, kept the text';
    });

    await check(D, 'The analyzer paste box counts as unsaved even though it sits outside the form', async () => {
      const { ctx, page } = await demo();
      await page.goto(`${B}/JobApplications/Create`);
      if (!(await page.isVisible('#analyzerTextarea'))) await page.click('#analyzerToggleBtn');
      await page.fill('#analyzerTextarea', 'A pasted job posting that has not been analyzed yet.');
      await page.click('#tour-btn');
      await page.waitForTimeout(300);
      await page.click('text=Take the app tour');
      await page.waitForTimeout(400);
      const asked = await page.evaluate(() => document.getElementById('app-confirm')?.classList.contains('active'));
      await ctx.close();
      assert(asked, 'the analyzer box was left behind without asking');
      return 'asked';
    });

    await check(D, 'A practice answer (which autosaves) does not trigger the guard', async () => {
      // The overlay makes the page inert while a card is up, so the realistic case is an answer typed
      // before the tour: it autosaves, and after the reload that resumes the tour it is restored into the
      // box — a value that differs from what the page loaded with, which is exactly what a naive guard
      // would flag. Back from the overview's last step (on /Practice) navigates to the dashboard, and must
      // do so without a dialog.
      const { context } = await newSignedInContext(browser, 'tourguard', { base: B, viewport: WIDTHS[0] });
      const page = await context.newPage();
      watch(page);
      await page.goto(`${B}/Practice`);
      await page.click('#practiceGenerateBtn');
      await page.waitForSelector('#practiceList .practice-card[data-question-id] textarea[name="answer"]', { timeout: 15000 });
      const box = '#practiceList .practice-card[data-question-id] textarea[name="answer"]';
      await page.fill(box, 'A practice answer typed but not submitted, long enough to count.');
      await page.waitForTimeout(300);
      await page.evaluate(() => sessionStorage.setItem('itai.tour.state', JSON.stringify({ id: 'overview', index: 4, nav: 4, tried: ['/practice'], ctx: { pendingDraft: false } })));
      await page.reload();
      await page.waitForSelector('.tour-tip', { state: 'visible', timeout: 5000 });
      const restored = await page.inputValue(box);
      const r = await settle(page, () => page.click('.tour-back'), { timeoutMs: 6000 });
      const asked = await page.evaluate(() => document.getElementById('app-confirm')?.classList.contains('active'));
      await context.close();
      assert(restored.startsWith('A practice answer typed'), 'the draft was not restored into the box, so this check tests nothing');
      assert(!asked, 'the guard asked about an answer box that autosaves');
      assertEqual(r.step?.path, '/Home/Dashboard', 'Back went to the previous step\'s page');
      return describe(r);
    });

    // ---------------------------------------------------------------- keyboard
    await check(D, 'Keyboard: Tab stays inside the card, Escape ends the tour and returns focus', async () => {
      const { ctx, page } = await demo();
      await page.goto(`${B}/Practice`);
      await page.focus('#tour-btn');
      await settle(page, () => page.keyboard.press('Enter'));
      const inside = [];
      for (let i = 0; i < 6; i++) {
        await page.keyboard.press('Tab');
        inside.push(await page.evaluate(() => Boolean(document.activeElement?.closest('.tour-tip'))));
      }
      await page.keyboard.press('Escape');
      await page.waitForTimeout(300);
      const end = await page.evaluate(() => ({
        open: Boolean(document.querySelector('.tour-tip')?.getBoundingClientRect().height),
        focus: document.activeElement?.id,
      }));
      await ctx.close();
      assert(inside.every(Boolean), `focus left the card on Tab ${inside.indexOf(false) + 1}`);
      assert(!end.open, 'Escape did not end the tour');
      assertEqual(end.focus, 'tour-btn', 'focus returns to the Tour button');
      return '6 Tabs inside, Escape closed, focus restored';
    });

    await check(D, 'No console errors or uncaught exceptions anywhere in the tour run', async () => {
      assert(errors.length === 0, errors.slice(0, 10).join('\n'));
      return 'none';
    });
  } finally {
    await app.stop();
    await browser.close();
  }
}
