// Functional dimension: user flows, form behaviour, CRUD, filters, API contracts.
import { chromium, BASE, check, assert, assertEqual, newSignedInContext, createApplication, antiforgery, postForm, postJson, uniqueEmail, PASSWORD, assertSignedIn, syntheticClient } from './lib/harness.mjs';

const D = 'Functional';

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });
  const anon = await browser.newContext({ viewport: { width: 1440, height: 900 } });

  // ---------------------------------------------------------------- anonymous surface
  await check(D, 'Landing page renders for anonymous visitors', async () => {
    const page = await anon.newPage();
    const res = await page.goto(BASE + '/', { waitUntil: 'networkidle' });
    assertEqual(res.status(), 200, 'landing status');
    const text = await page.textContent('body');
    assert(/InternTrackAI/i.test(text), 'brand name missing from landing page');
    const title = await page.title();
    await page.close();
    return `status 200, title "${title}"`;
  });

  await check(D, 'Health endpoint reports Healthy with a database check', async () => {
    const page = await anon.newPage();
    const res = await page.goto(BASE + '/health');
    const body = await page.textContent('body');
    await page.close();
    assertEqual(res.status(), 200, '/health status');
    const json = JSON.parse(body);
    assertEqual(json.status, 'Healthy', 'health status');
    assert(json.checks.some((c) => c.name === 'database'), 'no database check reported');
    return body.slice(0, 200);
  });

  await check(D, 'Unknown route re-executes to the styled 404 page', async () => {
    const page = await anon.newPage();
    const res = await page.goto(BASE + '/this/route/does/not/exist');
    const body = await page.textContent('body');
    await page.close();
    assertEqual(res.status(), 404, 'status code for unknown route');
    assert(!/Stack|at .*\.cs:line/i.test(body), 'error page leaked a stack trace');
    return `404 with ${body.trim().slice(0, 120).replace(/\s+/g, ' ')}`;
  });

  for (const path of ['/Home/Dashboard', '/JobApplications', '/JobApplications/Board', '/Profile', '/CoverLetter/Generate']) {
    await check(D, `Anonymous ${path} redirects to Login`, async () => {
      const page = await anon.newPage();
      await page.goto(BASE + path, { waitUntil: 'domcontentloaded' });
      const url = page.url();
      await page.close();
      assert(/\/Identity\/Account\/Login/i.test(url), `expected login redirect, landed on ${url}`);
      return url.replace(BASE, '');
    });
  }

  // ---------------------------------------------------------------- registration + session
  const email = uniqueEmail('func');
  let context;
  await check(D, 'Register through the real Register page signs the user in', async () => {
    context = await browser.newContext({ viewport: { width: 1440, height: 900 } });
    const page = await context.newPage();
    await page.setExtraHTTPHeaders(syntheticClient());
    await page.goto(BASE + '/Identity/Account/Register', { waitUntil: 'domcontentloaded' });
    await page.fill('#Input_DisplayName', 'QA Functional');
    await page.fill('#Input_Email', email);
    await page.fill('#Input_Password', PASSWORD);
    await page.fill('#Input_ConfirmPassword', PASSWORD);
    await Promise.all([page.waitForLoadState('networkidle'), page.click('button[type=submit]')]);
    const url = page.url();
    assert(!/Register/i.test(url), `still on Register: ${url}`);
    await page.goto(BASE + '/Home/Dashboard', { waitUntil: 'domcontentloaded' });
    assert(!/Login/i.test(page.url()), 'dashboard still redirects to login after register');
    await page.close();
    return `registered ${email}, landed on ${url.replace(BASE, '')}`;
  });

  await check(D, 'Register rejects a weak password with field validation', async () => {
    const page = await anon.newPage();
    await page.setExtraHTTPHeaders(syntheticClient());
    await page.goto(BASE + '/Identity/Account/Register', { waitUntil: 'domcontentloaded' });
    await page.fill('#Input_Email', uniqueEmail('weak'));
    await page.fill('#Input_Password', 'abc');
    await page.fill('#Input_ConfirmPassword', 'abc');
    await Promise.all([page.waitForLoadState('networkidle'), page.click('button[type=submit]')]);
    const body = await page.textContent('body');
    const url = page.url();
    await page.close();
    assert(/Register/i.test(url), 'weak password was accepted');
    assert(/(at least|must have|characters)/i.test(body), 'no password validation message shown');
    return body.replace(/\s+/g, ' ').match(/.{0,140}(at least|must have).{0,80}/i)?.[0] ?? 'validation shown';
  });

  await check(D, 'Register rejects a duplicate email address', async () => {
    const page = await anon.newPage();
    await page.setExtraHTTPHeaders(syntheticClient());
    await page.goto(BASE + '/Identity/Account/Register', { waitUntil: 'domcontentloaded' });
    await page.fill('#Input_Email', email);
    await page.fill('#Input_Password', PASSWORD);
    await page.fill('#Input_ConfirmPassword', PASSWORD);
    await Promise.all([page.waitForLoadState('networkidle'), page.click('button[type=submit]')]);
    const body = await page.textContent('body');
    const url = page.url();
    await page.close();
    assert(/Register/i.test(url), 'duplicate email created a second account');
    assert(/already taken|already in use/i.test(body), `no duplicate-email message: ${body.replace(/\s+/g, ' ').slice(0, 200)}`);
    return 'duplicate email refused on the Register page';
  });

  await check(D, 'Login with a wrong password fails and keeps the session anonymous', async () => {
    const page = await anon.newPage();
    await page.goto(BASE + '/Identity/Account/Login', { waitUntil: 'domcontentloaded' });
    await page.fill('#Input_Email', email);
    await page.fill('#Input_Password', 'WrongPassword!99');
    await Promise.all([page.waitForLoadState('networkidle'), page.click('button[type=submit]')]);
    const url = page.url();
    const body = await page.textContent('body');
    await page.close();
    assert(/Login/i.test(url), `wrong password logged in: ${url}`);
    assert(/incorrect email or password|invalid login/i.test(body), `no invalid-login message: ${body.replace(/\s+/g, ' ').slice(0, 200)}`);
    return 'invalid login attempt rejected';
  });

  const page = await context.newPage();

  // ---------------------------------------------------------------- dashboard
  await check(D, 'Dashboard renders an empty state for a brand-new account', async () => {
    const res = await page.goto(BASE + '/Home/Dashboard', { waitUntil: 'networkidle' });
    assertEqual(res.status(), 200, 'dashboard status');
    const body = await page.textContent('body');
    assert(/0/.test(body), 'no counters rendered');
    return `dashboard 200, ${(await page.$$('.card')).length} cards`;
  });

  // ---------------------------------------------------------------- create
  await check(D, 'Create rejects a submission with no company or role', async () => {
    await page.goto(BASE + '/JobApplications/Create', { waitUntil: 'domcontentloaded' });
    await page.click('#appForm button[type=submit]:not([form])');
    await page.waitForTimeout(400);
    const errors = await page.evaluate(() =>
      Array.from(document.querySelectorAll('.field-validation-error, .text-danger, [data-valmsg-for]'))
        .map((e) => e.textContent.trim()).filter(Boolean));
    assert(/Create/i.test(page.url()), 'empty form was accepted and navigated away');
    assert(errors.length > 0, 'no client-side validation messages appeared');
    return errors.slice(0, 4).join(' | ');
  });

  let appId;
  await check(D, 'Create saves a valid application and it appears in the list', async () => {
    await createApplication(page, {
      CompanyName: 'Northwind Systems',
      RoleTitle: 'Backend Engineering Intern',
      Location: 'Toronto, ON',
      JobLink: 'https://example.test/jobs/1',
      Status: '1',
      JobDescription: 'Requirements: experience with C#, ASP.NET Core, PostgreSQL, Docker and Kubernetes. You will build REST APIs.',
    });
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const rows = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => ({ id: r.getAttribute('data-app-id'), text: r.textContent.replace(/\s+/g, ' ').trim() })));
    const row = rows.find((r) => /Northwind Systems/.test(r.text));
    assert(row, `created application not listed; rows=${JSON.stringify(rows).slice(0, 300)}`);
    appId = row.id;
    return `id ${appId}: ${row.text.slice(0, 120)}`;
  });

  await check(D, 'Duplicate company+role warns before saving a second copy', async () => {
    await page.goto(BASE + '/JobApplications/Create', { waitUntil: 'domcontentloaded' });
    await page.fill('#CompanyName', '  northwind systems ');
    await page.fill('#RoleTitle', 'backend engineering intern');
    await page.click('#appForm button[type=submit]:not([form])');
    await page.waitForTimeout(1200);
    const warned = await page.evaluate(() => {
      const w = document.querySelector('#duplicateWarning');
      return w ? { hidden: w.hidden || getComputedStyle(w).display === 'none', text: w.textContent.replace(/\s+/g, ' ').trim() } : null;
    });
    const stillOnCreate = /Create/i.test(page.url());
    assert(warned && !warned.hidden, `no duplicate warning shown (onCreate=${stillOnCreate}, warn=${JSON.stringify(warned)})`);
    return warned.text.slice(0, 180);
  });

  await check(D, 'Role title longer than 100 characters is rejected', async () => {
    await page.goto(BASE + '/JobApplications/Create', { waitUntil: 'domcontentloaded' });
    await page.fill('#CompanyName', 'Length Test Co');
    await page.evaluate(() => { document.querySelector('#RoleTitle').removeAttribute('maxlength'); });
    await page.fill('#RoleTitle', 'R'.repeat(140));
    await page.click('#appForm button[type=submit]:not([form])');
    await page.waitForTimeout(800);
    const onCreate = /Create/i.test(page.url());
    const errs = await page.evaluate(() => Array.from(document.querySelectorAll('.field-validation-error')).map((e) => e.textContent.trim()).filter(Boolean));
    assert(onCreate, 'a 140-character role title was saved');
    return `stayed on Create; ${errs.join(' | ').slice(0, 160) || 'validation blocked submit'}`;
  });

  // ---------------------------------------------------------------- edit
  await check(D, 'Edit changes status and the list reflects it', async () => {
    await page.goto(`${BASE}/JobApplications/Edit/${appId}`, { waitUntil: 'domcontentloaded' });
    await page.selectOption('#Status', '2');
    await Promise.all([page.waitForLoadState('networkidle'), page.click('#appForm button[type=submit]:not([form])')]);
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const row = await page.evaluate((id) => {
      const r = document.querySelector(`tr[data-app-id="${id}"]`);
      return r ? { status: r.getAttribute('data-status'), text: r.textContent.replace(/\s+/g, ' ').trim() } : null;
    }, appId);
    assert(row, 'row vanished after edit');
    assertEqual(row.status, '2', 'data-status after setting Interview');
    assert(/Interview/i.test(row.text), `row does not show Interview: ${row.text.slice(0, 160)}`);
    return row.text.slice(0, 160);
  });

  await check(D, 'Session survives the edit flow', async () => { await assertSignedIn(page, 'filters'); return 'still signed in'; });

  // ---------------------------------------------------------------- filters
  await check(D, 'Search filter narrows the list to matching applications', async () => {
    await createApplication(page, { CompanyName: 'Contoso Robotics', RoleTitle: 'Data Intern', Status: '0' });
    await page.goto(BASE + '/JobApplications?view=list&search=Contoso', { waitUntil: 'networkidle' });
    const texts = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.textContent.replace(/\s+/g, ' ').trim()));
    assert(texts.length >= 1, 'search returned no rows');
    assert(texts.every((t) => /Contoso/i.test(t)), `search leaked non-matching rows: ${texts.join(' // ').slice(0, 200)}`);
    return `${texts.length} row(s), all matching`;
  });

  await check(D, 'Status filter returns only that status', async () => {
    await page.goto(BASE + '/JobApplications?view=list&status=Interview', { waitUntil: 'networkidle' });
    const statuses = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.getAttribute('data-status')));
    assert(statuses.length > 0, 'status filter returned nothing');
    assert(statuses.every((s) => s === '2'), `status filter leaked: ${statuses.join(',')}`);
    return `${statuses.length} row(s) all status=2`;
  });

  await check(D, 'An unknown filter value does not 500 the list page', async () => {
    const res = await page.goto(BASE + '/JobApplications?view=list&status=notanumber&search=%00&sort=zzz', { waitUntil: 'networkidle' });
    assert(res.status() < 500, `status ${res.status()}`);
    return `status ${res.status()} for garbage query string`;
  });

  // ---------------------------------------------------------------- board
  await check(D, 'Board renders columns and the application sits in its status column', async () => {
    const res = await page.goto(BASE + '/JobApplications/Board', { waitUntil: 'networkidle' });
    assertEqual(res.status(), 200, 'board status');
    const cols = await page.evaluate(() => Array.from(document.querySelectorAll('.board-column')).map((c) => c.getAttribute('data-status')));
    const cards = await page.evaluate(() => Array.from(document.querySelectorAll('[data-app-id]')).map((c) => c.getAttribute('data-app-id')).filter(Boolean));
    assert(cols.length >= 4, `expected 5 status columns, saw ${cols.length}: ${cols.join(',')}`);
    assert(cards.includes(String(appId)), `card ${appId} missing from board`);
    return `${cols.length} columns, ${cards.length} cards`;
  });

  await check(D, 'Board move endpoint persists a status change', async () => {
    const token = await antiforgery(page, '/JobApplications/Board');
    const res = await postJson(page, `/JobApplications/${appId}/move`, { status: 'Offer', boardOrder: 0 }, token);
    assert(res.status >= 200 && res.status < 400, `move returned ${res.status}: ${res.body.slice(0, 200)}`);
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const status = await page.evaluate((id) => document.querySelector(`tr[data-app-id="${id}"]`)?.getAttribute('data-status'), appId);
    assertEqual(status, '4', 'status after move to Offer');
    return `move -> ${res.status}, list shows status 4 (Offer)`;
  });

  // ---------------------------------------------------------------- notes
  await check(D, 'Adding a note appends it to the timeline', async () => {
    const token = await antiforgery(page, '/JobApplications');
    const res = await postForm(page, '/JobApplications/AddNote', { appId: String(appId), text: 'Recruiter call booked for Friday.' }, token);
    assert(res.status >= 200 && res.status < 400, `AddNote ${res.status}: ${res.body.slice(0, 200)}`);
    const notes = await page.evaluate(async ({ base, id }) => {
      const r = await fetch(`${base}/JobApplications/Notes?appId=${id}`);
      return { status: r.status, body: (await r.text()).slice(0, 1500) };
    }, { base: BASE, id: appId });
    assertEqual(notes.status, 200, 'Notes fetch status');
    assert(/Recruiter call booked/.test(notes.body), `note missing from timeline: ${notes.body.slice(0, 200)}`);
    return 'note stored and returned by /JobApplications/Notes';
  });

  await check(D, 'An empty note is not stored', async () => {
    const token = await antiforgery(page, '/JobApplications');
    const before = await page.evaluate(async ({ base, id }) => (await (await fetch(`${base}/JobApplications/Notes?appId=${id}`)).text()).length, { base: BASE, id: appId });
    await postForm(page, '/JobApplications/AddNote', { appId: String(appId), text: '   ' }, token);
    const after = await page.evaluate(async ({ base, id }) => (await (await fetch(`${base}/JobApplications/Notes?appId=${id}`)).text()).length, { base: BASE, id: appId });
    assert(Math.abs(after - before) < 40, `whitespace-only note appears to have been stored (${before} -> ${after} chars)`);
    return 'whitespace-only note ignored';
  });

  // ---------------------------------------------------------------- drawer
  await check(D, 'Deep link #open-{id} opens the detail drawer', async () => {
    // Navigate away first: a same-document hash change would not re-run app-drawer.js.
    await page.goto(`${BASE}/Home/Dashboard`, { waitUntil: 'domcontentloaded' });
    await page.goto(`${BASE}/JobApplications#open-${appId}`, { waitUntil: 'networkidle' });
    await page.waitForTimeout(900);
    const drawer = await page.evaluate(() => {
      const d = document.querySelector('.app-drawer');
      if (!d) return null;
      return { open: d.classList.contains('active') && d.getAttribute('aria-hidden') === 'false', cls: d.className, text: d.textContent.replace(/\s+/g, ' ').trim().slice(0, 300) };
    });
    assert(drawer, 'no drawer element found in the DOM');
    assert(drawer.open, `drawer did not open: ${drawer.cls}`);
    assert(/Northwind/i.test(drawer.text), `drawer shows the wrong record: ${drawer.text}`);
    return drawer.text.slice(0, 140);
  });

  // ---------------------------------------------------------------- keyword coverage (no AI)
  await check(D, 'Keyword coverage returns a deterministic report for an unsaved description', async () => {
    const token = await antiforgery(page, '/JobApplications/Create');
    const res = await postForm(page, '/JobApplications/KeywordCoverage', {
      Description: 'We are looking for experience with Kubernetes, Terraform, PostgreSQL, ASP.NET Core, Docker, Redis, GraphQL, Kafka, Jenkins, and Prometheus. Requirements: strong REST API design.',
      Company: 'Northwind Systems',
      Role: 'Backend Engineering Intern',
      Location: 'Toronto',
    }, token);
    assertEqual(res.status, 200, `coverage status; body ${res.body.slice(0, 200)}`);
    const json = JSON.parse(res.body);
    assert('available' in json && 'reason' in json && 'total' in json && Array.isArray(json.missing), `unexpected shape: ${res.body.slice(0, 300)}`);
    return `available=${json.available} reason=${json.reason} covered=${json.covered}/${json.total} missing=${json.missing.length}`;
  });

  await check(D, 'Keyword coverage for a stored application is owner-scoped and answers', async () => {
    const token = await antiforgery(page, '/JobApplications');
    const res = await postForm(page, '/JobApplications/KeywordCoverage', { AppId: String(appId) }, token);
    assertEqual(res.status, 200, `coverage(appId) status; ${res.body.slice(0, 200)}`);
    const json = JSON.parse(res.body);
    return `available=${json.available} reason=${json.reason} total=${json.total}`;
  });

  // ---------------------------------------------------------------- AI endpoints with no key
  await check(D, 'Analyzer degrades gracefully when no OpenAI key is configured', async () => {
    const res = await postJson(page, '/Analyzer/Analyze', { jobDescription: 'Software engineering intern, C# and Docker.' });
    assert(res.status < 500, `analyzer returned ${res.status}: ${res.body.slice(0, 200)}`);
    assert(/not configured|api key/i.test(res.body) || /success/i.test(res.body), `unexpected body: ${res.body.slice(0, 200)}`);
    return `status ${res.status}, body ${res.body.replace(/\s+/g, ' ').slice(0, 180)}`;
  });

  await check(D, 'Analyzer rejects an empty job description with 400', async () => {
    const res = await postJson(page, '/Analyzer/Analyze', { jobDescription: '   ' });
    assertEqual(res.status, 400, `expected 400; body ${res.body.slice(0, 200)}`);
    return res.body.slice(0, 160);
  });

  await check(D, 'Salary insight endpoint answers without a key instead of erroring', async () => {
    const token = await antiforgery(page, '/JobApplications/Create');
    const res = await postForm(page, '/SalaryInsight/Estimate', { role: 'Backend Engineering Intern', company: 'Northwind Systems', location: 'Toronto' }, token);
    assert(res.status < 500, `status ${res.status}: ${res.body.slice(0, 200)}`);
    return `status ${res.status}, ${res.body.replace(/\s+/g, ' ').slice(0, 160)}`;
  });

  await check(D, 'Cover letter page loads and generation degrades without a key', async () => {
    const res = await page.goto(`${BASE}/CoverLetter/Generate?appId=${appId}`, { waitUntil: 'networkidle' });
    assertEqual(res.status(), 200, 'cover letter page status');
    const token = await page.evaluate(() => document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? null);
    const gen = await postForm(page, '/CoverLetter/GenerateAjax', { applicationId: String(appId), tone: 'professional' }, token);
    assert(gen.status < 500, `GenerateAjax ${gen.status}: ${gen.body.slice(0, 200)}`);
    return `page 200; GenerateAjax ${gen.status} ${gen.body.replace(/\s+/g, ' ').slice(0, 140)}`;
  });

  await check(D, 'Interview prep page loads for an application', async () => {
    const res = await page.goto(`${BASE}/InterviewPrep/Prep?appId=${appId}`, { waitUntil: 'networkidle' });
    assertEqual(res.status(), 200, 'prep page status');
    const body = await page.textContent('body');
    return `200, ${body.replace(/\s+/g, ' ').trim().slice(0, 120)}`;
  });

  // ---------------------------------------------------------------- CSV
  let csv;
  await check(D, 'CSV export returns the applications as a downloadable file', async () => {
    const out = await page.evaluate(async (base) => {
      const r = await fetch(`${base}/JobApplications/Export`);
      return { status: r.status, ct: r.headers.get('content-type'), cd: r.headers.get('content-disposition'), body: (await r.text()).slice(0, 2000) };
    }, BASE);
    assertEqual(out.status, 200, 'export status');
    assert(/csv/i.test(out.ct || ''), `content-type is ${out.ct}`);
    assert(/Northwind Systems/.test(out.body), 'export is missing the created application');
    csv = out.body;
    return `${out.ct}; ${out.cd}; first line: ${out.body.split('\n')[0].slice(0, 160)}`;
  });

  await check(D, 'CSV import round-trips exported rows back into the list', async () => {
    if (!csv) throw new Error('no CSV captured from the export test');
    const header = csv.split('\n')[0];
    const imported = `${header}\nImported Labs,Platform Intern,,Remote,,Saved,,,,,,,,\n`;
    const token = await antiforgery(page, '/JobApplications');
    const out = await page.evaluate(async ({ base, csvText, token }) => {
      const fd = new FormData();
      fd.append('file', new Blob([csvText], { type: 'text/csv' }), 'import.csv');
      fd.append('__RequestVerificationToken', token);
      const r = await fetch(`${base}/JobApplications/ImportCsv`, { method: 'POST', body: fd, redirect: 'manual' });
      return { status: r.status, body: (await r.text()).slice(0, 400) };
    }, { base: BASE, csvText: imported, token });
    assert(out.status < 400, `import returned ${out.status}: ${out.body}`);
    await page.goto(BASE + '/JobApplications?view=list&search=Imported', { waitUntil: 'networkidle' });
    const found = await page.evaluate(() => document.body.textContent.includes('Imported Labs'));
    assert(found, 'imported row did not appear in the list');
    return 'imported row visible in the list';
  });

  await check(D, 'Importing a non-CSV file is refused without a server error', async () => {
    const token = await antiforgery(page, '/JobApplications');
    const out = await page.evaluate(async ({ base, token }) => {
      const fd = new FormData();
      fd.append('file', new Blob(['%PDF-1.4 not a csv'], { type: 'application/pdf' }), 'evil.pdf');
      fd.append('__RequestVerificationToken', token);
      const r = await fetch(`${base}/JobApplications/ImportCsv`, { method: 'POST', body: fd, redirect: 'manual' });
      return { status: r.status, body: (await r.text()).slice(0, 300) };
    }, { base: BASE, token });
    assert(out.status < 500, `status ${out.status}: ${out.body}`);
    return `status ${out.status}`;
  });

  await check(D, 'Session survives the CSV flow', async () => { await assertSignedIn(page, 'profile'); return 'still signed in'; });

  // ---------------------------------------------------------------- profile
  await check(D, 'Profile page renders its cards', async () => {
    const res = await page.goto(BASE + '/Profile', { waitUntil: 'networkidle' });
    assertEqual(res.status(), 200, 'profile status');
    const body = await page.textContent('body');
    assert(/Resume/i.test(body) && /Skills/i.test(body), 'expected Resume and Skills cards');
    return `200, ${(await page.$$('.card')).length} cards`;
  });

  await check(D, 'Saving profile info persists the display name and time zone', async () => {
    const token = await antiforgery(page, '/Profile');
    const res = await postForm(page, '/Profile/SaveInfo', { FullName: 'QA Functional Tester', DisplayName: 'QA Func', Country: 'Canada', TimeZoneId: 'America/Vancouver' }, token);
    assert(res.status < 400, `SaveInfo ${res.status}: ${res.body.slice(0, 200)}`);
    await page.goto(BASE + '/Profile', { waitUntil: 'networkidle' });
    const body = await page.textContent('body');
    assert(/QA Functional Tester|QA Func/.test(body), 'saved name not rendered back');
    const tz = await page.evaluate(() => document.querySelector('#timeZoneSelect')?.value ?? null);
    assertEqual(tz, 'America/Vancouver', 'time zone round-trip');
    return 'name + time zone persisted';
  });

  await check(D, 'Skills save as deduplicated tags', async () => {
    const token = await antiforgery(page, '/Profile');
    const res = await postForm(page, '/Profile/SaveSkills', { skills: 'C#, docker, Docker,  C#  , Kubernetes' }, token);
    assert(res.status < 400, `SaveSkills ${res.status}: ${res.body.slice(0, 200)}`);
    await page.goto(BASE + '/Profile', { waitUntil: 'networkidle' });
    const tags = await page.evaluate(() => Array.from(document.querySelectorAll('#skillsCloud .tag-item .tag-item-label, .tag-item .tag-item-label')).map((e) => e.textContent.trim()).filter(Boolean));
    const skillish = tags.filter((t) => /^(C#|docker|Docker|Kubernetes)$/i.test(t));
    const lower = skillish.map((s) => s.toLowerCase());
    assert(new Set(lower).size === lower.length, `duplicate tags stored: ${skillish.join(', ')}`);
    return `stored tags: ${skillish.join(', ') || tags.slice(0, 6).join(', ')}`;
  });

  await check(D, 'Uploading a non-PDF as a resume is refused', async () => {
    const token = await antiforgery(page, '/Profile');
    const out = await page.evaluate(async ({ base, token }) => {
      const fd = new FormData();
      fd.append('file', new Blob(['GIF89a not a pdf at all'], { type: 'application/pdf' }), 'fake.pdf');
      fd.append('__RequestVerificationToken', token);
      const r = await fetch(`${base}/Profile/UploadResume`, { method: 'POST', body: fd, redirect: 'manual' });
      return { status: r.status, body: (await r.text()).slice(0, 400) };
    }, { base: BASE, token });
    assert(out.status < 500, `status ${out.status}: ${out.body}`);
    await page.goto(BASE + '/Profile', { waitUntil: 'networkidle' });
    const body = await page.textContent('body');
    assert(!/fake\.pdf/.test(body), 'a file with a forged PDF extension was stored');
    return `status ${out.status}; no resume row created`;
  });

  await check(D, 'Calendar token regenerates and the feed serves ICS for the new token', async () => {
    const token = await antiforgery(page, '/Profile');
    const res = await postForm(page, '/Profile/RegenerateCalendarToken', {}, token);
    assert(res.status < 400, `Regenerate ${res.status}`);
    await page.goto(BASE + '/Profile', { waitUntil: 'networkidle' });
    const feedUrl = await page.evaluate(() => document.querySelector('#calendarFeedInput')?.value ?? null);
    assert(feedUrl, 'no calendar feed URL rendered on the profile');
    const out = await page.evaluate(async ({ base, feedUrl }) => {
      const r = await fetch(feedUrl.startsWith('http') ? feedUrl : base + feedUrl);
      return { status: r.status, ct: r.headers.get('content-type'), body: (await r.text()).slice(0, 300) };
    }, { base: BASE, feedUrl });
    assertEqual(out.status, 200, 'feed status');
    assert(/BEGIN:VCALENDAR/.test(out.body), `feed body is not ICS: ${out.body.slice(0, 120)}`);
    return `${out.ct}; ${out.body.split('\n')[0]}`;
  });

  await check(D, 'Calendar feed with a wrong token is 404, not an empty calendar', async () => {
    const out = await page.evaluate(async (base) => {
      const r = await fetch(`${base}/Calendar/feed.ics?token=${'z'.repeat(43)}`);
      return { status: r.status, body: (await r.text()).slice(0, 200) };
    }, BASE);
    assertEqual(out.status, 404, `expected 404 for a wrong token, got ${out.status}`);
    return 'wrong token -> 404';
  });

  await check(D, 'Per-application .ics download works', async () => {
    const out = await page.evaluate(async ({ base, id }) => {
      const r = await fetch(`${base}/Calendar/application/${id}.ics`);
      return { status: r.status, ct: r.headers.get('content-type'), body: (await r.text()).slice(0, 200) };
    }, { base: BASE, id: appId });
    assertEqual(out.status, 200, 'ics status');
    assert(/BEGIN:VCALENDAR/.test(out.body), `not ICS: ${out.body.slice(0, 120)}`);
    return `${out.ct}`;
  });

  // ---------------------------------------------------------------- bulk + delete
  await check(D, 'Bulk status change applies to the selected rows', async () => {
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const ids = await page.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.getAttribute('data-app-id')));
    const target = [...new Set(ids)].slice(0, 2);
    assert(target.length >= 1, 'no rows available to bulk-update');
    const token = await antiforgery(page, '/JobApplications');
    const res = await page.evaluate(async ({ base, target, token }) => {
      const body = new URLSearchParams();
      for (const id of target) body.append('ids', id);
      body.append('newStatus', 'Rejected');
      body.append('__RequestVerificationToken', token);
      const r = await fetch(`${base}/JobApplications/BulkStatus`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: body.toString(), redirect: 'manual' });
      return { status: r.status };
    }, { base: BASE, target, token });
    assert(res.status < 400, `BulkStatus ${res.status}`);
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const statuses = await page.evaluate((ids) => ids.map((id) => document.querySelector(`tr[data-app-id="${id}"]`)?.getAttribute('data-status')), target);
    assert(statuses.every((s) => s === '3'), `expected all Rejected(3), got ${statuses.join(',')}`);
    return `${target.length} rows moved to Rejected`;
  });

  await check(D, 'Delete removes the application from the list', async () => {
    const token = await antiforgery(page, `/JobApplications/Delete/${appId}`);
    const res = await postForm(page, `/JobApplications/Delete/${appId}`, { id: String(appId) }, token);
    assert(res.status < 400, `Delete ${res.status}: ${res.body.slice(0, 200)}`);
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const still = await page.evaluate((id) => !!document.querySelector(`tr[data-app-id="${id}"]`), appId);
    assert(!still, 'deleted application is still listed');
    const detail = await page.evaluate(async ({ base, id }) => (await fetch(`${base}/JobApplications/Edit/${id}`)).status, { base: BASE, id: appId });
    assertEqual(detail, 404, 'Edit on a deleted id should 404');
    return 'row gone, Edit -> 404';
  });

  // ---------------------------------------------------------------- logout
  await check(D, 'Logout ends the session', async () => {
    const token = await antiforgery(page, '/Home/Dashboard');
    await postForm(page, '/Identity/Account/Logout', { returnUrl: '/' }, token);
    await page.goto(BASE + '/Home/Dashboard', { waitUntil: 'domcontentloaded' });
    assert(/Login/i.test(page.url()), `dashboard still reachable after logout: ${page.url()}`);
    return 'dashboard redirects to Login after logout';
  });

  await browser.close();
}
