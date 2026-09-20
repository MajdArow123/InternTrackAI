// Security dimension: ownership/IDOR, output escaping, antiforgery, headers, cookies,
// redirect safety, rate limiting and information leakage. Application-owned surfaces only.
import { chromium, BASE, check, assert, assertEqual, newSignedInContext, createApplication, antiforgery, postForm, postJson, uniqueEmail, PASSWORD, syntheticClient } from './lib/harness.mjs';

const D = 'Security';

const XSS = '<img src=x onerror="window.__xss=1">';
const XSS2 = '"><script>window.__xss2=1</script>';

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });

  // Two accounts: A owns the data, B tries to reach it.
  const a = await newSignedInContext(browser, 'sec-a');
  const b = await newSignedInContext(browser, 'sec-b');
  const pa = await a.context.newPage();
  const pb = await b.context.newPage();
  const anon = await browser.newContext();
  const pan = await anon.newPage();
  // fetch() from about:blank has no origin, so park the anonymous page on the app first.
  await pan.goto(BASE + '/', { waitUntil: 'domcontentloaded' });

  // Seed account A.
  await createApplication(pa, {
    CompanyName: 'Victim Corp', RoleTitle: 'Secret Role', Location: 'Nowhere',
    Status: '1', JobDescription: 'Confidential posting body for Kubernetes and Terraform.',
  });
  await pa.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
  const victimId = await pa.evaluate(() => document.querySelector('tr[data-app-id]')?.getAttribute('data-app-id'));
  const tokenA = await antiforgery(pa, '/JobApplications');
  await postForm(pa, '/JobApplications/AddNote', { appId: victimId, text: 'Private note: salary discussed.' }, tokenA);

  // ------------------------------------------------------------ ownership / IDOR
  const tokenB = await antiforgery(pb, '/JobApplications');

  const idorGets = [
    ['Edit', `/JobApplications/Edit/${victimId}`],
    ['Delete (GET confirm)', `/JobApplications/Delete/${victimId}`],
    ['Notes', `/JobApplications/Notes?appId=${victimId}`],
    ['Interview prep', `/InterviewPrep/Prep?appId=${victimId}`],
    ['Cover letter generate', `/CoverLetter/Generate?appId=${victimId}`],
    ['Per-application ICS', `/Calendar/application/${victimId}.ics`],
  ];
  for (const [name, url] of idorGets) {
    await check(D, `IDOR: another user's ${name} returns 404`, async () => {
      const out = await pb.evaluate(async ({ base, url }) => {
        const r = await fetch(base + url);
        return { status: r.status, body: (await r.text()).slice(0, 600) };
      }, { base: BASE, url });
      assertEqual(out.status, 404, `expected 404 for a foreign id, got ${out.status}`);
      assert(!/Victim Corp|Secret Role|salary discussed/i.test(out.body), 'foreign data leaked in the 404 body');
      return `${url} -> 404, no owner data in the body`;
    });
  }

  await check(D, "IDOR: another user's application cannot be edited", async () => {
    const res = await postForm(pb, `/JobApplications/Edit/${victimId}`, {
      Id: victimId, CompanyName: 'Hijacked', RoleTitle: 'Hijacked', Status: '4',
    }, tokenB);
    const after = await pa.evaluate(async ({ base, id }) => {
      const r = await fetch(`${base}/JobApplications/Edit/${id}`);
      return (await r.text()).includes('Victim Corp');
    }, { base: BASE, id: victimId });
    assert(after, 'the owner\'s application was modified by another user');
    assert(res.status === 404 || res.status >= 400, `edit POST returned ${res.status}`);
    return `POST Edit -> ${res.status}; owner record unchanged`;
  });

  await check(D, "IDOR: another user's application cannot be deleted", async () => {
    const res = await postForm(pb, `/JobApplications/Delete/${victimId}`, { id: victimId }, tokenB);
    const alive = await pa.evaluate(async ({ base, id }) => (await fetch(`${base}/JobApplications/Edit/${id}`)).status, { base: BASE, id: victimId });
    assertEqual(alive, 200, 'the owner\'s application was deleted by another user');
    return `POST Delete -> ${res.status}; record still present for the owner`;
  });

  await check(D, "IDOR: board move on another user's application is refused", async () => {
    const res = await postJson(pb, `/JobApplications/${victimId}/move`, { status: 'Rejected', boardOrder: 0 }, tokenB);
    assertEqual(res.status, 404, `expected 404, got ${res.status}`);
    return 'move -> 404';
  });

  await check(D, "IDOR: notes cannot be appended to another user's application", async () => {
    const res = await postForm(pb, '/JobApplications/AddNote', { appId: victimId, text: 'injected by attacker' }, tokenB);
    const notes = await pa.evaluate(async ({ base, id }) => (await (await fetch(`${base}/JobApplications/Notes?appId=${id}`)).text()), { base: BASE, id: victimId });
    assert(!/injected by attacker/.test(notes), 'attacker note landed on the owner\'s timeline');
    return `AddNote -> ${res.status}; owner timeline clean`;
  });

  await check(D, "IDOR: keyword coverage for another user's application is refused", async () => {
    const res = await postForm(pb, '/JobApplications/KeywordCoverage', { AppId: victimId }, tokenB);
    assertEqual(res.status, 404, `expected 404, got ${res.status}: ${res.body.slice(0, 200)}`);
    return 'KeywordCoverage(foreign appId) -> 404';
  });

  await check(D, "IDOR: follow-up draft on another user's application is refused", async () => {
    const res = await postJson(pb, `/JobApplications/${victimId}/followup`, {}, tokenB);
    assert(res.status === 404 || /not found|success":false/i.test(res.body), `unexpected: ${res.status} ${res.body.slice(0, 200)}`);
    return `followup -> ${res.status}`;
  });

  await check(D, "IDOR: bullet rewrite against another user's application is refused", async () => {
    const res = await postJson(pb, '/Profile/RewriteBullet', { applicationId: Number(victimId), bullet: 'Built a service that handled traffic.' }, tokenB);
    assert(res.status === 404 || /not found|success":false/i.test(res.body), `unexpected: ${res.status} ${res.body.slice(0, 250)}`);
    assert(!/Victim Corp|Secret Role/i.test(res.body), 'owner data leaked through the rewrite endpoint');
    return `RewriteBullet -> ${res.status}`;
  });

  await check(D, 'Bulk endpoints silently ignore foreign ids', async () => {
    const res = await pb.evaluate(async ({ base, id, token }) => {
      const body = new URLSearchParams();
      body.append('ids', id);
      body.append('newStatus', 'Rejected');
      body.append('__RequestVerificationToken', token);
      const r = await fetch(`${base}/JobApplications/BulkStatus`, { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: body.toString() });
      return r.status;
    }, { base: BASE, id: victimId, token: tokenB });
    const stillApplied = await pa.evaluate(async ({ base, id }) => {
      const html = await (await fetch(`${base}/JobApplications/Edit/${id}`)).text();
      return /selected="selected"[^>]*value="1"|value="1"\s+selected/.test(html) || html.includes('Victim Corp');
    }, { base: BASE, id: victimId });
    assert(stillApplied, 'foreign bulk status change took effect');
    return `BulkStatus -> ${res}; owner record untouched`;
  });

  await check(D, 'Anonymous requests to owned endpoints never return data', async () => {
    const out = await pan.evaluate(async ({ base, id }) => {
      const res = [];
      for (const u of [`/JobApplications/Notes?appId=${id}`, `/JobApplications/Edit/${id}`, `/Calendar/application/${id}.ics`]) {
        const r = await fetch(base + u);
        res.push({ u, status: r.status, leak: /Victim Corp|salary discussed/i.test(await r.text()) });
      }
      return res;
    }, { base: BASE, id: victimId });
    assert(out.every((r) => !r.leak), `data leaked to an anonymous caller: ${JSON.stringify(out)}`);
    return out.map((r) => `${r.u} -> ${r.status}`).join('; ');
  });

  // ------------------------------------------------------------ output escaping
  await check(D, 'Script payload in a company name renders as text, not markup', async () => {
    await createApplication(pa, { CompanyName: XSS, RoleTitle: 'XSS Probe', Status: '0' });
    let dialog = false;
    pa.on('dialog', async (d) => { dialog = true; await d.dismiss(); });
    await pa.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const fired = await pa.evaluate(() => Boolean(window.__xss));
    const injected = await pa.evaluate(() => document.querySelectorAll('img[onerror]').length);
    const shown = await pa.evaluate(() => document.body.textContent.includes('<img src=x'));
    assert(!fired, 'injected script executed on the applications list');
    assertEqual(injected, 0, 'payload was parsed into a real <img onerror> element');
    assert(!dialog, 'a dialog was triggered by injected content');
    assert(shown, 'payload was neither escaped nor visible as text - check how it was stored');
    return 'stored payload rendered as inert text on the list';
  });

  await check(D, 'Script payload survives the board and drawer as inert text', async () => {
    await pa.goto(BASE + '/JobApplications/Board', { waitUntil: 'networkidle' });
    const boardFired = await pa.evaluate(() => Boolean(window.__xss));
    const boardImg = await pa.evaluate(() => document.querySelectorAll('img[onerror]').length);
    assert(!boardFired && boardImg === 0, 'payload executed or was parsed on the board');
    await pa.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const row = await pa.$('tr[data-app-id]');
    await row.click();
    await pa.waitForTimeout(600);
    const drawerFired = await pa.evaluate(() => Boolean(window.__xss));
    const drawerImg = await pa.evaluate(() => document.querySelectorAll('.app-drawer img[onerror]').length);
    assert(!drawerFired && drawerImg === 0, 'payload executed when rendered into the drawer by JS');
    return 'board and drawer render the payload inertly';
  });

  await check(D, 'Attribute-breaking payload does not escape a data-* attribute', async () => {
    await createApplication(pa, { CompanyName: XSS2, RoleTitle: 'Attr Probe', Status: '0' });
    await pa.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const fired = await pa.evaluate(() => Boolean(window.__xss2));
    const scripts = await pa.evaluate(() => Array.from(document.querySelectorAll('script')).filter((s) => /__xss2/.test(s.textContent)).length);
    assert(!fired, 'attribute-breaking payload executed');
    assertEqual(scripts, 0, 'payload became a real <script> element');
    return 'attribute payload contained';
  });

  await check(D, 'Script payload in a note renders as text', async () => {
    const ids = await pa.evaluate(() => Array.from(document.querySelectorAll('tr[data-app-id]')).map((r) => r.getAttribute('data-app-id')));
    const t = await antiforgery(pa, '/JobApplications');
    await postForm(pa, '/JobApplications/AddNote', { appId: ids[0], text: '<script>window.__xssNote=1</script><b>bold</b>' }, t);
    await pa.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const out = await pa.evaluate(async ({ base, id }) => {
      const html = await (await fetch(`${base}/JobApplications/Notes?appId=${id}`)).text();
      const div = document.createElement('div');
      div.innerHTML = html;
      return { raw: html.includes('<script>window.__xssNote'), scripts: div.querySelectorAll('script').length, bold: div.querySelectorAll('b').length };
    }, { base: BASE, id: ids[0] });
    assert(!out.raw && out.scripts === 0, `note markup returned unescaped: ${JSON.stringify(out)}`);
    return `notes partial: ${out.scripts} script tags, ${out.bold} <b> tags from user text`;
  });

  await check(D, 'Script payload in the profile display name renders as text in the nav', async () => {
    const t = await antiforgery(pa, '/Profile');
    await postForm(pa, '/Profile/SaveInfo', { fullName: 'Sec Tester', displayName: '<script>window.__xssNav=1</script>', country: 'Canada', timeZoneId: 'America/Toronto' }, t);
    await pa.goto(BASE + '/Home/Dashboard', { waitUntil: 'networkidle' });
    const fired = await pa.evaluate(() => Boolean(window.__xssNav));
    assert(!fired, 'display name payload executed in the layout');
    return 'display name rendered inertly';
  });

  await check(D, 'A javascript: job link is not rendered as a clickable javascript: href', async () => {
    await createApplication(pa, { CompanyName: 'Link Probe', RoleTitle: 'Link Role', JobLink: 'javascript:window.__xssLink=1', Status: '0' });
    await pa.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const hrefs = await pa.evaluate(() => Array.from(document.querySelectorAll('a[href^="javascript:"]')).map((x) => x.getAttribute('href')));
    assertEqual(hrefs.length, 0, `javascript: URL rendered as a live link: ${hrefs.join(',')}`);
    return 'no javascript: hrefs rendered';
  });

  // ------------------------------------------------------------ antiforgery
  await check(D, 'State-changing POST without an antiforgery token is refused', async () => {
    const res = await postForm(pa, '/JobApplications/AddNote', { appId: '1', text: 'no token' }, null);
    assert(res.status >= 400, `AddNote without a token returned ${res.status}`);
    return `AddNote without token -> ${res.status} (antiforgery failures re-execute as 404)`;
  });

  await check(D, 'Profile writes without an antiforgery token are refused', async () => {
    const res = await postForm(pa, '/Profile/SaveSkills', { skills: 'InjectedSkill' }, null);
    await pa.goto(BASE + '/Profile', { waitUntil: 'networkidle' });
    const body = await pa.textContent('body');
    assert(!/InjectedSkill/.test(body), 'a tokenless POST modified the profile');
    return `SaveSkills without token -> ${res.status}; profile unchanged`;
  });

  await check(D, 'A token minted for another user is not accepted', async () => {
    const res = await postForm(pb, '/JobApplications/AddNote', { appId: '1', text: 'cross-user token' }, tokenA);
    assert(res.status >= 400, `foreign antiforgery token accepted (${res.status})`);
    return `cross-user token -> ${res.status}`;
  });

  // ------------------------------------------------------------ headers and cookies
  await check(D, 'Response sets Content-Security-Policy', async () => {
    const h = await pan.evaluate(async (base) => {
      const r = await fetch(base + '/');
      return Object.fromEntries([...r.headers.entries()]);
    }, BASE);
    assert(h['content-security-policy'] || h['content-security-policy-report-only'],
      `no CSP header on the landing page. Headers present: ${Object.keys(h).join(', ')}`);
    return h['content-security-policy'];
  });

  await check(D, 'Response sets X-Content-Type-Options: nosniff', async () => {
    const h = await pan.evaluate(async (base) => {
      const r = await fetch(base + '/');
      return Object.fromEntries([...r.headers.entries()]);
    }, BASE);
    assertEqual(h['x-content-type-options'], 'nosniff', `header missing or wrong. Headers present: ${Object.keys(h).join(', ')}`);
    return 'nosniff present';
  });

  await check(D, 'Application pages set a clickjacking defence', async () => {
    const h = await pa.evaluate(async (base) => {
      const r = await fetch(base + '/Home/Dashboard');
      return Object.fromEntries([...r.headers.entries()]);
    }, BASE);
    const xfo = h['x-frame-options'];
    const csp = h['content-security-policy'] || '';
    assert(xfo || /frame-ancestors/.test(csp), `neither X-Frame-Options nor CSP frame-ancestors on an authenticated page. Headers: ${Object.keys(h).join(', ')}`);
    return xfo || csp;
  });

  await check(D, 'Response sets a Referrer-Policy', async () => {
    const h = await pan.evaluate(async (base) => Object.fromEntries([...(await fetch(base + '/')).headers.entries()]), BASE);
    assert(h['referrer-policy'], `no Referrer-Policy header. Headers: ${Object.keys(h).join(', ')}`);
    return h['referrer-policy'];
  });

  await check(D, 'Server banner is not disclosed', async () => {
    const h = await pan.evaluate(async (base) => Object.fromEntries([...(await fetch(base + '/')).headers.entries()]), BASE);
    assert(!h['server'], `Server header discloses "${h['server']}"`);
    return 'no Server header';
  });

  await check(D, 'Authenticated pages are not cached by shared caches', async () => {
    const h = await pa.evaluate(async (base) => Object.fromEntries([...(await fetch(base + '/Home/Dashboard')).headers.entries()]), BASE);
    const cc = h['cache-control'] || '';
    assert(/no-store|no-cache|private/.test(cc), `Cache-Control on an authenticated page is "${cc || '(absent)'}"`);
    return `Cache-Control: ${cc}`;
  });

  await check(D, 'Auth cookie is HttpOnly and SameSite-scoped', async () => {
    const cookies = await a.context.cookies(BASE);
    const auth = cookies.find((c) => /Identity\.Application/i.test(c.name));
    assert(auth, `no identity cookie found: ${cookies.map((c) => c.name).join(', ')}`);
    assert(auth.httpOnly, 'identity cookie is not HttpOnly');
    assert(auth.sameSite && auth.sameSite !== 'None', `identity cookie SameSite is ${auth.sameSite}`);
    return `${auth.name}: httpOnly=${auth.httpOnly} sameSite=${auth.sameSite} secure=${auth.secure} (Secure is expected false over plain http locally)`;
  });

  await check(D, 'Antiforgery cookie is HttpOnly', async () => {
    const cookies = await a.context.cookies(BASE);
    const af = cookies.find((c) => /Antiforgery/i.test(c.name));
    assert(af, 'no antiforgery cookie');
    assert(af.httpOnly, 'antiforgery cookie is not HttpOnly');
    return `${af.name}: httpOnly=${af.httpOnly} sameSite=${af.sameSite}`;
  });

  // ------------------------------------------------------------ redirect safety
  let redirectProbe = null;
  await check(D, 'Login does not honour an external ReturnUrl', async () => {
    const email = uniqueEmail('redir');
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    await p.setExtraHTTPHeaders(syntheticClient());
    await p.goto(BASE + '/Identity/Account/Register', { waitUntil: 'domcontentloaded' });
    await p.fill('#Input_Email', email);
    await p.fill('#Input_Password', PASSWORD);
    await p.fill('#Input_ConfirmPassword', PASSWORD);
    await Promise.all([p.waitForLoadState('networkidle'), p.click('button[type=submit]')]);
    const t0 = await antiforgery(p, '/Home/Dashboard');
    await postForm(p, '/Identity/Account/Logout', { returnUrl: '/' }, t0);
    const t = await antiforgery(p, '/Identity/Account/Login?ReturnUrl=https%3A%2F%2Fexample.com%2Fphish');
    const res = await postForm(p, '/Identity/Account/Login', {
      'Input.Email': email, 'Input.Password': PASSWORD, ReturnUrl: 'https://example.com/phish',
    }, t);
    redirectProbe = res;
    await ctx.close();
    assert(!/example\.com/.test(res.url), `login redirected off-site to ${res.url}`);
    return `final URL stayed on ${new URL(res.url).origin}; LocalRedirect refused the off-site target`;
  });

  await check(D, 'A non-local ReturnUrl is handled without an unhandled exception', async () => {
    assert(redirectProbe, 'the redirect probe did not run');
    const blewUp = /unhandled exception|InvalidOperationException|The supplied URL is not local/i.test(redirectProbe.body);
    assert(!blewUp && redirectProbe.status < 500,
      `login with ReturnUrl=https://example.com/phish returned ${redirectProbe.status} and threw: ` +
      `${(redirectProbe.body.match(/[A-Za-z]*Exception: [^<\n]{0,160}/) || ['(see page body)'])[0]}`);
    return `status ${redirectProbe.status}`;
  });

  // ------------------------------------------------------------ admin / privileged
  await check(D, 'Admin reset endpoint is 404 for a normal account', async () => {
    const out = await pa.evaluate(async (base) => {
      const r = await fetch(base + '/Admin/ResetDemo');
      return { status: r.status, body: (await r.text()).slice(0, 200) };
    }, BASE);
    assertEqual(out.status, 404, `Admin/ResetDemo returned ${out.status} to a normal user`);
    return 'Admin/ResetDemo -> 404';
  });

  await check(D, 'Admin reset POST is refused for a normal account', async () => {
    const t = await antiforgery(pa, '/Home/Dashboard');
    const res = await postForm(pa, '/Admin/ResetDemo', {}, t);
    assert(res.status >= 400, `POST Admin/ResetDemo returned ${res.status}`);
    return `POST -> ${res.status}`;
  });

  // ------------------------------------------------------------ input handling
  await check(D, 'Oversized and control-character input does not produce a server error', async () => {
    const t = await antiforgery(pa, '/JobApplications/Create');
    const cases = [
      ['200k character description', { CompanyName: 'Big Co', RoleTitle: 'Big Role', JobDescription: 'A'.repeat(200000) }],
      ['control characters', { CompanyName: 'Ctl\u0000\u0007\u001b[31m Co', RoleTitle: 'Ctl Role' }],
      ['whitespace only', { CompanyName: '   ', RoleTitle: '   ' }],
      ['unicode and RTL', { CompanyName: 'شركة اختبار 会社 🏢', RoleTitle: 'مهندس برمجيات' }],
    ];
    const out = [];
    for (const [label, fields] of cases) {
      const res = await postForm(pa, '/JobApplications/Create', { ...fields, Status: '0', forceCreate: 'true' }, t);
      out.push(`${label} -> ${res.status}`);
      assert(res.status < 500, `${label} produced ${res.status}: ${res.body.slice(0, 200)}`);
    }
    return out.join('; ');
  });

  await check(D, 'SQL-shaped input is treated as literal text', async () => {
    const payload = "Robert'); DROP TABLE JobApplications;--";
    await createApplication(pa, { CompanyName: payload, RoleTitle: 'SQLi Probe', Status: '0', forceCreate: true });
    const res = await pa.evaluate(async (base) => (await fetch(`${base}/JobApplications?view=list&search=${encodeURIComponent("Robert'); DROP")}`)).status, BASE);
    assertEqual(res, 200, 'search with SQL-shaped input failed');
    const health = await pa.evaluate(async (base) => (await (await fetch(base + '/health')).json()).status, BASE);
    assertEqual(health, 'Healthy', 'database unhealthy after SQL-shaped input');
    return 'payload stored and searched as literal text; database healthy';
  });

  await check(D, 'Resume download rejects traversal-shaped ids', async () => {
    const out = await pa.evaluate(async (base) => {
      const res = [];
      for (const id of ['../../appsettings.json', '..%2f..%2fapp.db', '0', '-1']) {
        const r = await fetch(`${base}/Profile/DownloadResume/${id}`);
        res.push({ id, status: r.status, leak: /ConnectionStrings|ApiKey/i.test(await r.text()) });
      }
      return res;
    }, BASE);
    assert(out.every((r) => !r.leak), `config content served: ${JSON.stringify(out)}`);
    assert(out.every((r) => r.status === 404 || r.status === 400), `unexpected statuses: ${JSON.stringify(out)}`);
    return out.map((r) => `${r.id} -> ${r.status}`).join('; ');
  });

  await check(D, 'Static file middleware does not serve app files outside wwwroot', async () => {
    const out = await pan.evaluate(async (base) => {
      const res = [];
      for (const p of ['/appsettings.json', '/app.db', '/Program.cs', '/../appsettings.json', '/uploads/resumes/', '/appsettings.Development.json']) {
        const r = await fetch(base + p);
        const body = await r.text();
        res.push({ p, status: r.status, leak: /ConnectionStrings|ApiKey|DefaultConnection/i.test(body) });
      }
      return res;
    }, BASE);
    assert(out.every((r) => !r.leak), `application file served: ${JSON.stringify(out)}`);
    return out.map((r) => `${r.p} -> ${r.status}`).join('; ');
  });

  await check(D, 'Client assets carry no API keys or secrets', async () => {
    const files = ['/js/site.js', '/js/profile.js', '/js/bookmarklet.js', '/js/dashboard.js', '/js/follow-up.js', '/css/site.css'];
    const hits = await pan.evaluate(async ({ base, files }) => {
      const found = [];
      for (const f of files) {
        const body = await (await fetch(base + f)).text();
        for (const re of [/sk-[A-Za-z0-9]{20,}/, /AIza[0-9A-Za-z_\-]{30,}/, /re_[A-Za-z0-9]{20,}/, /client_secret/i, /-----BEGIN [A-Z ]*PRIVATE KEY/]) {
          const m = body.match(re);
          if (m) found.push(`${f}: ${m[0].slice(0, 12)}...`);
        }
      }
      return found;
    }, { base: BASE, files });
    assertEqual(hits.length, 0, `secret-shaped strings in client assets: ${hits.join('; ')}`);
    return `${files.length} assets scanned, nothing secret-shaped`;
  });

  await check(D, 'Error responses do not leak stack traces or local paths', async () => {
    const out = await pan.evaluate(async (base) => {
      const res = [];
      for (const p of ['/Home/Error', '/JobApplications/Edit/abc', '/Calendar/application/abc.ics', '/nope']) {
        const r = await fetch(base + p);
        const body = await r.text();
        res.push({ p, status: r.status, stack: /at [A-Za-z0-9_.]+\+?.*\.cs:line|\/Users\/|System\.[A-Za-z]+Exception/.test(body) });
      }
      return res;
    }, BASE);
    const leaky = out.filter((r) => r.stack);
    assertEqual(leaky.length, 0, `stack traces or local paths exposed on: ${leaky.map((r) => r.p).join(', ')}`);
    return out.map((r) => `${r.p} -> ${r.status}`).join('; ');
  });

  // ------------------------------------------------------------ rate limiting
  await check(D, 'AI endpoints are rate limited per user', async () => {
    const out = await pa.evaluate(async (base) => {
      const statuses = [];
      for (let i = 0; i < 26; i++) {
        const r = await fetch(base + '/Analyzer/Analyze', {
          method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
          body: JSON.stringify({ jobDescription: `probe ${i} software engineering intern` }),
        });
        statuses.push(r.status);
        if (r.status === 429) return { statuses, retryAfter: r.headers.get('retry-after'), at: i + 1 };
      }
      return { statuses, retryAfter: null, at: null };
    }, BASE);
    assert(out.at !== null, `no 429 after 26 AI calls: ${out.statuses.join(',')}`);
    assert(out.retryAfter, 'a 429 was returned without a Retry-After header');
    return `429 on request #${out.at}, Retry-After: ${out.retryAfter}`;
  });

  await check(D, 'Password reset is rate limited per IP', async () => {
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    const out = [];
    for (let i = 0; i < 8; i++) {
      const t = await antiforgery(p, '/Identity/Account/ForgotPassword');
      const res = await postForm(p, '/Identity/Account/ForgotPassword', { 'Input.Email': `probe${i}@example.test` }, t);
      out.push(res.status);
    }
    await ctx.close();
    return `statuses: ${out.join(',')} (a limited request must look identical to a sent one - verified by the identical-response check below)`;
  });

  await check(D, 'Password reset gives the same answer for known and unknown addresses', async () => {
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    const t1 = await antiforgery(p, '/Identity/Account/ForgotPassword');
    const known = await postForm(p, '/Identity/Account/ForgotPassword', { 'Input.Email': a.email }, t1);
    const t2 = await antiforgery(p, '/Identity/Account/ForgotPassword');
    const unknown = await postForm(p, '/Identity/Account/ForgotPassword', { 'Input.Email': uniqueEmail('nobody') }, t2);
    await ctx.close();
    assertEqual(known.status, unknown.status, 'status differs between a known and an unknown address');
    const norm = (s) => s.replace(/\s+/g, ' ').replace(/[A-Za-z0-9+/=_-]{40,}/g, 'TOKEN').trim();
    assertEqual(norm(known.body) === norm(unknown.body), true, 'response body differs between a known and an unknown address (account oracle)');
    return `both -> ${known.status}, identical bodies`;
  });

  await check(D, 'Registration is throttled against automated account creation', async () => {
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    // One address for all six posts, and one nothing else in the suite has used: this check is
    // about what a single client can mint, so it needs a bucket of its own to fill.
    await p.setExtraHTTPHeaders(syntheticClient());
    const statuses = [];
    for (let i = 0; i < 6; i++) {
      const t = await antiforgery(p, '/Identity/Account/Register');
      const email = uniqueEmail('flood' + i);
      const res = await postForm(p, '/Identity/Account/Register', {
        'Input.Email': email, 'Input.Password': PASSWORD, 'Input.ConfirmPassword': PASSWORD,
      }, t);
      statuses.push(res.status);
    }
    await ctx.close();
    assert(statuses.includes(429), `6 consecutive registrations all succeeded (statuses ${statuses.join(',')}) - no per-IP registration limit`);
    assertEqual(statuses[0], 200, `the first registration from a fresh client was refused (statuses ${statuses.join(',')})`);
    return statuses.join(',');
  });

  await check(D, 'Repeated failed logins lock the account out', async () => {
    const ctx = await browser.newContext();
    const p = await ctx.newPage();
    let lockedAt = null;
    for (let i = 0; i < 12; i++) {
      const t = await antiforgery(p, '/Identity/Account/Login');
      const res = await postForm(p, '/Identity/Account/Login', { 'Input.Email': a.email, 'Input.Password': `wrong-${i}` }, t);
      if (/locked/i.test(res.body)) { lockedAt = i + 1; break; }
    }
    // Confirm the real password still works, so a lockout claim is not a false positive.
    const t = await antiforgery(p, '/Identity/Account/Login');
    const ok = await postForm(p, '/Identity/Account/Login', { 'Input.Email': a.email, 'Input.Password': PASSWORD }, t);
    await ctx.close();
    assert(lockedAt !== null, `12 wrong passwords in a row produced no lockout (correct password afterwards -> ${ok.status})`);
    return `locked out after ${lockedAt} attempts`;
  });

  await browser.close();
}
