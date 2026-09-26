// Lists every link on every page a signed-out and a signed-in visitor can reach, with whether it is
// underlined and whether it sits in running text. Run it before and after a global CSS change and diff the
// two files: that is how a rule like "underline links in running text" is checked for hitting something it
// should not (a nav item, a breadcrumb, a pill) and for missing something it should.
//
//   NODE_PATH=$HOME/.claude/skills/gstack/node_modules node e2e/lib/link-audit.mjs before.json
//   ... change the CSS ...
//   NODE_PATH=... node e2e/lib/link-audit.mjs after.json
//   node e2e/lib/link-audit.mjs --diff before.json after.json
//
// Needs the app on BASE_URL (default :5240) with the placeholder key, as the other dimensions do. Registers
// one throwaway account through the Register page and creates one application through the Create form.
import fs from 'node:fs';
import { pathToFileURL } from 'node:url';

const PAGES_ANON = ['/', '/Identity/Account/Login', '/Identity/Account/Register', '/Identity/Account/ForgotPassword', '/Home/Privacy', '/Home/Terms', '/no-such-page'];
const PAGES_SIGNED_IN = (appId) => [
  '/Home/Dashboard', '/JobApplications?view=list', '/JobApplications/Board', '/JobApplications/Create',
  `/JobApplications/Edit/${appId}`, `/JobApplications/Delete/${appId}`, '/Profile', '/Profile/Bookmarklet',
  `/CoverLetter/Generate?appId=${appId}`, `/InterviewPrep/Prep?appId=${appId}`, '/Practice',
  '/Identity/Account/Manage', '/Identity/Account/Manage/ChangePassword', '/Identity/Account/Manage/DeletePersonalData',
  '/Home/Privacy', '/Home/Terms', '/no-such-page',
];

/** Every link on the page: where it is, how it is drawn, and whether it is inside running text. */
function collect(page, path, who) {
  return page.evaluate(({ path, who }) => {
    const BLOCK = new Set(['P', 'LI', 'DD', 'DT', 'TD', 'TH', 'LABEL', 'BLOCKQUOTE', 'FIGCAPTION', 'DIV', 'SECTION', 'SPAN', 'SMALL', 'H1', 'H2', 'H3', 'H4', 'H5', 'H6']);
    return Array.from(document.querySelectorAll('a[href]')).filter((a) => {
      const r = a.getBoundingClientRect(); const cs = getComputedStyle(a);
      return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden';
    }).map((a) => {
      // axe's notion of "in a block of text": the nearest block-ish parent has text beyond the link's own.
      let p = a.parentElement;
      while (p && !BLOCK.has(p.tagName)) p = p.parentElement;
      const own = a.textContent.replace(/\s+/g, ' ').trim();
      const around = p ? p.textContent.replace(/\s+/g, ' ').trim() : own;
      const cs = getComputedStyle(a);
      // The shape the site.css rule targets: a class-less link whose nearest parent is a text element.
      const TEXT = new Set(['P', 'LI', 'DD', 'TD', 'TH', 'BLOCKQUOTE', 'FIGCAPTION', 'LABEL', 'SMALL']);
      const parent = a.parentElement;
      // Links inside <nav> (breadcrumbs) are navigation, not prose: a trail of lone links with no text around them.
      const prose = !a.className && parent && !a.closest('nav') && (TEXT.has(parent.tagName) || parent.classList.contains('form-hint') || parent.classList.contains('sub'));
      const trail = []; let q = a.parentElement;
      for (let i = 0; q && i < 3; i++, q = q.parentElement) trail.push(q.tagName.toLowerCase() + (q.className && typeof q.className === 'string' ? '.' + q.className.trim().split(/\s+/).slice(0, 2).join('.') : ''));
      return {
        who, path,
        text: own.slice(0, 60) || a.getAttribute('aria-label') || '(icon)',
        href: a.getAttribute('href'),
        cls: a.className && typeof a.className === 'string' ? a.className.trim() : '',
        parents: trail.join(' < '),
        underlined: cs.textDecorationLine.includes('underline'),
        bold: Number(cs.fontWeight) >= 600,
        inRunningText: around.length > own.length + 3,
        prose: Boolean(prose),
      };
    });
  }, { path, who });
}

export async function audit(out) {
  const { chromium, BASE, registerThroughUi, uniqueEmail, createApplication } = await import('./harness.mjs');
  const browser = await chromium.launch({ channel: 'chrome' });
  const rows = [];
  try {
    const anon = await (await browser.newContext({ viewport: { width: 1280, height: 900 } })).newPage();
    for (const p of PAGES_ANON) { await anon.goto(BASE + p, { waitUntil: 'networkidle' }); rows.push(...(await collect(anon, p, 'anonymous'))); }

    const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } });
    await registerThroughUi(ctx, uniqueEmail('links'));
    const page = await ctx.newPage();
    await createApplication(page, { CompanyName: 'Link Audit Co', RoleTitle: 'Intern', JobDescription: 'Requirements: SQL.', forceCreate: true });
    await page.goto(`${BASE}/JobApplications?view=list`, { waitUntil: 'networkidle' });
    const appId = await page.$eval('[data-app-id]', (e) => e.dataset.appId);
    for (const p of PAGES_SIGNED_IN(appId)) {
      await page.goto(BASE + p, { waitUntil: 'networkidle' });
      rows.push(...(await collect(page, p.replace(appId, '{id}'), 'signed-in')));
    }
  } finally {
    await browser.close();
  }
  fs.writeFileSync(out, JSON.stringify(rows, null, 1));
  return rows;
}

const key = (r) => `${r.who} ${r.path} | ${r.text} | ${r.href} | ${r.parents}`;

export function diff(beforeFile, afterFile) {
  const before = new Map(JSON.parse(fs.readFileSync(beforeFile, 'utf8')).map((r) => [key(r), r]));
  const after = JSON.parse(fs.readFileSync(afterFile, 'utf8'));
  const changed = after.filter((r) => before.has(key(r)) && before.get(key(r)).underlined !== r.underlined);
  const missing = after.filter((r) => !before.has(key(r)));
  // Prose links that are neither underlined nor bold: colour is all that marks them.
  const stillBare = after.filter((r) => r.prose && !r.underlined && !r.bold);
  return { changed, missing, stillBare, total: after.length };
}

// realpath: /tmp on macOS is a symlink, and Node reports the main module's resolved path.
if (process.argv[1] && import.meta.url === pathToFileURL(fs.realpathSync(process.argv[1])).href) {
  if (process.argv[2] === '--diff') {
    const d = diff(process.argv[3], process.argv[4]);
    console.log(`${d.total} links; ${d.changed.length} changed underline; ${d.missing.length} not matched to a before row; ${d.stillBare.length} prose links marked by colour alone`);
    for (const r of d.missing) console.log(`  unmatched  ${r.who.padEnd(9)} ${r.path.padEnd(38)} "${r.text}"  [${r.cls || 'no class'}]  underlined=${r.underlined}`);
    for (const r of d.changed) console.log(`  ${r.underlined ? '+underline' : '-underline'}  ${r.who.padEnd(9)} ${r.path.padEnd(38)} "${r.text}"  [${r.cls || 'no class'}]  ${r.parents}`);
    if (d.stillBare.length) { console.log('\nProse links marked by colour alone:'); for (const r of d.stillBare) console.log(`  ${r.who.padEnd(9)} ${r.path.padEnd(38)} "${r.text}"  [${r.cls || 'no class'}]  ${r.parents}`); }
  } else {
    const rows = await audit(process.argv[2] || 'links.json');
    console.log(`${rows.length} links written to ${process.argv[2] || 'links.json'}`);
  }
}
