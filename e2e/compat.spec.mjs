// Compatibility dimension: browser engines, device emulation, degraded environments (no JS,
// blocked storage, offline, slow network), reduced motion, and non-Latin / RTL text.
// Chromium is the system Chrome channel; Firefox and WebKit come from the Playwright browser
// download. If an engine is not installed its checks record SKIP with the install command, so the
// dimension still runs on a machine that only has Chrome.
import { chromium, firefox, webkit, devices, BASE, check, assert, assertEqual, skip, newSignedInContext, createApplication, antiforgery, postForm, shot, isCspReportNoise } from './lib/harness.mjs';

const D = 'Compatibility';

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });
  const { context } = await newSignedInContext(browser, 'compat');
  const page = await context.newPage();

  await createApplication(page, { CompanyName: 'Compat Co', RoleTitle: 'Platform Intern', Location: 'Remote', Status: '1', JobDescription: 'Requirements: Docker, Kubernetes, Go, PostgreSQL.' });
  await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
  const appId = await page.evaluate(() => document.querySelector('tr[data-app-id]')?.getAttribute('data-app-id'));

  // ------------------------------------------------------------ browser engines
  // Everything else in this file is Chromium. These run the core of the app on Gecko and WebKit
  // too: the pages the app is actually used through, the JS-built drawer and the filters, and a
  // real form post. Each engine gets its own account, created through the Register page from its
  // own synthetic client address.
  for (const [name, engineType] of [['Firefox', firefox], ['WebKit', webkit]]) {
    let engine = null;
    try {
      engine = await engineType.launch();
    } catch (err) {
      for (const label of ['pages render', 'drawer and filters work', 'a form post round-trips']) {
        await check(D, `${name}: ${label}`, async () => {
          skip(`${name} is not installed: ${String(err.message).split('\n')[0]}. Install it with "node <playwright>/cli.js install ${name.toLowerCase()}".`);
        });
      }
      continue;
    }

    try {
      const { context: ec } = await newSignedInContext(engine, name.toLowerCase());
      const ep = await ec.newPage();
      // The report-only CSP is separated out rather than counted: Gecko and WebKit log those
      // reports as console errors and Chromium does not, so without this the same clean page
      // "fails" on two engines out of three. The count is still reported as evidence.
      const engineErrors = [];
      const cspReports = [];
      const note = (text) => (isCspReportNoise(text) ? cspReports : engineErrors).push(text);
      ep.on('pageerror', (e) => note(e.message));
      ep.on('console', (m) => { if (m.type() === 'error') note(`console: ${m.text()}`); });

      await createApplication(ep, {
        CompanyName: `${name} Co`, RoleTitle: 'Platform Intern', Location: 'Remote', Status: '1',
        JobDescription: 'Requirements: Docker, Kubernetes, Go, PostgreSQL.',
      });

      await check(D, `${name}: pages render`, async () => {
        const out = [];
        for (const [label, url] of [['landing', '/'], ['dashboard', '/Home/Dashboard'], ['list', '/JobApplications?view=list'], ['board', '/JobApplications/Board'], ['create', '/JobApplications/Create'], ['profile', '/Profile']]) {
          const res = await ep.goto(BASE + url, { waitUntil: 'networkidle' });
          assertEqual(res.status(), 200, `${label} status on ${name}`);
          const m = await ep.evaluate(() => ({
            scrollW: document.documentElement.scrollWidth,
            docW: document.documentElement.clientWidth,
            text: document.body.textContent.replace(/\s+/g, ' ').trim().length,
            bg: getComputedStyle(document.body).backgroundColor,
          }));
          assert(m.text > 200, `${label} renders almost nothing on ${name}`);
          assert(m.bg && m.bg !== 'rgba(0, 0, 0, 0)', `${label} painted no background on ${name}`);
          out.push(`${label}: ${m.scrollW}/${m.docW}${m.scrollW > m.docW + 1 ? ' OVERFLOW' : ''}`);
        }
        await shot(ep, `engine-${name.toLowerCase()}-list`);
        const overflowing = out.filter((o) => o.includes('OVERFLOW'));
        assertEqual(overflowing.length, 0, `horizontal overflow on ${name}: ${overflowing.join(', ')}`);
        assertEqual(engineErrors.length, 0, `uncaught/console errors on ${name}: ${engineErrors.slice(0, 3).join(' | ')}`);
        return `${out.join('; ')}; ${cspReports.length} report-only CSP notices (not errors)`;
      });

      await check(D, `${name}: drawer and filters work`, async () => {
        await ep.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
        await ep.click('tr[data-app-id] .row-open');
        await ep.waitForSelector('.app-drawer.active', { timeout: 5000 });
        const d = await ep.evaluate(() => ({
          company: document.querySelector('#drawer-company')?.textContent?.trim() || '',
          focusInside: document.querySelector('.app-drawer').contains(document.activeElement),
        }));
        assert(d.company.length > 0, `drawer opened with no company on ${name}`);
        await ep.click('#drawer-close');
        await ep.waitForTimeout(400);
        const closed = await ep.evaluate(() => !document.querySelector('.app-drawer').classList.contains('active'));
        assert(closed, `the drawer close button did not close it on ${name}`);

        await ep.goto(`${BASE}/JobApplications?view=list&search=${encodeURIComponent(name)}`, { waitUntil: 'networkidle' });
        const rows = await ep.evaluate(() => document.querySelectorAll('tr[data-app-id]').length);
        assert(rows > 0, `search returned nothing on ${name}`);
        assertEqual(engineErrors.length, 0, `uncaught/console errors on ${name}: ${engineErrors.slice(0, 3).join(' | ')}`);
        return `drawer opened on "${d.company}" (focus inside: ${d.focusInside}) and closed; search matched ${rows} row(s)`;
      });

      await check(D, `${name}: a form post round-trips`, async () => {
        const marker = `${name} Form Co`;
        await createApplication(ep, { CompanyName: marker, RoleTitle: 'Form Intern', Status: '0' });
        await ep.goto(`${BASE}/JobApplications?view=list&search=${encodeURIComponent(marker)}`, { waitUntil: 'networkidle' });
        const found = (await ep.textContent('body')).includes(marker);
        assert(found, `the Create form did not round-trip on ${name}`);
        return `"${marker}" created and listed`;
      });

      await ec.close();
    } finally {
      await engine.close();
    }
  }

  // ------------------------------------------------------------ device emulation
  for (const deviceName of ['iPhone 13', 'Pixel 5', 'iPad (gen 7)']) {
    await check(D, `${deviceName}: core pages render and the nav is reachable`, async () => {
      const d = devices[deviceName];
      if (!d) skip(`device profile "${deviceName}" not in this Playwright build`);
      const ctx = await browser.newContext({ ...d, storageState: await context.storageState() });
      const p = await ctx.newPage();
      const out = [];
      for (const [label, url] of [['dashboard', '/Home/Dashboard'], ['list', '/JobApplications?view=list'], ['board', '/JobApplications/Board'], ['create', '/JobApplications/Create']]) {
        const res = await p.goto(BASE + url, { waitUntil: 'networkidle' });
        assertEqual(res.status(), 200, `${label} status on ${deviceName}`);
        const m = await p.evaluate(() => ({
          scrollW: document.documentElement.scrollWidth,
          docW: document.documentElement.clientWidth,
          navToggle: Boolean(document.querySelector('.navbar-toggler')),
          navVisible: (() => { const t = document.querySelector('.navbar-toggler'); return t ? t.offsetParent !== null : false; })(),
        }));
        out.push(`${label}: ${m.scrollW}/${m.docW}${m.scrollW > m.docW + 1 ? ' OVERFLOW' : ''}`);
      }
      const toggler = await p.$('.navbar-toggler');
      if (toggler && await toggler.isVisible()) {
        await toggler.click();
        await p.waitForTimeout(400);
        const opened = await p.evaluate(() => Boolean(document.querySelector('.navbar-collapse.show, .navbar-collapse.collapsing')));
        assert(opened, 'the mobile nav toggle did not open the menu');
      }
      await shot(p, `device-${deviceName.replace(/[^a-z0-9]+/gi, '-').toLowerCase()}-dashboard`);
      const overflowing = out.filter((o) => o.includes('OVERFLOW'));
      await ctx.close();
      assertEqual(overflowing.length, 0, `horizontal overflow on ${deviceName}: ${overflowing.join(', ')}`);
      return out.join('; ');
    });
  }

  // ------------------------------------------------------------ degraded environments
  await check(D, 'Core pages still work with JavaScript disabled', async () => {
    const ctx = await browser.newContext({ javaScriptEnabled: false, storageState: await context.storageState() });
    const p = await ctx.newPage();
    const out = [];
    for (const [label, url] of [['dashboard', '/Home/Dashboard'], ['list', '/JobApplications?view=list'], ['create', '/JobApplications/Create'], ['profile', '/Profile']]) {
      const res = await p.goto(BASE + url, { waitUntil: 'domcontentloaded' });
      const text = (await p.textContent('body')).replace(/\s+/g, ' ').trim();
      out.push(`${label} ${res.status()} (${text.length} chars)`);
      assertEqual(res.status(), 200, `${label} without JS`);
      assert(text.length > 200, `${label} renders almost nothing without JS`);
    }
    // A plain form post must still create a record without client-side JS.
    await p.goto(BASE + '/JobApplications/Create', { waitUntil: 'domcontentloaded' });
    await p.fill('#CompanyName', 'NoJS Corp');
    await p.fill('#RoleTitle', 'NoJS Intern');
    await p.click('#appForm button[type=submit]:not([form])');
    await p.waitForLoadState('domcontentloaded');
    await p.goto(BASE + '/JobApplications?view=list&search=NoJS', { waitUntil: 'domcontentloaded' });
    const created = (await p.textContent('body')).includes('NoJS Corp');
    await ctx.close();
    assert(created, 'the Create form does not work without JavaScript (server-side post failed)');
    return `${out.join('; ')}; form post without JS succeeded`;
  });

  await check(D, 'Blocked browser storage does not break the page or the theme', async () => {
    const ctx = await browser.newContext({ storageState: await context.storageState() });
    const p = await ctx.newPage();
    await p.addInitScript(() => {
      const boom = () => { throw new DOMException('denied', 'SecurityError'); };
      Object.defineProperty(window, 'localStorage', { get: boom, configurable: true });
      Object.defineProperty(window, 'sessionStorage', { get: boom, configurable: true });
    });
    const errors = [];
    p.on('pageerror', (e) => errors.push(e.message));
    for (const url of ['/', '/Home/Dashboard', '/JobApplications?view=list', '/Profile']) {
      await p.goto(BASE + url, { waitUntil: 'networkidle' });
      const painted = await p.evaluate(() => document.body && getComputedStyle(document.body).backgroundColor);
      assert(painted && painted !== 'rgba(0, 0, 0, 0)', `${url} rendered with no background while storage was blocked`);
    }
    const themeToggleWorks = await p.evaluate(() => {
      const t = document.querySelector('#theme-toggle');
      if (!t) return 'no toggle';
      t.click();
      return document.documentElement.getAttribute('data-theme');
    });
    await ctx.close();
    assertEqual(errors.length, 0, `uncaught page errors with storage blocked: ${errors.slice(0, 3).join(' | ')}`);
    return `no uncaught errors; theme toggle produced data-theme="${themeToggleWorks}"`;
  });

  await check(D, 'Going offline mid-session does not corrupt the page or lose form input', async () => {
    const ctx = await browser.newContext({ storageState: await context.storageState() });
    const p = await ctx.newPage();
    await p.goto(BASE + '/JobApplications/Create', { waitUntil: 'networkidle' });
    await p.fill('#CompanyName', 'Offline Draft Co');
    await p.fill('#RoleTitle', 'Offline Role');
    await ctx.setOffline(true);
    const before = await p.inputValue('#CompanyName');
    // A fetch-backed feature must fail visibly rather than hang or throw into the console.
    const errors = [];
    p.on('pageerror', (e) => errors.push(e.message));
    await p.evaluate(() => fetch('/JobApplications/KeywordCoverage', { method: 'POST' }).catch(() => {}));
    await p.waitForTimeout(500);
    const after = await p.inputValue('#CompanyName');
    await ctx.setOffline(false);
    await ctx.close();
    assertEqual(after, before, 'typed input was lost when the connection dropped');
    assertEqual(errors.length, 0, `uncaught errors while offline: ${errors.slice(0, 2).join(' | ')}`);
    return `input preserved ("${after}"), no uncaught errors from failed fetches`;
  });

  await check(D, 'A slow connection does not break the applications list', async () => {
    const ctx = await browser.newContext({ storageState: await context.storageState() });
    const p = await ctx.newPage();
    const cdp = await ctx.newCDPSession(p);
    await cdp.send('Network.enable');
    await cdp.send('Network.emulateNetworkConditions', {
      offline: false, latency: 400, downloadThroughput: (400 * 1024) / 8, uploadThroughput: (400 * 1024) / 8,
    });
    const t0 = Date.now();
    const res = await p.goto(BASE + '/JobApplications?view=list', { waitUntil: 'load', timeout: 60000 });
    const elapsed = Date.now() - t0;
    const rows = await p.evaluate(() => document.querySelectorAll('tr[data-app-id]').length);
    await ctx.close();
    assertEqual(res.status(), 200, 'list status on a throttled connection');
    assert(rows > 0, 'no rows rendered on a throttled connection');
    return `loaded in ${elapsed} ms at ~400 kbps / 400 ms RTT (Chromium CDP emulation), ${rows} rows rendered`;
  });

  await check(D, 'prefers-reduced-motion is honoured', async () => {
    const ctx = await browser.newContext({ reducedMotion: 'reduce', storageState: await context.storageState() });
    const p = await ctx.newPage();
    await p.goto(BASE + '/Home/Dashboard', { waitUntil: 'networkidle' });
    const animated = await p.evaluate(() => {
      const out = [];
      for (const el of document.querySelectorAll('body *')) {
        const cs = getComputedStyle(el);
        // animation-duration alone is not evidence: `animation: none` leaves the shorthand's
        // duration in place while cancelling the animation. Only a live animation-name counts.
        const running = cs.animationName !== 'none' && (parseFloat(cs.animationDuration) || 0) > 0.1;
        const tdur = parseFloat(cs.transitionDuration) || 0;
        if (running || tdur > 0.3) out.push(`${el.tagName}.${(el.className || '').toString().split(' ')[0]} anim=${cs.animationName}/${cs.animationDuration} trans=${cs.transitionDuration}`);
        if (out.length >= 5) break;
      }
      return out;
    });
    await ctx.close();
    assertEqual(animated.length, 0, `elements still animate under prefers-reduced-motion: ${animated.join('; ')}`);
    return 'no long animations or transitions under reduced motion';
  });

  // ------------------------------------------------------------ text and locale
  await check(D, 'Non-Latin and RTL text round-trips through create, list and export', async () => {
    const samples = [
      ['Arabic (RTL)', 'شركة التقنية المتقدمة', 'مهندس برمجيات متدرب'],
      ['Japanese', '株式会社テクノロジー', 'ソフトウェアエンジニア インターン'],
      ['Emoji + combining', 'Ünïcødé 🏢 Có', 'Intern 🚀'],
    ];
    const seen = [];
    for (const [label, company, role] of samples) {
      await createApplication(page, { CompanyName: company, RoleTitle: role, Status: '0', forceCreate: true });
      await page.goto(`${BASE}/JobApplications?view=list&search=${encodeURIComponent(company.slice(0, 6))}`, { waitUntil: 'networkidle' });
      const shown = await page.evaluate(() => document.body.textContent);
      assert(shown.normalize('NFC').includes(company.normalize('NFC')), `${label}: "${company}" did not round-trip to the list`);
      seen.push(label);
    }
    const csv = await page.evaluate(async (base) => (await (await fetch(`${base}/JobApplications/Export`)).text()), BASE);
    assert(csv.normalize('NFC').includes('شركة التقنية المتقدمة'), 'RTL text was mangled in the CSV export');
    await shot(page, 'desktop-light-rtl-list');
    return `${seen.join(', ')} all round-tripped; CSV export preserved RTL text`;
  });

  await check(D, 'RTL content does not flip or break the surrounding layout', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const m = await page.evaluate(() => ({
      dir: document.documentElement.getAttribute('dir') || getComputedStyle(document.documentElement).direction,
      scrollW: document.documentElement.scrollWidth,
      docW: document.documentElement.clientWidth,
    }));
    assertEqual(m.dir, 'ltr', `document direction changed to ${m.dir} after storing RTL content`);
    assert(m.scrollW <= m.docW + 1, `RTL rows pushed the page to ${m.scrollW}px in a ${m.docW}px viewport`);
    return `direction stays ltr; no overflow (${m.scrollW}/${m.docW})`;
  });

  await check(D, 'Browser back and forward restore filter state', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const all = await page.evaluate(() => document.querySelectorAll('tr[data-app-id]').length);
    await page.goto(BASE + '/JobApplications?view=list&status=Applied', { waitUntil: 'networkidle' });
    const filtered = await page.evaluate(() => document.querySelectorAll('tr[data-app-id]').length);
    await page.goBack({ waitUntil: 'networkidle' });
    const back = await page.evaluate(() => document.querySelectorAll('tr[data-app-id]').length);
    await page.goForward({ waitUntil: 'networkidle' });
    const forward = await page.evaluate(() => document.querySelectorAll('tr[data-app-id]').length);
    assert(filtered <= all, `filter widened the result set (${all} -> ${filtered})`);
    assertEqual(back, all, 'back did not restore the unfiltered list');
    assertEqual(forward, filtered, 'forward did not restore the filtered list');
    return `all=${all}, filtered=${filtered}, back=${back}, forward=${forward}`;
  });

  await check(D, 'No console errors on the main pages', async () => {
    const found = [];
    const p = await context.newPage();
    // Same report-only CSP filter as the engine checks above, so this check means the same thing
    // whichever engine it is pointed at.
    p.on('console', (m) => { if (m.type() === 'error' && !isCspReportNoise(m.text())) found.push(`${p.url().replace(BASE, '')}: ${m.text().slice(0, 160)}`); });
    p.on('pageerror', (e) => { if (!isCspReportNoise(e.message)) found.push(`${p.url().replace(BASE, '')}: ${e.message.slice(0, 160)}`); });
    for (const url of ['/', '/Home/Dashboard', '/JobApplications?view=list', '/JobApplications/Board', '/JobApplications/Create', `/JobApplications/Edit/${appId}`, '/Profile', `/CoverLetter/Generate?appId=${appId}`, `/InterviewPrep/Prep?appId=${appId}`]) {
      await p.goto(BASE + url, { waitUntil: 'networkidle' });
      await p.waitForTimeout(300);
    }
    await p.close();
    // Favicon/analytics style noise is still worth listing, but only real errors fail the check.
    assertEqual(found.length, 0, `console errors:\n    ${found.slice(0, 6).join('\n    ')}`);
    return '9 pages visited, console clean';
  });

  await browser.close();
}
