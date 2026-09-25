// The practice page and the interview prep page, driven the way a person uses them, against a stub model.
//
// Written because three bug classes in one week passed a fully green xUnit suite: the progress card went
// stale after answering and again after generating (both reported as data loss, because a card full of
// figures reads as authoritative), and an autosave helper's catch swallowed a ReferenceError so drafts
// silently never restored. All of that lives in practice.js, which had no automated coverage at all.
//
// Runs on its own throwaway server (lib/app-server.mjs) with the in-process stub as its model, so every
// "Get more" and every score is real app code answering a real request — only the model is fake.
import { chromium, check, assert, assertEqual, newSignedInContext, isCspReportNoise, createApplication } from './lib/harness.mjs';
import { startApp, PRACTICE_LIMIT } from './lib/app-server.mjs';

const D = 'Practice';
const LONG = 'I added an idempotency key column and made the handler return the stored result on a retry, which stopped the double charges.';
const CARD = '#practiceList .practice-card[data-question-id]';

export async function run() {
  const browser = await chromium.launch({ channel: 'chrome' });
  const app = await startApp(browser, { name: 'practice' });
  const B = app.base;
  const stub = app.stub;

  const errors = [];
  const watch = (page) => {
    page.on('console', (m) => {
      const t = m.text();
      // A refused call is a 429 the batch check provokes on purpose; the browser logs every non-2xx fetch.
      if (m.type() === 'error' && !isCspReportNoise(t) && !/status of 429/.test(t)) errors.push(`${page.url().replace(B, '')}: ${t.slice(0, 160)}`);
    });
    page.on('pageerror', (e) => errors.push(`${page.url().replace(B, '')}: pageerror ${e.message.slice(0, 160)}`));
  };

  const account = async (tag) => {
    const { context } = await newSignedInContext(browser, tag, { base: B, viewport: { width: 1280, height: 900 } });
    const page = await context.newPage();
    watch(page);
    return { context, page };
  };

  // ---------------------------------------------------------------- page readers
  const ids = (page) => page.$$eval(CARD, (cs) => cs.map((c) => c.dataset.questionId));
  /** Unanswered cards only — the page orders newest first, so an index into ids() can land on an answered card. */
  const fresh = (page) => page.$$eval(`${CARD}[data-answered="false"]`, (cs) => cs.map((c) => c.dataset.questionId));
  const card = (id) => `${CARD.replace('[data-question-id]', '')}[data-question-id="${id}"]`;
  /** The progress card's "answered/total", or null when the page shows no progress card. */
  const progress = (page) => page.evaluate(() => {
    const n = document.querySelector('#practiceProgress .mini-stat-num');
    if (!n) return null;
    const [answered, total] = n.textContent.replace(/\s+/g, '').split('/').map(Number);
    return { answered, total };
  });
  const generate = async (page) => {
    const before = (await ids(page)).length;
    await page.click('#practiceGenerateBtn');
    await page.waitForFunction((n) => document.querySelectorAll('#practiceList .practice-card[data-question-id]').length > n
      && !document.getElementById('practiceGenerateBtn').disabled, before, { timeout: 15000 });
    await page.waitForTimeout(600);   // the progress refresh is a second fetch after the cards land
  };
  const answer = async (page, id, text = LONG) => {
    await page.fill(`${card(id)} textarea[name="answer"]`, text);
    await page.click(`${card(id)} [data-practice-submit]`);
    await page.waitForSelector(`${card(id)}[data-answered="true"]`, { timeout: 15000 });
    await page.waitForTimeout(300);
  };

  try {
    // ================================================================ account A: the page itself
    const A = await account('practice');
    const pa = A.page;

    await check(D, 'An empty page shows one labelled example and no question of its own', async () => {
      await pa.goto(`${B}/Practice`);
      const s = await pa.evaluate(() => ({
        example: Boolean(document.querySelector('[data-practice-example]')),
        real: document.querySelectorAll('#practiceList [data-question-id]').length,
      }));
      assert(s.example, 'no example card on an empty practice page');
      assertEqual(s.real, 0, 'question cards on an empty page');
      return 'example shown, no real cards';
    });

    await check(D, 'The first "Get more" shows the progress card without a reload', async () => {
      await generate(pa);
      const n = (await ids(pa)).length;
      const p = await progress(pa);
      assert(p, `the progress card is missing after the first ${n} questions arrived (it only appears on reload)`);
      assertEqual(p.total, n, 'progress total vs cards on the page');
      return `${n} cards, progress ${p.answered}/${p.total}`;
    });

    await check(D, 'A second "Get more" updates the progress total in place (stale-card bug, generate)', async () => {
      await pa.goto(`${B}/Practice`);
      await generate(pa);
      const n = (await ids(pa)).length;
      const p = await progress(pa);
      assert(p, 'no progress card');
      assertEqual(p.total, n, 'progress total vs cards on the page, without a reload');
      return `${n} cards, progress ${p.answered}/${p.total}`;
    });

    let first;
    await check(D, 'Answering updates answered and average in place (stale-card bug, answer)', async () => {
      [first] = await ids(pa);
      const calls = stub.count('answer-feedback');
      await answer(pa, first);
      const p = await progress(pa);
      const avg = await pa.$eval('#practiceProgress .practice-progress-score', (e) => e.textContent.replace(/\s+/g, '')).catch(() => null);
      assertEqual(p.answered, 1, 'answered count on the progress card, without a reload');
      assert(avg && avg.startsWith('3'), `average on the progress card is "${avg}", the stub scores 3`);
      assertEqual(stub.count('answer-feedback') - calls, 1, 'model calls for one answer');
      return `progress ${p.answered}/${p.total}, average ${avg}`;
    });

    await check(D, 'The answered card shows its score and how long the answer took', async () => {
      const s = await pa.evaluate((sel) => ({
        score: document.querySelector(`${sel} .practice-score`)?.textContent.replace(/\s+/g, ''),
        elapsed: document.querySelector(`${sel} .practice-elapsed`)?.textContent.trim(),
      }), card(first));
      assertEqual(s.score, '3/5', 'score on the card');
      assert(s.elapsed, 'no elapsed-time label on a freshly answered card');
      return `score ${s.score}, took ${s.elapsed}`;
    });

    let drafted;
    await check(D, 'A typed answer survives a reload, and its draft is cleared once scored (the TDZ bug)', async () => {
      drafted = (await fresh(pa))[0];
      const text = 'A draft I have not submitted yet, long enough to be scored once it is.';
      await pa.fill(`${card(drafted)} textarea[name="answer"]`, text);
      await pa.waitForTimeout(200);
      await pa.reload();
      await pa.waitForTimeout(400);
      const restored = await pa.inputValue(`${card(drafted)} textarea[name="answer"]`);
      assertEqual(restored, text, 'draft after reload');
      await pa.click(`${card(drafted)} [data-practice-submit]`);
      await pa.waitForSelector(`${card(drafted)}[data-answered="true"]`, { timeout: 15000 });
      const left = await pa.evaluate((id) => localStorage.getItem('itai.practice.answer.' + id), drafted);
      assertEqual(left, null, 'draft left in localStorage after scoring');
      return 'restored after reload, cleared after scoring';
    });

    await check(D, 'A too-short answer cannot be sent, and a scripted one costs no model call', async () => {
      const id = (await fresh(pa))[0];
      await pa.fill(`${card(id)} textarea[name="answer"]`, 'Too short.');
      const ui = await pa.evaluate((sel) => ({
        disabled: document.querySelector(`${sel} [data-practice-submit]`).disabled,
        counter: document.querySelector(`${sel} [data-practice-counter]`).textContent.trim(),
      }), card(id));
      const calls = stub.count('answer-feedback');
      const res = await pa.evaluate(async (qid) => {
        const token = document.querySelector('[data-practice-token] [name="__RequestVerificationToken"]').value;
        const body = new URLSearchParams({ questionId: qid, answer: 'Too short.' });
        const r = await fetch('/Practice/SubmitAnswer', { method: 'POST', body, headers: { RequestVerificationToken: token, 'X-Requested-With': 'XMLHttpRequest' } });
        return r.json();
      }, id);
      await pa.fill(`${card(id)} textarea[name="answer"]`, '');
      assert(ui.disabled, 'Get feedback is enabled for a 10-character answer');
      assert(/10 \/ 40/.test(ui.counter), `counter reads "${ui.counter}"`);
      assert(res.success === false, 'the server accepted a 10-character answer');
      assertEqual(stub.count('answer-feedback') - calls, 0, 'model calls for a refused answer');
      return `button disabled, counter "${ui.counter}", server said: ${res.error}`;
    });

    await check(D, 'A freshly scored card is open, the same card after a reload is collapsed, and expand/collapse all work', async () => {
      const id = (await fresh(pa))[1];
      await answer(pa, id);
      const justScored = await pa.$eval(`${card(id)} details[data-practice-feedback]`, (d) => d.open);
      await pa.reload();
      await pa.waitForTimeout(300);
      const reloaded = await pa.$eval(`${card(id)} details[data-practice-feedback]`, (d) => d.open);
      await pa.click('[data-practice-toggle-all="expand"]');
      const allOpen = await pa.$$eval('[data-practice-feedback]', (ds) => ds.every((d) => d.open));
      await pa.click('[data-practice-toggle-all="collapse"]');
      const allShut = await pa.$$eval('[data-practice-feedback]', (ds) => ds.every((d) => !d.open));
      assert(justScored, 'the card just scored came back collapsed');
      assert(!reloaded, 'a scored card is expanded after a reload');
      assert(allOpen && allShut, `expand all ${allOpen}, collapse all ${allShut}`);
      return 'open → collapsed on reload → expand all → collapse all';
    });

    await check(D, 'Retry: a second attempt shows both attempts side by side', async () => {
      await pa.click(`${card(first)} details[data-practice-feedback] > summary`);
      await pa.click(`${card(first)} [data-practice-retry]`);
      await answer(pa, first, 'A second, better attempt that names the outcome, the people involved and the number it moved.');
      const cols = await pa.$$eval(`${card(first)} .practice-compare-col`, (c) => c.length);
      assertEqual(cols, 2, 'attempt columns after one retry');
      return '2 attempts side by side';
    });

    let starred;
    await check(D, 'Starring a question persists and the Saved filter shows only starred questions', async () => {
      const unanswered = await fresh(pa);
      starred = unanswered[unanswered.length - 1];
      await pa.click(`${card(starred)} [data-practice-star]`);
      await pa.waitForTimeout(500);
      const pressed = await pa.$eval(`${card(starred)} [data-practice-star]`, (b) => b.getAttribute('aria-pressed'));
      await pa.click('a.practice-filter:has-text("Saved")');
      await pa.waitForLoadState('domcontentloaded');
      const shown = await ids(pa);
      await pa.goto(`${B}/Practice`);
      assertEqual(pressed, 'true', 'star state after clicking');
      assertEqual(JSON.stringify(shown), JSON.stringify([starred]), 'cards under the Saved filter');
      return `question ${starred} saved and filtered`;
    });

    await check(D, 'A model error shows on the card and keeps the typed answer', async () => {
      const id = (await fresh(pa)).find((x) => x !== starred);
      stub.failNext(1, 500);
      await pa.fill(`${card(id)} textarea[name="answer"]`, LONG);
      await pa.click(`${card(id)} [data-practice-submit]`);
      await pa.waitForSelector(`${card(id)} [data-practice-answer-error]:not([hidden])`, { timeout: 15000 });
      const s = await pa.evaluate((sel) => ({
        answered: document.querySelector(sel).dataset.answered,
        text: document.querySelector(`${sel} textarea[name="answer"]`).value,
        error: document.querySelector(`${sel} [data-practice-answer-error]`).textContent.trim(),
      }), card(id));
      await pa.fill(`${card(id)} textarea[name="answer"]`, '');
      assertEqual(s.answered, 'false', 'card state after a failed score');
      assertEqual(s.text, LONG, 'typed answer after a failed score');
      return `error shown: "${s.error}"`;
    });

    await check(D, 'The dashboard practice card reports the same numbers as the practice page', async () => {
      await pa.goto(`${B}/Practice`);
      const p = await progress(pa);
      await pa.goto(`${B}/Home/Dashboard`);
      const dash = await pa.$eval('[data-practice-stat="answered"]', (e) => e.textContent.replace(/\s+/g, ''));
      assertEqual(dash, `${p.answered}/${p.total}`, 'dashboard answered/total');
      return `both say ${dash}`;
    });

    await check(D, '"Clear unanswered" keeps answered and starred questions, and the page agrees with itself afterwards', async () => {
      await pa.goto(`${B}/Practice`);
      const before = await pa.$$eval(CARD, (cs) => cs.map((c) => ({ id: c.dataset.questionId, answered: c.dataset.answered === 'true' })));
      await pa.click('.practice-manage-summary');
      await Promise.all([pa.waitForNavigation(), pa.click('form[action*="ClearUnanswered"] button[type=submit]')]);
      const after = await ids(pa);
      const expected = before.filter((c) => c.answered || c.id === starred).map((c) => c.id).sort();
      const p = await progress(pa);
      assertEqual(JSON.stringify([...after].sort()), JSON.stringify(expected), 'questions left');
      assertEqual(p.total, after.length, 'progress total vs cards left');
      return `${before.length} → ${after.length} (answered + 1 starred kept)`;
    });

    await check(D, '"Delete all" asks first, then empties the page back to the example', async () => {
      await pa.click('.practice-manage-summary');
      await pa.click('form[action*="DeleteAll"] button[type=submit]');
      const asked = await pa.evaluate(() => document.getElementById('app-confirm')?.classList.contains('active'));
      await Promise.all([pa.waitForNavigation(), pa.click('#app-confirm-ok')]);
      const s = await pa.evaluate(() => ({
        real: document.querySelectorAll('#practiceList [data-question-id]').length,
        example: Boolean(document.querySelector('[data-practice-example]')),
      }));
      assert(asked, 'no confirm dialog before deleting everything');
      assertEqual(s.real, 0, 'questions left after Delete all');
      assert(s.example, 'the empty page shows no example');
      return 'confirmed, emptied, example back';
    });
    await A.context.close();

    // ================================================================ account B: batch scoring at the limit
    await check(D, `Batch scoring at the practice limit (${PRACTICE_LIMIT}): the refused answer says so, the rest are stored, progress refreshes once`, async () => {
      const { context, page } = await account('batch');
      await page.goto(`${B}/Practice`);
      const GENERATES = 3;                                   // each "Get more" is one practice request
      for (let i = 0; i < GENERATES; i++) await generate(page);
      await page.goto(`${B}/Practice`);
      const all = await ids(page);
      const room = PRACTICE_LIMIT - GENERATES;
      const typed = all.slice(0, room + 1);                  // one more than the allowance has room for
      for (const id of typed) await page.fill(`${card(id)} textarea[name="answer"]`, LONG);
      const calls = stub.count('answer-feedback');
      await page.click('#practiceBatchBtn');
      await page.waitForFunction(() => /Scored/.test(document.getElementById('practiceBatchNote')?.textContent || ''), null, { timeout: 30000 });
      await page.waitForTimeout(800);                        // the one progress refresh after the batch
      const note = await page.textContent('#practiceBatchNote');
      const answered = await page.$$eval(`${CARD}[data-answered="true"]`, (c) => c.length);
      const refused = await page.$$eval(`${CARD} [data-practice-answer-error]:not([hidden])`, (e) => e.map((x) => x.textContent.trim()));
      const p = await progress(page);
      await context.close();
      assertEqual(answered, room, 'answers stored');
      assertEqual(refused.length, 1, 'cards showing their own error');
      assert(/limit/i.test(refused[0]), `the refused card says "${refused[0]}"`);
      assert(new RegExp(`Scored ${room} of ${room + 1}`).test(note), `batch note reads "${note}"`);
      assertEqual(p.answered, room, 'progress card answered count after the batch, without a reload');
      assertEqual(stub.count('answer-feedback') - calls, room, 'model calls — the refused one must not reach the model');
      return `${note.trim()} | refused card: "${refused[0]}" | progress ${p.answered}/${p.total}`;
    });

    // ================================================================ account C: the interview prep page
    await check(D, 'Prep page: regenerate appends, a regenerate with nothing new keeps everything, and typing survives', async () => {
      const { context, page } = await account('prep');
      await createApplication(page, { CompanyName: 'Stubco', RoleTitle: 'Backend Intern', JobDescription: 'Backend intern. Go and Postgres. Payments.' }, { base: B });
      await page.goto(`${B}/JobApplications?view=list`);
      const appId = await page.$eval('[data-app-id]', (e) => e.dataset.appId);
      const prep = `${B}/InterviewPrep/Prep?appId=${appId}`;
      const gen = async () => {
        await page.click('#prepGenerateBtn');
        await page.waitForFunction(() => !document.getElementById('prepGenerateBtn').disabled && !document.getElementById('prepNote').hidden, null, { timeout: 15000 });
      };
      await page.goto(prep);
      await gen();
      const firstBatch = await ids(page);
      await answer(page, firstBatch[0]);
      await gen();
      await gen();
      const twelve = await ids(page);
      const keep = `${card(twelve[5])} textarea[name="answer"]`;
      await page.fill(keep, 'Typing in a card while regenerating must not lose this text.');
      await gen();                                           // the stub's fourth batch repeats its first
      const after = await ids(page);
      const note = await page.textContent('#prepNote');
      const counts = await page.$$eval('[data-prep-category]', (ss) => ss.every((s) => s.querySelector('[data-prep-count]').textContent.startsWith(String(s.querySelectorAll('.practice-card').length) + ' ')));
      const kept = await page.inputValue(keep);
      const scored = await page.$(`${card(firstBatch[0])}[data-answered="true"]`);
      await page.goto(`${B}/Practice`);
      const onPractice = await page.$(`${card(firstBatch[0])}[data-answered="true"]`);
      await context.close();
      assertEqual(firstBatch.length, 4, 'first generate');
      assertEqual(twelve.length, 12, 'after three generates');
      assertEqual(after.length, 12, 'after a generate that stored nothing');
      assert(/No new questions/.test(note), `note reads "${note}"`);
      assert(counts, 'a section header count disagrees with its cards');
      assert(kept.startsWith('Typing in a card'), 'typed text was lost on regenerate');
      assert(scored, 'the scored prep card lost its answered state on regenerate');
      assert(onPractice, 'an answer given on the prep page is not answered on /Practice');
      return `4 → 12 → 12 ("${note.trim()}"), typed text and scored card kept, answer visible on /Practice`;
    });

    await check(D, 'No console errors or uncaught exceptions anywhere in the practice run', async () => {
      assert(errors.length === 0, errors.slice(0, 10).join('\n'));
      return 'none';
    });
  } finally {
    await app.stop();
    await browser.close();
  }
}
