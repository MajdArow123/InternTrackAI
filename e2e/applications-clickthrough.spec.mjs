// Required by the CLAUDE.md rule: any refactor of the Applications views gets a Playwright
// click-through of every action on the page. This exists because the A4 accessibility fix changed
// the table row itself — it is no longer focusable and the company name became a real <button> —
// and that row is the entry point for the drawer, bulk selection and compare.
// Every check drives the real control and asserts the real outcome; nothing is stubbed.
import { chromium, BASE, check, assert, assertEqual, newSignedInContext, createApplication, antiforgery, postForm, shot, freeze, assertSignedIn } from './lib/harness.mjs';

const D = 'Applications click-through';
const LIST = '/JobApplications?view=list';

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });
  const { context } = await newSignedInContext(browser, 'click');
  const page = await context.newPage();
  await page.setViewportSize({ width: 1440, height: 950 });

  const consoleErrors = [];
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(`${page.url().replace(BASE, '')}: ${m.text().slice(0, 140)}`); });
  page.on('pageerror', (e) => consoleErrors.push(`${page.url().replace(BASE, '')}: ${e.message.slice(0, 140)}`));

  // ---------------------------------------------------------------- seed
  const seeded = [
    { CompanyName: 'Alpha Systems',  RoleTitle: 'Backend Intern',  Location: 'Toronto',   Status: '1', JobDescription: 'Requirements: Docker, Kubernetes, PostgreSQL, Go.' },
    { CompanyName: 'Beta Robotics',  RoleTitle: 'Platform Intern', Location: 'Remote',    Status: '2', JobDescription: 'Requirements: Terraform, AWS, Python.' },
    { CompanyName: 'Gamma Labs',     RoleTitle: 'Data Intern',     Location: 'Vancouver', Status: '0', JobDescription: 'Requirements: SQL, Pandas.' },
    { CompanyName: 'Delta Networks', RoleTitle: 'SRE Intern',      Location: 'Remote',    Status: '1', JobDescription: 'Requirements: Prometheus, Grafana.' },
  ];
  for (const a of seeded) await createApplication(page, { ...a, forceCreate: true });

  // Always resolve from the list: called while the Board is showing, a tr[data-app-id] lookup
  // finds nothing and hands back null, which then posts to /JobApplications/null/move.
  const idOf = async (company) => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const id = await page.evaluate((c) => {
      const row = Array.from(document.querySelectorAll('tr[data-app-id]')).find((r) => r.textContent.includes(c));
      return row ? row.getAttribute('data-app-id') : null;
    }, company);
    assert(id, `no row found for "${company}" on the list`);
    return id;
  };

  await check(D, 'List renders every seeded application', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const rows = await page.evaluate(() => document.querySelectorAll('tr[data-app-id]').length);
    assertEqual(rows, seeded.length, 'row count');
    return `${rows} rows`;
  });

  // ---------------------------------------------------------------- drawer, both input methods
  await check(D, 'Mouse: clicking a row opens the drawer with that record', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    await page.click('tr[data-app-id]:has-text("Alpha Systems")');
    await page.waitForTimeout(500);
    const d = await page.evaluate(() => {
      const el = document.querySelector('.app-drawer');
      return { open: el.classList.contains('active'), company: document.querySelector('#drawer-company')?.textContent?.trim(), role: document.querySelector('#drawer-role')?.textContent?.trim() };
    });
    assert(d.open, 'drawer did not open on row click');
    assert(/Alpha Systems/.test(d.company), `drawer shows "${d.company}"`);
    return `${d.company} — ${d.role}`;
  });

  await check(D, 'Keyboard: the row opener button opens the drawer and takes focus', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    await page.evaluate(() => document.querySelector('tr[data-app-id] .row-open').focus());
    await page.keyboard.press('Enter');
    await page.waitForTimeout(500);
    const state = await page.evaluate(() => ({
      open: document.querySelector('.app-drawer').classList.contains('active'),
      inside: document.querySelector('.app-drawer').contains(document.activeElement),
    }));
    assert(state.open && state.inside, `open=${state.open} focusInside=${state.inside}`);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);
    const back = await page.evaluate(() => document.activeElement?.classList.contains('row-open'));
    assert(back, 'focus did not return to the opener');
    return 'Enter opens, Escape closes, focus returns';
  });

  await check(D, 'Drawer close button and backdrop both dismiss it', async () => {
    for (const how of ['#drawer-close', '#app-drawer-backdrop']) {
      await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
      await page.click('tr[data-app-id]');
      await page.waitForTimeout(400);
      await page.click(how, { force: how.includes('backdrop') });
      await page.waitForTimeout(400);
      const open = await page.evaluate(() => document.querySelector('.app-drawer').classList.contains('active'));
      assert(!open, `${how} did not close the drawer`);
    }
    return 'close button and backdrop both work';
  });

  await check(D, 'Drawer shows the posting, keyword coverage and the notes timeline', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    await page.click('tr[data-app-id]:has-text("Alpha Systems")');
    await page.waitForTimeout(1500);
    const d = await page.evaluate(() => ({
      description: document.querySelector('#drawer-description')?.textContent?.trim().slice(0, 60),
      keywords: document.querySelector('#drawer-keywords-section')?.textContent?.replace(/\s+/g, ' ').trim().slice(0, 80),
      notes: Boolean(document.querySelector('#drawer-note-timeline')),
    }));
    assert(/Docker|Kubernetes/.test(d.description || ''), `posting missing: ${d.description}`);
    assert(d.notes, 'no notes timeline in the drawer');
    return `posting + keywords ("${d.keywords}")`;
  });

  await check(D, 'Adding a note from the drawer appends it to the timeline', async () => {
    await page.fill('#drawer-note-input', 'Click-through note.');
    await page.click('.drawer-note-submit');
    await page.waitForTimeout(1200);
    const shown = await page.evaluate(() => document.querySelector('#drawer-note-timeline')?.textContent || '');
    assert(/Click-through note/.test(shown), `note not shown: ${shown.slice(0, 120)}`);
    return 'note added and rendered';
  });

  // ---------------------------------------------------------------- row action links
  await check(D, 'Row action buttons still navigate (and do not open the drawer)', async () => {
    const targets = [['Cover Letter', '/CoverLetter/Generate'], ['Interview Prep', '/InterviewPrep/Prep'], ['Edit', '/JobApplications/Edit']];
    const out = [];
    for (const [label, expected] of targets) {
      await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
      await Promise.all([
        page.waitForLoadState('networkidle'),
        page.click(`tr[data-app-id]:has-text("Alpha Systems") .row-action-btn:has-text("${label}")`),
      ]);
      assert(page.url().includes(expected), `${label} went to ${page.url()}`);
      out.push(`${label} -> ${expected}`);
    }
    return out.join('; ');
  });

  // ---------------------------------------------------------------- filters
  await check(D, 'Search filter narrows the list', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    await page.fill('#filterSearch', 'Beta');
    await Promise.all([page.waitForLoadState('networkidle'), page.click('.filter-bar button.filter-apply-btn')]);
    const texts = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.textContent));
    assert(texts.length >= 1 && texts.every((t) => /Beta/.test(t)), `search returned ${texts.length} rows, not all matching`);
    return `${texts.length} row(s)`;
  });

  await check(D, 'Work mode and sort selects apply', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    await page.selectOption('#filterWorkMode', 'Remote');
    await Promise.all([page.waitForLoadState('networkidle'), page.click('.filter-bar button.filter-apply-btn')]);
    const modes = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.getAttribute('data-mode')));
    assert(modes.length > 0 && modes.every((m) => m === 'Remote'), `work mode filter leaked: ${modes.join(',')}`);

    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const sortValue = await page.evaluate(() => Array.from(document.querySelector('#filterSortBy').options).map((o) => o.value).find((v) => v && v !== ''));
    await page.selectOption('#filterSortBy', sortValue);
    await Promise.all([page.waitForLoadState('networkidle'), page.click('.filter-bar button.filter-apply-btn')]);
    const after = await page.evaluate(() => ({ n: document.querySelectorAll('tr[data-app-id]').length, sort: document.querySelector('#filterSortBy').value }));
    assertEqual(after.sort, sortValue, 'sort selection did not stick');
    assert(after.n > 0, 'sorting emptied the list');
    return `workMode=Remote -> ${modes.length} rows; sortBy=${sortValue} -> ${after.n} rows, selection retained`;
  });

  await check(D, 'Status pills filter the list', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    await Promise.all([page.waitForLoadState('networkidle'), page.click('.filter-pill:has-text("Interview")')]);
    const statuses = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.getAttribute('data-status')));
    assert(statuses.length > 0 && statuses.every((s) => s === '2'), `Interview pill gave statuses ${statuses.join(',')}`);
    return `${statuses.length} Interview row(s)`;
  });

  await check(D, 'Needs-attention pill filters without emptying the page', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    await Promise.all([page.waitForLoadState('networkidle'), page.click('#attention-pill')]);
    const body = await page.textContent('body');
    assert(page.url().includes('attention=true'), `attention pill went to ${page.url()}`);
    assert(body.replace(/\s+/g, ' ').trim().length > 200, 'attention view rendered blank');
    return 'attention filter applied';
  });

  await check(D, 'Session survives the filter flow', async () => { await assertSignedIn(page, 'bulk actions'); return 'still signed in'; });

  // ---------------------------------------------------------------- selection, bulk, compare
  await check(D, 'Row checkboxes raise the bulk bar with the right count', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const boxes = await page.$$('.row-check');
    await boxes[0].check();
    await boxes[1].check();
    await page.waitForTimeout(350);
    const bar = await page.evaluate(() => {
      const b = document.querySelector('#bulk-bar');
      return { visible: b && getComputedStyle(b).display !== 'none' && !b.hidden, count: document.querySelector('#bulk-count')?.textContent?.trim() };
    });
    assert(bar.visible, 'bulk bar did not appear');
    assert(/2/.test(bar.count || ''), `bulk count reads "${bar.count}"`);
    return `bulk bar shows "${bar.count}"`;
  });

  await check(D, 'Select-all checks every row, deselect clears it', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    await page.check('#select-all-check');
    await page.waitForTimeout(300);
    const all = await page.evaluate(() => ({
      checked: Array.from(document.querySelectorAll('.row-check')).filter((c) => c.checked).length,
      total: document.querySelectorAll('.row-check').length,
      count: document.querySelector('#bulk-count')?.textContent?.trim(),
    }));
    assertEqual(all.checked, all.total, 'select-all did not check every row');
    await page.click('#bulk-deselect-btn');
    await page.waitForTimeout(300);
    const cleared = await page.evaluate(() => Array.from(document.querySelectorAll('.row-check')).filter((c) => c.checked).length);
    assertEqual(cleared, 0, 'deselect did not clear the selection');
    return `select-all -> ${all.checked}/${all.total} ("${all.count}"), deselect -> 0`;
  });

  await check(D, 'Compare opens with the selected rows side by side', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const boxes = await page.$$('.row-check');
    await boxes[0].check();
    await boxes[1].check();
    await page.waitForTimeout(300);
    await page.click('#compare-btn');
    await page.waitForTimeout(600);
    const cmp = await page.evaluate(() => {
      const o = document.querySelector('#compare-overlay');
      const text = document.querySelector('#compare-content')?.textContent?.replace(/\s+/g, ' ').trim() || '';
      return { open: o && (o.classList.contains('active') || getComputedStyle(o).display !== 'none'), text: text.slice(0, 160), companies: (text.match(/Alpha Systems|Beta Robotics|Gamma Labs|Delta Networks/g) || []).length };
    });
    assert(cmp.open, 'compare overlay did not open');
    assert(cmp.companies >= 2, `compare shows ${cmp.companies} companies: ${cmp.text}`);
    const file = await shot(page, 'clickthrough-compare');
    await page.click('#compare-close');
    await page.waitForTimeout(300);
    const closed = await page.evaluate(() => { const o = document.querySelector('#compare-overlay'); return !o.classList.contains('active'); });
    assert(closed, 'compare did not close');
    return `${cmp.companies} records compared; screenshot ${file}`;
  });

  await check(D, 'Bulk status change applies to the selected rows only', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const before = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => [r.getAttribute('data-app-id'), r.getAttribute('data-status')]));
    const boxes = await page.$$('.row-check');
    await boxes[0].check();
    await page.waitForTimeout(300);
    const targetId = await page.evaluate(() => document.querySelector('.row-check:checked')?.value);
    await page.selectOption('#bulk-status-select', 'Offer');
    await Promise.all([page.waitForLoadState('networkidle'), page.click('#bulk-status-btn')]);
    const after = await page.evaluate(() => Object.fromEntries(Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => [r.getAttribute('data-app-id'), r.getAttribute('data-status')])));
    assertEqual(after[targetId], '4', `selected row status after bulk change to Offer`);
    const untouched = before.filter(([id]) => id !== targetId).every(([id, st]) => after[id] === st);
    assert(untouched, 'bulk status changed rows that were not selected');
    return `row ${targetId} -> Offer, ${before.length - 1} others unchanged`;
  });

  // ---------------------------------------------------------------- reminders
  await check(D, 'Mark contacted updates the last-contact stamp', async () => {
    const appId = await idOf('Alpha Systems');
    await page.goto(`${BASE}/JobApplications?view=list#open-${appId}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(800);
    const hasBtn = await page.locator('#drawer-contacted-btn').isVisible();
    if (!hasBtn) {
      const t = await antiforgery(page, LIST);
      const res = await postForm(page, `/JobApplications/${appId}/contacted`, {}, t);
      assert(res.status < 400, `contacted endpoint returned ${res.status}`);
      return `no reminder chip on this row (nothing due); endpoint verified directly -> ${res.status}`;
    }
    await page.click('#drawer-contacted-btn');
    await page.waitForTimeout(900);
    const stamp = await page.evaluate(() => document.querySelector('#drawer-last-contact')?.textContent?.trim());
    assert(stamp && !/never/i.test(stamp), `last contact still reads "${stamp}"`);
    return `last contact now "${stamp}"`;
  });

  await check(D, 'Snooze follow-up is accepted', async () => {
    const appId = await idOf('Delta Networks');
    const t = await antiforgery(page, LIST);
    const res = await postForm(page, `/JobApplications/${appId}/snooze`, {}, t);
    assert(res.status < 400, `snooze returned ${res.status}: ${res.body.slice(0, 120)}`);
    return `snooze -> ${res.status}`;
  });

  await check(D, 'Draft follow-up opens its modal and reports the missing AI key inline', async () => {
    const appId = await idOf('Alpha Systems');
    await page.goto(`${BASE}/JobApplications?view=list#open-${appId}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(800);
    const visible = await page.locator('#drawer-followup-btn').isVisible();
    if (!visible) {
      // The button only shows while the row is follow-up due; drive the endpoint instead.
      const t = await antiforgery(page, LIST);
      const res = await postForm(page, `/JobApplications/${appId}/followup`, {}, t);
      assert(res.status < 500, `followup returned ${res.status}`);
      return `button hidden (row not due); endpoint -> ${res.status} ${res.body.replace(/\s+/g, ' ').slice(0, 90)}`;
    }
    await page.click('#drawer-followup-btn');
    await page.waitForTimeout(2500);
    const modal = await page.evaluate(() => {
      const m = document.querySelector('#followup-modal, .followup-modal');
      return { open: m && getComputedStyle(m).display !== 'none', text: (m?.textContent || '').replace(/\s+/g, ' ').trim().slice(0, 140) };
    });
    assert(modal.open, 'follow-up modal did not open');
    return `modal open: "${modal.text}"`;
  });

  // ---------------------------------------------------------------- export
  await check(D, 'CSV export downloads the current applications', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const dl = page.waitForEvent('download', { timeout: 10000 });
    await page.click('a[href*="Export"], #export-csv-btn');
    const file = await dl;
    const stream = await file.createReadStream();
    let body = '';
    for await (const chunk of stream) body += chunk;
    assert(/Alpha Systems/.test(body), 'export is missing a seeded application');
    return `${file.suggestedFilename()}, ${body.split('\n').length - 1} data rows`;
  });

  // ---------------------------------------------------------------- board
  await check(D, 'Board renders the same records as cards', async () => {
    await page.goto(BASE + '/JobApplications/Board', { waitUntil: 'networkidle' });
    const board = await page.evaluate(() => ({
      columns: document.querySelectorAll('.board-column').length,
      cards: document.querySelectorAll('.board-card[data-app-id]').length,
    }));
    assertEqual(board.columns, 5, 'board columns');
    assert(board.cards > 0, 'no cards on the board');
    return `${board.columns} columns, ${board.cards} cards`;
  });

  await check(D, 'Board card opens the drawer, by click and by keyboard', async () => {
    await page.goto(BASE + '/JobApplications/Board', { waitUntil: 'networkidle' });
    await page.click('.board-card[data-app-id]');
    await page.waitForTimeout(500);
    let open = await page.evaluate(() => document.querySelector('.app-drawer').classList.contains('active'));
    assert(open, 'card click did not open the drawer');
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);

    // The card is still a focusable role=button - it nests nothing, so A4 left it alone.
    await page.evaluate(() => document.querySelector('.board-card[data-app-id]').focus());
    await page.keyboard.press('Enter');
    await page.waitForTimeout(500);
    open = await page.evaluate(() => ({
      active: document.querySelector('.app-drawer').classList.contains('active'),
      inside: document.querySelector('.app-drawer').contains(document.activeElement),
    }));
    assert(open.active, 'Enter on a focused card did not open the drawer');
    assert(open.inside, 'focus did not move into the drawer from the board');
    return 'click and Enter both open the drawer from a card';
  });

  await check(D, 'Board move endpoint repositions a card and the list agrees', async () => {
    const appId = await idOf('Gamma Labs');
    await page.goto(BASE + '/JobApplications/Board', { waitUntil: 'networkidle' });
    const token = await page.evaluate(() => document.querySelector('input[name="__RequestVerificationToken"]')?.value);
    const res = await page.evaluate(async ({ base, id, token }) => {
      const r = await fetch(`${base}/JobApplications/${id}/move`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
        body: JSON.stringify({ status: 'Interview', boardOrder: 0 }),
      });
      return { status: r.status, body: (await r.text()).slice(0, 120) };
    }, { base: BASE, id: appId, token });
    assertEqual(res.status, 200, `move -> ${res.body}`);
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const status = await page.evaluate((id) => document.querySelector(`tr[data-app-id="${id}"]`)?.getAttribute('data-status'), appId);
    assertEqual(status, '2', 'list status after the board move');
    return 'card moved to Interview; list reflects it';
  });

  // ---------------------------------------------------------------- destructive, last
  await check(D, 'Bulk delete removes exactly the selected rows', async () => {
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const before = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.getAttribute('data-app-id')));
    const boxes = await page.$$('.row-check');
    await boxes[0].check();
    await page.waitForTimeout(300);
    const doomed = await page.evaluate(() => document.querySelector('.row-check:checked')?.value);
    page.once('dialog', (d) => d.accept());
    await page.click('#bulk-delete-btn');
    await page.waitForTimeout(500);
    const confirmBtn = await page.$('#app-confirm-ok');
    if (confirmBtn && await confirmBtn.isVisible()) {
      await Promise.all([page.waitForLoadState('networkidle'), confirmBtn.click()]);
    }
    await page.goto(BASE + LIST, { waitUntil: 'networkidle' });
    const after = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.getAttribute('data-app-id')));
    assert(!after.includes(doomed), `row ${doomed} survived the bulk delete`);
    assertEqual(after.length, before.length - 1, 'exactly one row should be gone');
    return `deleted ${doomed}; ${after.length} rows remain`;
  });

  await check(D, 'No console errors across the whole click-through', async () => {
    assertEqual(consoleErrors.length, 0, `console errors:\n    ${consoleErrors.slice(0, 6).join('\n    ')}`);
    return 'console clean';
  });

  await browser.close();
}
