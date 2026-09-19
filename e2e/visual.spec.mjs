// Visual dimension. There is no committed screenshot baseline in this repo, so this run
// CAPTURES baselines (e2e/artifacts/screenshots) and asserts on deterministic layout
// properties instead of pixel diffs: horizontal overflow, clipped text, off-screen content,
// dark-mode coverage and sticky-header geometry.
import { chromium, BASE, check, assert, assertEqual, newSignedInContext, createApplication, shot, freeze, ARTIFACTS } from './lib/harness.mjs';
import fs from 'node:fs';
import path from 'node:path';

const D = 'Visual';

const VIEWPORTS = [
  ['mobile', 390, 844],
  ['tablet', 768, 1024],
  ['desktop', 1440, 900],
];

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });
  const { context } = await newSignedInContext(browser, 'visual');
  const page = await context.newPage();

  await createApplication(page, {
    CompanyName: 'Pixel Perfect Inc', RoleTitle: 'Frontend Engineering Intern', Location: 'Vancouver, BC',
    Status: '1', JobDescription: 'Requirements: React, TypeScript, CSS, accessibility, Figma, Playwright, Docker.',
  });
  await createApplication(page, { CompanyName: 'Ultra Long Company Name That Should Wrap Or Truncate Gracefully GmbH', RoleTitle: 'Senior Staff Platform Reliability Engineering Intern (Summer)', Location: 'Remote — Worldwide', Status: '2' });
  await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
  const appId = await page.evaluate(() => document.querySelector('tr[data-app-id]')?.getAttribute('data-app-id'));

  const pages = [
    ['landing', '/', false],
    ['login', '/Identity/Account/Login', false],
    ['dashboard', '/Home/Dashboard', true],
    ['applications-list', '/JobApplications?view=list', true],
    ['board', '/JobApplications/Board', true],
    ['create', '/JobApplications/Create', true],
    ['edit', `/JobApplications/Edit/${appId}`, true],
    ['profile', '/Profile', true],
    ['cover-letter', `/CoverLetter/Generate?appId=${appId}`, true],
  ];

  const anon = await browser.newContext();
  const pan = await anon.newPage();

  for (const [vpName, w, h] of VIEWPORTS) {
    for (const theme of ['light', 'dark']) {
      await page.setViewportSize({ width: w, height: h });
      await pan.setViewportSize({ width: w, height: h });
      for (const [name, url, authed] of pages) {
        const p = authed ? page : pan;
        await check(D, `${vpName}/${theme}: ${name} has no horizontal overflow`, async () => {
          await p.goto(BASE + url, { waitUntil: 'networkidle' });
          await p.evaluate((t) => { document.documentElement.setAttribute('data-theme', t); try { localStorage.theme = t; } catch {} }, theme);
          await freeze(p);
          await p.waitForTimeout(150);
          const file = await shot(p, `${vpName}-${theme}-${name}`);
          const overflow = await p.evaluate(() => {
            const docW = document.documentElement.clientWidth;
            const offenders = [];
            for (const el of document.querySelectorAll('body *')) {
              const r = el.getBoundingClientRect();
              if (r.width === 0 || r.height === 0) continue;
              const cs = getComputedStyle(el);
              if (cs.position === 'fixed') continue;
              if (r.right > docW + 1 || r.left < -1) {
                offenders.push(`${el.tagName}.${(el.className || '').toString().split(' ')[0]} [${Math.round(r.left)}..${Math.round(r.right)}] vs ${docW}`);
              }
              if (offenders.length >= 4) break;
            }
            return { scrollW: document.documentElement.scrollWidth, docW, offenders };
          });
          assert(overflow.scrollW <= overflow.docW + 1,
            `page scrolls horizontally: scrollWidth ${overflow.scrollW} > viewport ${overflow.docW}. First offenders: ${overflow.offenders.join(' | ') || '(none identified)'}\n    screenshot: ${file}`);
          return `no overflow (${overflow.docW}px); screenshot ${file}`;
        });
      }
    }
  }

  await page.setViewportSize({ width: 1440, height: 900 });

  await check(D, 'Long company and role names do not break the list layout', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const clipped = await page.evaluate(() => {
      const out = [];
      for (const cell of document.querySelectorAll('tr[data-app-id] td')) {
        if (cell.scrollWidth > cell.clientWidth + 2) {
          const cs = getComputedStyle(cell);
          const handled = cs.textOverflow === 'ellipsis' || cs.overflow !== 'visible' || cs.whiteSpace === 'normal';
          if (!handled) out.push(`${cell.textContent.trim().slice(0, 40)} (${cell.scrollWidth} > ${cell.clientWidth})`);
        }
      }
      return out;
    });
    assertEqual(clipped.length, 0, `cells overflow with no ellipsis/wrap: ${clipped.join('; ')}`);
    return 'long values wrap or ellipsize';
  });

  await check(D, 'Dark mode repaints every surface (no light-theme leftovers)', async () => {
    const problems = [];
    for (const [name, url, authed] of pages) {
      const p = authed ? page : pan;
      await p.goto(BASE + url, { waitUntil: 'networkidle' });
      await p.evaluate(() => document.documentElement.setAttribute('data-theme', 'dark'));
      await p.waitForTimeout(120);
      const light = await p.evaluate(() => {
        const toRgb = (s) => (s.match(/\d+/g) || []).map(Number);
        const bright = [];
        for (const el of document.querySelectorAll('body *')) {
          const r = el.getBoundingClientRect();
          if (r.width < 120 || r.height < 40) continue;
          const bg = toRgb(getComputedStyle(el).backgroundColor);
          if (bg.length >= 3 && (bg[3] === undefined || bg[3] > 0.5) && bg[0] > 235 && bg[1] > 235 && bg[2] > 235) {
            bright.push(`${el.tagName}.${(el.className || '').toString().split(' ')[0]}`);
          }
          if (bright.length >= 3) break;
        }
        return bright;
      });
      if (light.length) problems.push(`${name}: ${light.join(', ')}`);
    }
    assertEqual(problems.length, 0, `near-white surfaces while dark theme is active: ${problems.join(' | ')}`);
    return 'no light surfaces left in dark mode';
  });

  await check(D, 'The sticky header does not cover page content', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const geom = await page.evaluate(() => {
      const header = document.querySelector('body > header');
      const navH = getComputedStyle(document.documentElement).getPropertyValue('--site-nav-h').trim();
      const main = document.querySelector('main');
      return {
        headerPosition: header ? getComputedStyle(header).position : null,
        headerH: header ? Math.round(header.getBoundingClientRect().height) : null,
        navVar: navH,
        scrollPadding: getComputedStyle(document.documentElement).scrollPaddingTop,
        mainTransform: main ? getComputedStyle(main).transform : null,
      };
    });
    assertEqual(geom.headerPosition, 'sticky', `body > header position is ${geom.headerPosition}`);
    assert(geom.navVar && parseFloat(geom.navVar) > 0, `--site-nav-h is "${geom.navVar}"`);
    assert(geom.mainTransform === 'none', `main retains transform "${geom.mainTransform}" — fixed children (drawer, modals, bulk bar) would be trapped inside it`);
    return `sticky header ${geom.headerH}px, --site-nav-h ${geom.navVar}, scroll-padding-top ${geom.scrollPadding}, main transform ${geom.mainTransform}`;
  });

  await check(D, 'Drawer, modals and bulk bar escape their container when open', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    await page.click('tr[data-app-id]');
    await page.waitForTimeout(400);
    const box = await page.evaluate(() => {
      const d = document.querySelector('.app-drawer');
      const r = d.getBoundingClientRect();
      return { position: getComputedStyle(d).position, top: Math.round(r.top), right: Math.round(r.right), vw: window.innerWidth, visible: r.width > 100 };
    });
    assertEqual(box.position, 'fixed', `drawer position is ${box.position}`);
    assert(box.visible && box.right <= box.vw + 2, `drawer is off-screen or clipped: ${JSON.stringify(box)}`);
    const file = await shot(page, 'desktop-light-drawer-open');
    return `drawer fixed at top ${box.top}, right ${box.right}/${box.vw}; screenshot ${file}`;
  });

  await check(D, 'Empty states render for a brand-new account', async () => {
    const { context: fresh } = await newSignedInContext(browser, 'empty');
    const p = await fresh.newPage();
    await p.setViewportSize({ width: 1440, height: 900 });
    const shots = [];
    for (const [name, url] of [['dashboard-empty', '/Home/Dashboard'], ['list-empty', '/JobApplications?view=list'], ['board-empty', '/JobApplications/Board']]) {
      await p.goto(BASE + url, { waitUntil: 'networkidle' });
      await freeze(p);
      shots.push(await shot(p, `desktop-light-${name}`));
      const text = await p.textContent('body');
      assert(text.replace(/\s+/g, ' ').trim().length > 80, `${url} renders an essentially blank page for a new account`);
    }
    await fresh.close();
    return shots.join(', ');
  });

  await check(D, 'Screenshot baselines were written', async () => {
    const dir = path.join(ARTIFACTS, 'screenshots');
    const files = fs.existsSync(dir) ? fs.readdirSync(dir).filter((f) => f.endsWith('.png')) : [];
    assert(files.length >= 50, `only ${files.length} screenshots captured`);
    return `${files.length} baseline screenshots in ${path.relative(process.cwd(), dir)} (first run — nothing to diff against yet)`;
  });

  await browser.close();
}
