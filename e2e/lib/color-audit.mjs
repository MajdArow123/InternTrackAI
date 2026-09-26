// Records the colour, effective background and contrast of every visible piece of text on every page, in
// both themes, so a colour change can be diffed: which elements moved, from what to what, and whether
// anything moved that was not meant to. A token change is a global change wearing a local disguise; this is
// how it is checked.
//
//   NODE_PATH=$HOME/.claude/skills/gstack/node_modules node e2e/lib/color-audit.mjs before.json
//   ... change the CSS ...
//   NODE_PATH=... node e2e/lib/color-audit.mjs after.json
//   node e2e/lib/color-audit.mjs --diff before.json after.json
//
// Needs the app on BASE_URL (default :5240) pointed at the stub (e2e/lib/openai-stub.mjs), because the
// warning and accent washes only appear on real data: it creates one application in each status with a
// deadline three days out, practice questions at every difficulty, and one scored, saved answer.
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

const PAGES_ANON = ['/', '/Identity/Account/Login', '/Identity/Account/Register', '/Home/Privacy', '/Home/Terms', '/no-such-page'];
const PAGES_SIGNED_IN = (appId) => [
  '/Home/Dashboard', '/JobApplications?view=list', '/JobApplications/Board', '/JobApplications/Create',
  `/JobApplications/Edit/${appId}`, '/Profile', `/CoverLetter/Generate?appId=${appId}`, `/InterviewPrep/Prep?appId=${appId}`,
  '/Practice', '/Identity/Account/Manage', '/Identity/Account/Manage/ChangePassword', '/Identity/Account/Manage/DeletePersonalData',
];
const THEMES = ['light', 'dark'];

function collect(page, path, theme) {
  return page.evaluate(({ path, theme }) => {
    const parse = (c) => { const m = c.match(/rgba?\(([^)]+)\)/); if (!m) return null; const [r, g, b, a = 1] = m[1].split(',').map((x) => parseFloat(x)); return { r, g, b, a }; };
    const over = (top, under) => ({ r: top.r * top.a + under.r * (1 - top.a), g: top.g * top.a + under.g * (1 - top.a), b: top.b * top.a + under.b * (1 - top.a), a: 1 });
    const lum = ({ r, g, b }) => [r, g, b].map((v) => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }).reduce((s, v, i) => s + v * [0.2126, 0.7152, 0.0722][i], 0);
    const ratio = (a, b) => { const [x, y] = [lum(a), lum(b)].sort((p, q) => q - p); return (x + 0.05) / (y + 0.05); };
    const hex = (c) => '#' + [c.r, c.g, c.b].map((v) => Math.round(v).toString(16).padStart(2, '0')).join('');
    const background = (el) => {
      const layers = [];
      for (let e = el; e; e = e.parentElement) {
        const c = parse(getComputedStyle(e).backgroundColor);
        if (c && c.a > 0) { layers.push(c); if (c.a >= 1) break; }
      }
      let bg = { r: 255, g: 255, b: 255, a: 1 };
      for (const l of layers.reverse()) bg = over(l, bg);
      return bg;
    };
    const pathOf = (el) => {
      const parts = [];
      for (let e = el, i = 0; e && e !== document.body && i < 5; e = e.parentElement, i++) {
        const cls = typeof e.className === 'string' ? e.className.trim().split(/\s+/).filter(Boolean).slice(0, 2).join('.') : '';
        const idx = e.parentElement ? Array.from(e.parentElement.children).filter((c) => c.tagName === e.tagName).indexOf(e) : 0;
        parts.push(`${e.tagName.toLowerCase()}${cls ? '.' + cls : ''}:${idx}`);
      }
      return parts.join(' < ');
    };
    const out = [];
    for (const el of document.querySelectorAll('body *')) {
      const own = Array.from(el.childNodes).filter((n) => n.nodeType === 3).map((n) => n.textContent).join('').replace(/\s+/g, ' ').trim();
      if (!own) continue;
      const cs = getComputedStyle(el); const r = el.getBoundingClientRect();
      if (!r.width || !r.height || cs.visibility === 'hidden' || Number(cs.opacity) === 0) continue;
      const bg = background(el); const fgRaw = parse(cs.color); if (!fgRaw) continue;
      const fg = over(fgRaw, bg);
      const size = parseFloat(cs.fontSize); const bold = Number(cs.fontWeight) >= 700;
      out.push({ path, theme, el: pathOf(el), text: own.slice(0, 40), fg: hex(fg), bg: hex(bg), ratio: Math.round(ratio(fg, bg) * 100) / 100, large: size >= 24 || (bold && size >= 18.66) });
    }
    return out;
  }, { path, theme });
}

async function seed(page, BASE) {
  const inThreeDays = new Date(Date.now() + 3 * 86400000).toISOString().slice(0, 10);
  for (const [status, name] of [['0', 'Saved'], ['1', 'Applied'], ['2', 'Interview'], ['3', 'Rejected'], ['4', 'Offer']]) {
    await page.goto(`${BASE}/JobApplications/Create`, { waitUntil: 'domcontentloaded' });
    await page.fill('#CompanyName', `${name} Co`); await page.fill('#RoleTitle', 'Intern');
    await page.fill('#JobDescription', 'Requirements: SQL and Go.');
    await page.selectOption('#Status', status);
    await page.fill('#Deadline', inThreeDays).catch(() => {});
    await page.evaluate(() => { const f = document.querySelector('#forceCreate'); if (f) f.value = 'true'; });
    await Promise.all([page.waitForLoadState('networkidle'), page.click('#appForm button[type=submit]')]);
  }
  for (const d of ['Easy', 'Medium', 'Hard']) {
    await page.goto(`${BASE}/Practice?difficulty=${d}`, { waitUntil: 'networkidle' });
    const before = await page.$$eval('#practiceList [data-question-id]', (c) => c.length);
    await page.click('#practiceGenerateBtn');
    await page.waitForFunction((n) => document.querySelectorAll('#practiceList [data-question-id]').length > n, before, { timeout: 15000 });
  }
  const id = await page.$eval('#practiceList .practice-card[data-answered="false"]', (c) => c.dataset.questionId);
  await page.fill(`[data-question-id="${id}"] textarea[name="answer"]`, 'I added an idempotency key column and returned the stored result on a retry.');
  await page.click(`[data-question-id="${id}"] [data-practice-submit]`);
  await page.waitForSelector(`[data-question-id="${id}"][data-answered="true"]`, { timeout: 15000 });
  // A saved question too: nothing seeded one before, so the saved star's colour (about 2:1) was never measured.
  await page.click(`[data-question-id="${id}"] [data-practice-star]`);
  await page.waitForSelector(`[data-question-id="${id}"] [data-practice-star][aria-pressed="true"]`, { timeout: 15000 });
}

export async function audit(out) {
  const { chromium, BASE, registerThroughUi, uniqueEmail } = await import('./harness.mjs');
  const browser = await chromium.launch({ channel: 'chrome' });
  const rows = [];
  try {
    const setup = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    await registerThroughUi(setup, uniqueEmail('colors'));
    const page = await setup.newPage();
    await seed(page, BASE);
    await page.goto(`${BASE}/JobApplications?view=list`, { waitUntil: 'networkidle' });
    const appId = await page.$eval('[data-app-id]', (e) => e.dataset.appId);
    const auth = await setup.storageState();

    for (const theme of THEMES) {
      const init = (t) => { try { localStorage.setItem('theme', t); } catch { /* ignore */ } };
      const anon = await browser.newContext({ viewport: { width: 1280, height: 900 } });
      await anon.addInitScript(init, theme);
      const pa = await anon.newPage();
      for (const p of PAGES_ANON) { await pa.goto(BASE + p, { waitUntil: 'networkidle' }); rows.push(...(await collect(pa, p, theme))); }
      await anon.close();

      const signed = await browser.newContext({ viewport: { width: 1280, height: 900 }, storageState: auth });
      await signed.addInitScript(init, theme);
      const ps = await signed.newPage();
      for (const p of PAGES_SIGNED_IN(appId)) {
        await ps.goto(BASE + p, { waitUntil: 'networkidle' });
        rows.push(...(await collect(ps, p.replace(appId, '{id}'), theme)));
      }
      await signed.close();
    }
  } finally {
    await browser.close();
  }
  fs.writeFileSync(out, JSON.stringify(rows));
  return rows;
}

// Digits are dropped from the text so counts and dates that differ between two seeded runs still match.
// For the rest to match, run both audits against a freshly started stub: it hands out question texts in
// order, so a stub that has already answered earlier runs produces different questions.
const key = (r) => `${r.theme} ${r.path} ${r.el} ${r.text.replace(/\d+/g, '#')}`;

export function diff(beforeFile, afterFile) {
  const before = new Map(JSON.parse(fs.readFileSync(beforeFile, 'utf8')).map((r) => [key(r), r]));
  const after = JSON.parse(fs.readFileSync(afterFile, 'utf8'));
  const changed = after.filter((r) => before.has(key(r)) && (before.get(key(r)).fg !== r.fg || before.get(key(r)).bg !== r.bg))
    .map((r) => ({ ...r, was: before.get(key(r)) }));
  const unmatched = after.filter((r) => !before.has(key(r))).length;
  const failing = after.filter((r) => r.ratio < (r.large ? 3 : 4.5));
  return { changed, unmatched, failing, total: after.length };
}

// realpath: /tmp on macOS is a symlink, and Node reports the main module's resolved path.
if (process.argv[1] && import.meta.url === pathToFileURL(fs.realpathSync(process.argv[1])).href) {
  if (process.argv[2] === '--diff') {
    const d = diff(process.argv[3], process.argv[4]);
    console.log(`${d.total} text elements; ${d.changed.length} changed colour or background; ${d.unmatched} not matched to a before row`);
    const groups = new Map();
    for (const r of d.changed) {
      const g = `${r.theme}: ${r.was.fg} on ${r.was.bg} (${r.was.ratio}) -> ${r.fg} on ${r.bg} (${r.ratio})`;
      if (!groups.has(g)) groups.set(g, []);
      groups.get(g).push(`${r.path} "${r.text}" [${r.el.split(' < ')[0]}]`);
    }
    for (const [g, items] of groups) console.log(`\n  ${g}  x${items.length}\n    ${[...new Set(items)].slice(0, 12).join('\n    ')}`);
    console.log(`\nBelow AA after the change: ${d.failing.length}`);
    for (const r of d.failing.slice(0, 40)) console.log(`  ${r.theme.padEnd(5)} ${r.path.padEnd(40)} ${r.ratio}  ${r.fg} on ${r.bg}  "${r.text}"  [${r.el.split(' < ')[0]}]`);
  } else {
    const rows = await audit(process.argv[2] || 'colors.json');
    console.log(`${rows.length} text elements written to ${process.argv[2] || 'colors.json'}`);
  }
}
