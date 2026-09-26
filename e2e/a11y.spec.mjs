// Accessibility dimension: axe-core (WCAG 2.0/2.1 A + AA) on every main page in both themes,
// plus manual keyboard, focus-trap and accessible-name checks that axe cannot see.
import { chromium, BASE, check, assert, assertEqual, skip, newSignedInContext, createApplication, axeSource, freeze } from './lib/harness.mjs';

const D = 'Accessibility';
const AXE = axeSource();

async function scan(page, theme) {
  if (theme) await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
  await page.addScriptTag({ content: AXE });
  return page.evaluate(async () => {
    const r = await window.axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'] } });
    return r.violations.map((v) => ({
      id: v.id, impact: v.impact, help: v.help, count: v.nodes.length,
      nodes: v.nodes.slice(0, 3).map((n) => ({ target: n.target.join(' '), summary: (n.failureSummary || '').replace(/\s+/g, ' ').slice(0, 220) })),
    }));
  });
}

function fmt(violations) {
  return violations.map((v) => `${v.id} (${v.impact}, ${v.count}x): ${v.help}\n      e.g. ${v.nodes[0]?.target} — ${v.nodes[0]?.summary}`).join('\n    ');
}

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });
  const { context } = await newSignedInContext(browser, 'a11y');
  const page = await context.newPage();
  const anon = await browser.newContext({ viewport: { width: 1440, height: 900 } });
  const pan = await anon.newPage();

  await createApplication(page, {
    CompanyName: 'Axe Testing Ltd', RoleTitle: 'Accessibility Intern', Location: 'Remote',
    Status: '1', JobDescription: 'Requirements: WCAG, ARIA, Playwright, TypeScript, Docker, Kubernetes, PostgreSQL.',
  });
  // Every status, and every attention state, so axe judges the colours users actually see. A check only
  // covers the states its data holds: with one Applied row this dimension passed for months while the dark
  // Rejected badge, the Interview badge and the "due" tags all failed contrast — color-audit found them
  // (2026-09-26) because its own seed had every status. Saved and overdue, Applied long enough ago that a
  // follow-up is due, Interview with a deadline three days out, Rejected, Offer.
  const day = (offset) => new Date(Date.now() + offset * 86400000).toISOString().slice(0, 10);
  for (const [CompanyName, Status, extra] of [
    ['Saved Overdue Co', '0', { Deadline: day(-2) }],
    ['Applied Waiting Co', '1', { DateApplied: day(-12) }],
    ['Interview Soon Co', '2', { Deadline: day(3) }],
    ['Rejected Co', '3', {}],
    ['Offer Co', '4', {}],
  ]) {
    await createApplication(page, { CompanyName, RoleTitle: 'Intern', Status, ...extra });
  }
  await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
  const appId = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]'))
    .find((r) => r.textContent.includes('Axe Testing Ltd'))?.getAttribute('data-app-id'));

  const pages = [
    ['Landing (anonymous)', '/', pan],
    ['Login', '/Identity/Account/Login', pan],
    ['Register', '/Identity/Account/Register', pan],
    ['Forgot password', '/Identity/Account/ForgotPassword', pan],
    ['Dashboard', '/Home/Dashboard', page],
    ['Applications list', '/JobApplications?view=list', page],
    ['Kanban board', '/JobApplications/Board', page],
    ['Create application', '/JobApplications/Create', page],
    ['Edit application', `/JobApplications/Edit/${appId}`, page],
    ['Profile', '/Profile', page],
    ['Cover letter', `/CoverLetter/Generate?appId=${appId}`, page],
    ['Interview prep', `/InterviewPrep/Prep?appId=${appId}`, page],
    ['Privacy', '/Home/Privacy', pan],
    // Added 2026-09-26 with the running-text underline rule: pages whose prose links were never scanned.
    ['Terms', '/Home/Terms', pan],
    ['Practice', '/Practice', page],
    ['Account settings', '/Identity/Account/Manage', page],
    ['Change password', '/Identity/Account/Manage/ChangePassword', page],
    ['Delete account', '/Identity/Account/Manage/DeletePersonalData', page],
  ];

  for (const [name, url, p] of pages) {
    for (const theme of ['light', 'dark']) {
      await check(D, `axe ${theme}: ${name}`, async () => {
        if (!AXE) skip('axe-core not available (AXE_PATH unset)');
        await p.goto(BASE + url, { waitUntil: 'networkidle' });
        await freeze(p);
        // The mouse stays wherever the last click left it, so without this a scan judged whichever row
        // happened to sit under the pointer — pass or fail by layout. Hover is covered on purpose below.
        await p.mouse.move(0, 0);
        const v = await scan(p, theme);
        const serious = v.filter((x) => x.impact === 'critical' || x.impact === 'serious');
        assert(serious.length === 0, `${serious.length} serious/critical violation(s):\n    ${fmt(serious)}`);
        return v.length ? `no serious issues; ${v.length} minor/moderate: ${v.map((x) => `${x.id}(${x.impact},${x.count})`).join(', ')}` : 'no violations';
      });
    }
  }

  // A hovered row is a state every user sees and a page-load scan never does: it tints the row and reveals
  // the row actions, and both the company avatar and the dark row buttons failed contrast only there.
  for (const theme of ['light', 'dark']) {
    await check(D, `axe ${theme}: Applications list with a row hovered`, async () => {
      if (!AXE) skip('axe-core not available (AXE_PATH unset)');
      await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
      await freeze(page);
      await page.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
      await page.hover('tr[data-app-id] .company-avatar');
      const v = await scan(page, null);
      const serious = v.filter((x) => x.impact === 'critical' || x.impact === 'serious');
      assert(serious.length === 0, `${serious.length} serious/critical violation(s):\n    ${fmt(serious)}`);
      return 'no serious issues on the hovered row';
    });
  }

  // ------------------------------------------------------------ keyboard
  await check(D, 'A skip link is the first focusable element', async () => {
    await page.goto(BASE + '/Home/Dashboard', { waitUntil: 'networkidle' });
    await page.keyboard.press('Tab');
    const first = await page.evaluate(() => {
      const el = document.activeElement;
      return { tag: el.tagName, text: (el.textContent || '').trim().slice(0, 60), href: el.getAttribute('href') };
    });
    assert(/skip/i.test(first.text) || /^#(main|content)/.test(first.href || ''), `first tab stop is ${first.tag} "${first.text}" (href ${first.href}) — no skip link`);
    return `${first.tag} "${first.text}" -> ${first.href}`;
  });

  // The skip link only shows while focused, so a page-load scan never judges its colours — which is how a
  // leftover template stylesheet painting it #0077cc (3.65:1 in dark) went unseen. Focus it, then ask axe.
  for (const [layout, url, p] of [['app layout', '/Home/Dashboard', page], ['app layout, signed out', '/Home/Privacy', pan], ['auth layout', '/Identity/Account/Login', pan]]) {
    for (const theme of ['light', 'dark']) {
      await check(D, `axe ${theme}: the focused skip link (${layout})`, async () => {
        if (!AXE) skip('axe-core not available (AXE_PATH unset)');
        await p.goto(BASE + url, { waitUntil: 'networkidle' });
        await freeze(p);
        await p.evaluate((t) => document.documentElement.setAttribute('data-theme', t), theme);
        await p.keyboard.press('Tab');
        assert(await p.evaluate(() => document.activeElement?.classList.contains('skip-link')), 'the first Tab did not focus the skip link');
        await p.addScriptTag({ content: AXE });
        const v = await p.evaluate(async () => (await window.axe.run({ include: [['.skip-link']] }, { runOnly: { type: 'rule', values: ['color-contrast'] } })).violations);
        assert(v.length === 0, v.map((x) => x.nodes[0]?.failureSummary).join('; '));
        return 'passes color-contrast while focused';
      });
    }
  }

  await check(D, 'Every interactive control on the list page is reachable by keyboard', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const unreachable = await page.evaluate(() => {
      const interactive = Array.from(document.querySelectorAll('a[href], button, input, select, textarea, [role=button]'))
        .filter((el) => {
          const cs = getComputedStyle(el);
          return cs.display !== 'none' && cs.visibility !== 'hidden' && el.offsetParent !== null && !el.disabled;
        });
      return interactive.filter((el) => el.tabIndex < 0).map((el) => `${el.tagName}.${el.className}`.slice(0, 70));
    });
    assertEqual(unreachable.length, 0, `controls removed from the tab order: ${unreachable.join(', ')}`);
    return 'all visible controls are in the tab order';
  });

  await check(D, 'Every form control on Create has an accessible name', async () => {
    await page.goto(BASE + '/JobApplications/Create', { waitUntil: 'networkidle' });
    const unnamed = await page.evaluate(() => {
      const named = (el) => {
        if (el.getAttribute('aria-label') || el.getAttribute('aria-labelledby') || el.getAttribute('title')) return true;
        if (el.id && document.querySelector(`label[for="${CSS.escape(el.id)}"]`)) return true;
        return Boolean(el.closest('label'));
      };
      return Array.from(document.querySelectorAll('input:not([type=hidden]), select, textarea'))
        .filter((el) => el.offsetParent !== null && !named(el))
        .map((el) => `${el.tagName}#${el.id || '(no id)'}[name=${el.name || ''}]`);
    });
    assertEqual(unnamed.length, 0, `controls with no accessible name: ${unnamed.join(', ')}`);
    return 'every visible control is labelled';
  });

  await check(D, 'No <label for=...> points at a missing element', async () => {
    const problems = [];
    for (const [url, p2] of [['/JobApplications/Create', page], [`/JobApplications/Edit/${appId}`, page], ['/Profile', page], ['/Identity/Account/Register', pan], ['/Identity/Account/Login', pan]]) {
      await p2.goto(BASE + url, { waitUntil: 'networkidle' });
      const orphans = await p2.evaluate(() => Array.from(document.querySelectorAll('label[for]'))
        .filter((l) => !document.getElementById(l.getAttribute('for')))
        .map((l) => `for="${l.getAttribute('for')}" ("${(l.textContent || '').trim().slice(0, 30)}")`));
      if (orphans.length) problems.push(`${url}: ${orphans.join(', ')}`);
    }
    assertEqual(problems.length, 0, `orphaned labels:\n    ${problems.join('\n    ')}`);
    return 'every label points at a real control';
  });

  await check(D, 'Every icon-only button has an accessible name', async () => {
    const problems = [];
    for (const url of ['/Home/Dashboard', '/JobApplications?view=list', '/JobApplications/Board', '/Profile']) {
      await page.goto(BASE + url, { waitUntil: 'networkidle' });
      const bad = await page.evaluate(() => Array.from(document.querySelectorAll('button, a[href]'))
        .filter((el) => el.offsetParent !== null)
        .filter((el) => !(el.textContent || '').trim())
        .filter((el) => !el.getAttribute('aria-label') && !el.getAttribute('aria-labelledby') && !el.getAttribute('title'))
        .map((el) => `${el.tagName}.${(el.className || '').toString().slice(0, 40)}`));
      if (bad.length) problems.push(`${url}: ${bad.join(', ')}`);
    }
    assertEqual(problems.length, 0, `icon-only controls with no name:\n    ${problems.join('\n    ')}`);
    return 'all icon-only controls are named';
  });

  await check(D, 'Opening the detail drawer moves focus into the dialog', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    await page.click('tr[data-app-id]');
    await page.waitForTimeout(500);
    const inside = await page.evaluate(() => {
      const d = document.querySelector('.app-drawer');
      return { open: d?.classList.contains('active'), focusInside: d?.contains(document.activeElement), active: `${document.activeElement.tagName}.${(document.activeElement.className || '').toString().slice(0, 40)}`, ariaModal: d?.getAttribute('aria-modal') };
    });
    assert(inside.open, 'drawer did not open on click');
    assert(inside.focusInside, `role="dialog" opened but focus stayed outside it (on ${inside.active}); aria-modal=${inside.ariaModal}`);
    return 'focus moved into the drawer';
  });

  await check(D, 'The detail drawer traps focus while open', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    await page.click('tr[data-app-id]');
    await page.waitForTimeout(500);
    const open = await page.evaluate(() => document.querySelector('.app-drawer')?.classList.contains('active'));
    assert(open, 'drawer did not open on click');
    const escaped = [];
    for (let i = 0; i < 30; i++) {
      await page.keyboard.press('Tab');
      const inside = await page.evaluate(() => {
        const d = document.querySelector('.app-drawer');
        return d && d.contains(document.activeElement);
      });
      if (!inside) {
        escaped.push(await page.evaluate(() => `${document.activeElement.tagName}.${(document.activeElement.className || '').toString().slice(0, 40)}`));
        break;
      }
    }
    assertEqual(escaped.length, 0, `focus left the open drawer after tabbing: reached ${escaped[0]}`);
    return 'focus stayed inside the drawer for 30 tab presses';
  });

  await check(D, 'The drawer announces itself as a modal dialog', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    await page.click('tr[data-app-id]');
    await page.waitForTimeout(400);
    const open = await page.evaluate(() => {
      const d = document.querySelector('.app-drawer');
      return { role: d.getAttribute('role'), modal: d.getAttribute('aria-modal'), hidden: d.getAttribute('aria-hidden'), label: d.getAttribute('aria-label') };
    });
    assertEqual(open.role, 'dialog', 'drawer role');
    assertEqual(open.modal, 'true', 'aria-modal while open');
    assertEqual(open.hidden, 'false', 'aria-hidden while open');
    assert(open.label, 'dialog has no accessible name');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);
    const closed = await page.evaluate(() => {
      const d = document.querySelector('.app-drawer');
      return { modal: d.getAttribute('aria-modal'), hidden: d.getAttribute('aria-hidden') };
    });
    assertEqual(closed.hidden, 'true', 'aria-hidden after close');
    assert(!closed.modal, `aria-modal left behind after close: ${closed.modal}`);
    return `role=dialog aria-modal toggles, labelled "${open.label}"`;
  });

  await check(D, 'The drawer can be opened from the keyboard alone', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    // Tab to the row's opener button and press Enter - no mouse anywhere in this test.
    const reached = await page.evaluate(() => {
      const b = document.querySelector('tr[data-app-id] .row-open');
      if (!b) return false;
      b.focus();
      return document.activeElement === b;
    });
    assert(reached, 'no focusable opener on the row');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(500);
    const state = await page.evaluate(() => ({
      open: document.querySelector('.app-drawer')?.classList.contains('active'),
      focusInside: document.querySelector('.app-drawer')?.contains(document.activeElement),
    }));
    assert(state.open, 'Enter on the opener did not open the drawer');
    assert(state.focusInside, 'focus did not move into the drawer');
    return 'Enter on the row opener opens the drawer and moves focus in';
  });

  await check(D, 'Closing the drawer returns focus to the row that opened it', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    await page.evaluate(() => document.querySelector('tr[data-app-id] .row-open').focus());
    await page.keyboard.press('Enter');
    await page.waitForTimeout(400);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(400);
    const back = await page.evaluate(() => ({
      onOpener: document.activeElement?.classList.contains('row-open'),
      where: `${document.activeElement.tagName}.${(document.activeElement.className || '').toString().slice(0, 30)}`,
    }));
    assert(back.onOpener, `focus landed on ${back.where}, not the opener button`);
    return 'focus returned to the row opener';
  });

  await check(D, 'Escape closes the drawer and focus returns to the row', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    await page.click('tr[data-app-id]');
    await page.waitForTimeout(400);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(400);
    const state = await page.evaluate(() => ({
      open: document.querySelector('.app-drawer')?.classList.contains('active'),
      focus: `${document.activeElement.tagName}${document.activeElement.getAttribute?.('data-app-id') ? '[data-app-id]' : ''}`,
      onRow: Boolean(document.activeElement.closest?.('tr[data-app-id]')),
    }));
    assert(!state.open, 'Escape did not close the drawer');
    // Opened by clicking the row background, so nothing was focused at open time; the fallback
    // puts focus on that row's opener rather than dropping it on <body>.
    assert(state.onRow, `focus did not return to the triggering row (it is on ${state.focus})`);
    return 'drawer closed, focus restored to the row';
  });

  await check(D, 'The keyboard-shortcuts modal opens with ? and closes with Escape', async () => {
    await page.goto(BASE + '/Home/Dashboard', { waitUntil: 'networkidle' });
    await page.keyboard.press('Shift+Slash');
    await page.waitForTimeout(400);
    const opened = await page.evaluate(() => {
      const m = document.querySelector('#kbd-modal');
      return m ? m.classList.contains('active') && m.getAttribute('aria-hidden') === 'false' : false;
    });
    assert(opened, 'the ? shortcut did not open the shortcuts modal');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(400);
    const closed = await page.evaluate(() => {
      const m = document.querySelector('#kbd-modal');
      return m ? !m.classList.contains('active') && m.getAttribute('aria-hidden') === 'true' : true;
    });
    assert(closed, 'Escape did not close the shortcuts modal');
    return 'opens with ?, closes with Escape';
  });

  await check(D, 'A visible focus indicator is present on keyboard focus', async () => {
    await page.goto(BASE + '/JobApplications/Create', { waitUntil: 'networkidle' });
    const styled = await page.evaluate(() => {
      const el = document.querySelector('#CompanyName');
      el.focus();
      const cs = getComputedStyle(el);
      return { outline: cs.outlineStyle + ' ' + cs.outlineWidth, shadow: cs.boxShadow };
    });
    const visible = (styled.outline && !/none/.test(styled.outline)) || (styled.shadow && styled.shadow !== 'none');
    assert(visible, `focused input has no outline or focus ring: ${JSON.stringify(styled)}`);
    return `outline "${styled.outline}", box-shadow "${styled.shadow.slice(0, 60)}"`;
  });

  await check(D, 'The page has exactly one h1 and a sane heading order', async () => {
    const problems = [];
    for (const url of ['/', '/Home/Dashboard', '/JobApplications?view=list', '/Profile', '/JobApplications/Create']) {
      const p = url.startsWith('/Home') || url.startsWith('/JobApplications') || url.startsWith('/Profile') ? page : pan;
      await p.goto(BASE + url, { waitUntil: 'networkidle' });
      const h = await p.evaluate(() => Array.from(document.querySelectorAll('h1,h2,h3,h4,h5,h6')).map((x) => Number(x.tagName[1])));
      const h1 = h.filter((n) => n === 1).length;
      if (h1 !== 1) problems.push(`${url}: ${h1} <h1> elements`);
      for (let i = 1; i < h.length; i++) if (h[i] - h[i - 1] > 1) { problems.push(`${url}: heading jumps h${h[i - 1]} -> h${h[i]}`); break; }
    }
    assertEqual(problems.length, 0, problems.join('; '));
    return 'one h1 per page, no skipped heading levels';
  });

  await check(D, 'The language and viewport are declared', async () => {
    await pan.goto(BASE + '/', { waitUntil: 'domcontentloaded' });
    const meta = await pan.evaluate(() => ({
      lang: document.documentElement.getAttribute('lang'),
      viewport: document.querySelector('meta[name=viewport]')?.getAttribute('content'),
    }));
    assert(meta.lang, 'no lang attribute on <html>');
    assert(meta.viewport && !/user-scalable=no|maximum-scale=1/.test(meta.viewport), `viewport blocks zoom: ${meta.viewport}`);
    return `lang="${meta.lang}", viewport="${meta.viewport}"`;
  });

  await check(D, 'Live regions announce toast messages', async () => {
    await page.goto(BASE + '/Home/Dashboard', { waitUntil: 'networkidle' });
    const toast = await page.evaluate(() => {
      if (typeof window.showAppToast === 'function') window.showAppToast('success', 'a11y probe');
      const t = document.querySelector('#app-toast, .app-toast, [role=status], [aria-live]');
      return t ? { role: t.getAttribute('role'), live: t.getAttribute('aria-live'), cls: t.className } : null;
    });
    assert(toast, 'showAppToast produced no element carrying role/aria-live');
    assert(toast.role === 'status' || toast.role === 'alert' || toast.live, `toast container is not a live region: ${JSON.stringify(toast)}`);
    return `role=${toast.role} aria-live=${toast.live}`;
  });

  await browser.close();
}
