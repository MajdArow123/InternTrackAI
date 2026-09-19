// Performance dimension: navigation and paint timings, transfer weight, layout shift,
// long tasks and server latency. NOTE: the server under test is a Debug build served by
// `dotnet run`, and this repo ships unbundled, unminified static assets by design
// (CLAUDE.md: "vendored in wwwroot/lib, no npm"), so asset weight here equals production
// weight while server timings are a pessimistic upper bound.
import { chromium, BASE, check, assert, assertEqual, newSignedInContext, createApplication, ARTIFACTS } from './lib/harness.mjs';
import fs from 'node:fs';
import path from 'node:path';

const D = 'Performance';

// Budgets: "good" Core Web Vitals thresholds, relaxed where a local Debug build is
// the bottleneck rather than the code under review.
const BUDGET = { fcp: 1800, lcp: 2500, dcl: 2000, load: 4000, cls: 0.1, transferKb: 1500, requests: 60, apiP50: 400, apiP95: 1200 };

async function measure(page, url) {
  await page.goto('about:blank');
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Network.enable');
  let transfer = 0; let requests = 0;
  const onData = (e) => { transfer += e.encodedDataLength || 0; };
  cdp.on('Network.loadingFinished', onData);
  cdp.on('Network.requestWillBeSent', () => { requests++; });

  await page.addInitScript(() => {
    window.__cls = 0; window.__long = [];
    try {
      new PerformanceObserver((l) => { for (const e of l.getEntries()) if (!e.hadRecentInput) window.__cls += e.value; }).observe({ type: 'layout-shift', buffered: true });
      new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__long.push(Math.round(e.duration)); }).observe({ type: 'longtask', buffered: true });
      new PerformanceObserver((l) => { const e = l.getEntries(); window.__lcp = e[e.length - 1]?.startTime; }).observe({ type: 'largest-contentful-paint', buffered: true });
    } catch { /* unsupported */ }
  });

  const t0 = Date.now();
  await page.goto(BASE + url, { waitUntil: 'load' });
  await page.waitForLoadState('networkidle').catch(() => {});
  await page.waitForTimeout(600);
  const wall = Date.now() - t0;

  const m = await page.evaluate(() => {
    const nav = performance.getEntriesByType('navigation')[0] || {};
    const paints = Object.fromEntries(performance.getEntriesByType('paint').map((p) => [p.name, Math.round(p.startTime)]));
    const res = performance.getEntriesByType('resource');
    return {
      ttfb: Math.round(nav.responseStart || 0),
      dcl: Math.round(nav.domContentLoadedEventEnd || 0),
      load: Math.round(nav.loadEventEnd || 0),
      fcp: paints['first-contentful-paint'] ?? null,
      lcp: window.__lcp ? Math.round(window.__lcp) : null,
      cls: Number((window.__cls || 0).toFixed(4)),
      longTasks: window.__long || [],
      resourceCount: res.length + 1,
      decodedKb: Math.round(res.reduce((s, r) => s + (r.decodedBodySize || 0), 0) / 1024),
      slowest: res.map((r) => ({ n: r.name.split('/').pop().split('?')[0], d: Math.round(r.duration) })).sort((x, y) => y.d - x.d).slice(0, 3),
    };
  });
  cdp.off('Network.loadingFinished', onData);
  await cdp.detach().catch(() => {});
  return { ...m, wall, transferKb: Math.round(transfer / 1024), requests };
}

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });
  const { context } = await newSignedInContext(browser, 'perf');
  const page = await context.newPage();
  await page.setViewportSize({ width: 1440, height: 900 });

  // A realistic amount of data, not an empty account.
  for (let i = 0; i < 12; i++) {
    await createApplication(page, {
      CompanyName: `Perf Company ${i}`, RoleTitle: `Engineering Intern ${i}`, Location: 'Remote',
      Status: String(i % 5), forceCreate: true,
      JobDescription: 'Requirements: Kubernetes, Terraform, PostgreSQL, Docker, Go, Redis, Kafka, GraphQL, Prometheus, Grafana. '.repeat(6),
    });
  }

  const report = {};
  const pages = [
    ['Landing', '/'],
    ['Dashboard', '/Home/Dashboard'],
    ['Applications list', '/JobApplications?view=list'],
    ['Kanban board', '/JobApplications/Board'],
    ['Create', '/JobApplications/Create'],
    ['Profile', '/Profile'],
  ];

  for (const [name, url] of pages) {
    await check(D, `Page metrics: ${name}`, async () => {
      await measure(page, url); // warm
      const m = await measure(page, url);
      report[name] = m;
      const over = [];
      if (m.fcp !== null && m.fcp > BUDGET.fcp) over.push(`FCP ${m.fcp}ms > ${BUDGET.fcp}`);
      if (m.lcp !== null && m.lcp > BUDGET.lcp) over.push(`LCP ${m.lcp}ms > ${BUDGET.lcp}`);
      if (m.dcl > BUDGET.dcl) over.push(`DCL ${m.dcl}ms > ${BUDGET.dcl}`);
      if (m.transferKb > BUDGET.transferKb) over.push(`transfer ${m.transferKb}KB > ${BUDGET.transferKb}KB`);
      if (m.requests > BUDGET.requests) over.push(`${m.requests} requests > ${BUDGET.requests}`);
      const line = `TTFB ${m.ttfb}ms, FCP ${m.fcp}ms, LCP ${m.lcp}ms, DCL ${m.dcl}ms, load ${m.load}ms, wall ${m.wall}ms, ${m.transferKb}KB over ${m.requests} requests (${m.decodedKb}KB decoded), CLS ${m.cls}, longTasks ${m.longTasks.length ? m.longTasks.join('/') + 'ms' : 'none'}`;
      assertEqual(over.length, 0, `${over.join('; ')}\n    ${line}\n    slowest: ${m.slowest.map((s) => `${s.n} ${s.d}ms`).join(', ')}`);
      return line;
    });
  }

  await check(D, 'Cumulative layout shift stays under 0.1 on every page', async () => {
    const bad = Object.entries(report).filter(([, m]) => m.cls > BUDGET.cls).map(([n, m]) => `${n}: ${m.cls}`);
    assertEqual(bad.length, 0, `CLS over budget: ${bad.join(', ')}`);
    return Object.entries(report).map(([n, m]) => `${n} ${m.cls}`).join(', ');
  });

  await check(D, 'No long task blocks the main thread for over 200 ms', async () => {
    const bad = Object.entries(report).flatMap(([n, m]) => m.longTasks.filter((t) => t > 200).map((t) => `${n}: ${t}ms`));
    assertEqual(bad.length, 0, `long tasks over 200ms: ${bad.join(', ')}`);
    const all = Object.entries(report).flatMap(([n, m]) => m.longTasks.map((t) => `${n} ${t}ms`));
    return all.length ? `longest tasks: ${all.join(', ')}` : 'no long tasks recorded';
  });

  // ------------------------------------------------------------ server latency
  await check(D, 'Server latency for the main routes', async () => {
    const routes = ['/health', '/', '/Home/Dashboard', '/JobApplications?view=list', '/JobApplications/Board', '/Profile'];
    const out = {};
    const slow = [];
    for (const r of routes) {
      const samples = await page.evaluate(async ({ base, r }) => {
        const t = [];
        await fetch(base + r); // warm
        for (let i = 0; i < 12; i++) {
          const s = performance.now();
          await fetch(base + r, { cache: 'no-store' });
          t.push(performance.now() - s);
        }
        return t.sort((a, b) => a - b);
      }, { base: BASE, r });
      const p50 = Math.round(samples[Math.floor(samples.length * 0.5)]);
      const p95 = Math.round(samples[Math.floor(samples.length * 0.95)]);
      out[r] = `p50 ${p50}ms / p95 ${p95}ms`;
      if (p50 > BUDGET.apiP50 || p95 > BUDGET.apiP95) slow.push(`${r}: ${out[r]}`);
    }
    assertEqual(slow.length, 0, `over budget (p50 ${BUDGET.apiP50}ms / p95 ${BUDGET.apiP95}ms): ${slow.join('; ')}`);
    return Object.entries(out).map(([k, v]) => `${k} ${v}`).join(' | ');
  });

  await check(D, 'Keyword coverage (the per-drawer-open computation) stays fast', async () => {
    const token = await page.evaluate(() => document.querySelector('input[name="__RequestVerificationToken"]')?.value);
    await page.goto(BASE + '/JobApplications?view=list', { waitUntil: 'networkidle' });
    const t = await page.evaluate(() => document.querySelector('input[name="__RequestVerificationToken"]')?.value);
    const samples = await page.evaluate(async ({ base, t }) => {
      const body = new URLSearchParams();
      body.append('Description', 'Requirements: Kubernetes, Terraform, PostgreSQL, Docker, Go, Redis, Kafka, GraphQL, Prometheus, Grafana, ASP.NET Core, CI/CD. '.repeat(8));
      body.append('Company', 'Perf Company 1');
      body.append('Role', 'Engineering Intern');
      body.append('__RequestVerificationToken', t);
      const times = [];
      for (let i = 0; i < 10; i++) {
        const s = performance.now();
        await fetch(base + '/JobApplications/KeywordCoverage', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded' }, body: body.toString() });
        times.push(performance.now() - s);
      }
      return times.sort((a, b) => a - b);
    }, { base: BASE, t: t || token });
    const p50 = Math.round(samples[5]);
    const p95 = Math.round(samples[9]);
    assert(p95 < 1000, `keyword coverage p95 ${p95}ms — it runs on every drawer open and is uncached`);
    return `p50 ${p50}ms / p95 ${p95}ms over 10 calls`;
  });

  // ------------------------------------------------------------ static asset weight
  await check(D, 'First-visit payload (cold cache) stays under budget', async () => {
    const cold = {};
    const over = [];
    for (const [name, url] of pages) {
      const ctx = await browser.newContext({ storageState: await context.storageState() });
      const p = await ctx.newPage();
      const cdp = await ctx.newCDPSession(p);
      await cdp.send('Network.enable');
      await cdp.send('Network.setCacheDisabled', { cacheDisabled: true });
      let transfer = 0; let n = 0;
      cdp.on('Network.loadingFinished', (e) => { transfer += e.encodedDataLength || 0; });
      cdp.on('Network.requestWillBeSent', () => { n++; });
      await p.goto(BASE + url, { waitUntil: 'load' });
      await p.waitForLoadState('networkidle').catch(() => {});
      cold[name] = { kb: Math.round(transfer / 1024), n };
      if (cold[name].kb > BUDGET.transferKb) over.push(`${name} ${cold[name].kb}KB`);
      await ctx.close();
    }
    const line = Object.entries(cold).map(([k, v]) => `${k} ${v.kb}KB/${v.n}req`).join(', ');
    assertEqual(over.length, 0, `over the ${BUDGET.transferKb}KB first-visit budget: ${over.join(', ')}\n    all pages: ${line}`);
    return line;
  });

  await check(D, 'Vendored assets on disk are not dead weight', async () => {
    const root = path.join(process.cwd(), 'wwwroot');
    const walk = (dir) => fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => {
      const f = path.join(dir, e.name);
      return e.isDirectory() ? walk(f) : [{ f: path.relative(root, f), size: fs.statSync(f).size }];
    });
    const files = walk(root).filter((x) => /\.(js|css)$/.test(x.f));
    const total = Math.round(files.reduce((s, x) => s + x.size, 0) / 1024);
    const first = Math.round(files.filter((x) => !x.f.startsWith('lib/')).reduce((s, x) => s + x.size, 0) / 1024);
    // Files shipped in the repo that no view references at all (unminified twins, RTL builds,
    // slim jQuery). Checked against the Razor sources, not against one page's network log, so
    // a script used on a single page (jsPDF on the cover-letter page) is not counted as unused.
    const views = [];
    const collect = (dir) => { for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
      const f = path.join(dir, e.name);
      if (e.isDirectory()) collect(f); else if (f.endsWith('.cshtml')) views.push(fs.readFileSync(f, 'utf8'));
    } };
    for (const d of ['Views', 'Areas']) if (fs.existsSync(d)) collect(d);
    const markup = views.join('\n');
    const unused = files.filter((x) => x.f.startsWith('lib/') && !markup.includes(path.basename(x.f))).sort((a, b) => b.size - a.size);
    const unusedKb = Math.round(unused.reduce((s, x) => s + x.size, 0) / 1024);
    const biggest = files.sort((a, b) => b.size - a.size).slice(0, 5).map((x) => `${x.f} ${Math.round(x.size / 1024)}KB`);
    assert(unusedKb < 1500, `${unusedKb}KB of vendored js/css is committed but referenced by no view (${unused.slice(0, 6).map((x) => `${x.f} ${Math.round(x.size / 1024)}KB`).join(', ')}). Largest overall: ${biggest.join(', ')}`);
    return `${total}KB js/css on disk (${first}KB first-party); ${unusedKb}KB never requested`;
  });

  await check(D, 'Static assets are served with caching validators', async () => {
    const out = await page.evaluate(async (base) => {
      const res = [];
      for (const f of ['/css/site.css', '/js/site.js', '/lib/bootstrap/dist/js/bootstrap.bundle.min.js']) {
        const r = await fetch(base + f, { cache: 'no-store' });
        res.push({ f, etag: r.headers.get('etag'), lm: r.headers.get('last-modified'), cc: r.headers.get('cache-control'), enc: r.headers.get('content-encoding'), len: r.headers.get('content-length') });
      }
      return res;
    }, BASE);
    const noValidator = out.filter((r) => !r.etag && !r.lm);
    assertEqual(noValidator.length, 0, `no ETag/Last-Modified on: ${noValidator.map((r) => r.f).join(', ')}`);
    return out.map((r) => `${r.f}: etag=${r.etag ? 'yes' : 'no'} cache-control=${r.cc || '(none)'} encoding=${r.enc || 'identity'} ${r.len ? Math.round(r.len / 1024) + 'KB' : ''}`).join(' | ');
  });

  await check(D, 'Static assets are compressed on the wire', async () => {
    const out = await page.evaluate(async (base) => {
      const r = await fetch(base + '/css/site.css', { cache: 'no-store' });
      return { enc: r.headers.get('content-encoding'), len: Number(r.headers.get('content-length') || 0) };
    }, BASE);
    assert(out.enc, `site.css is served uncompressed (${Math.round(out.len / 1024)}KB on the wire); no Content-Encoding`);
    return `Content-Encoding: ${out.enc}`;
  });

  await check(D, 'The list page scales with more applications', async () => {
    const m = await measure(page, '/JobApplications?view=list');
    const rows = await page.evaluate(() => document.querySelectorAll('tr[data-app-id]').length);
    assert(m.wall < 4000, `${rows} rows took ${m.wall}ms wall-clock`);
    return `${rows} rows: TTFB ${m.ttfb}ms, DCL ${m.dcl}ms, wall ${m.wall}ms, ${m.transferKb}KB`;
  });

  fs.writeFileSync(path.join(ARTIFACTS, 'perf-metrics.json'), JSON.stringify(report, null, 2));
  await browser.close();
}
