// Practice page (/Practice). Two jobs, both built the same way: the server renders question cards and
// this file moves them into place. "Get more" appends new ones; submitting an answer replaces the card
// it came from with its answered state. No page reload, and no client-side copy of the card markup.
(function () {
    'use strict';

    // ── Drafts and timing ────────────────────────────────────────────────────
    // Losing a long answer to an accidental reload is a bad moment, and localStorage costs nothing.
    // Every access is wrapped: a private window throws on read and write, and a lost draft must never
    // be the reason the page stops working.
    // Declared here, above the wireAnswering() call below, and not merely above its definition: these
    // are const, so reaching them from a call that runs earlier hits the temporal dead zone. The
    // try/catch that makes them safe in a private window would then swallow the ReferenceError and the
    // drafts would silently never restore, which is exactly what happened the first time.
    const DRAFT_PREFIX = 'itai.practice.answer.';

    function draftKey(id) { return DRAFT_PREFIX + id; }

    // Stricter than rethrowIfBug: inside a localStorage call neither ReferenceError nor TypeError is
    // an expected condition. A blocked or full store throws a DOMException (SecurityError,
    // QuotaExceededError), so anything else here is a bug and must not be disguised as "no draft".
    function storageBug(err) {
        if (err instanceof ReferenceError || err instanceof TypeError) throw err;
    }

    function readDraft(id) {
        try { return localStorage.getItem(draftKey(id)); }
        catch (err) { storageBug(err); return null; }
    }

    function saveDraft(id, text) {
        try {
            if (text && text.trim().length > 0) localStorage.setItem(draftKey(id), text);
            else localStorage.removeItem(draftKey(id));
        } catch (err) {
            storageBug(err);
            /* full, blocked, or private mode — the draft is a convenience, not a feature */
        }
    }

    function clearDraft(id) {
        try { localStorage.removeItem(draftKey(id)); } catch (err) { storageBug(err); }
    }

    // First keystroke per card, so the reported duration is time spent answering rather than time the
    // tab was open. Kept in memory only: a reload restores the draft but honestly forgets the clock.
    const startedAt = {};

    function noteTyping(id) {
        if (!startedAt[id]) startedAt[id] = Date.now();
    }

    function elapsedFor(id) {
        if (!startedAt[id]) return null;
        return Math.max(1, Math.round((Date.now() - startedAt[id]) / 1000));
    }

    // Mirrors PracticeAnswerService.MinAnswerChars. The server is the one that enforces it.
    const MIN_ANSWER_CHARS = 40;

    const form = document.getElementById('practiceGenerateForm');
    const list = document.getElementById('practiceList');

    if (list) wireAnswering(list);
    if (!form) return;

    const btn    = document.getElementById('practiceGenerateBtn');
    const label  = document.getElementById('practiceGenerateLabel');
    const note   = document.getElementById('practiceNote');
    const error  = document.getElementById('practiceError');
    const empty  = document.getElementById('practiceEmpty');

    // The button is disabled while in flight, but a double submit can still slip past a disabled
    // attribute (Enter twice, a fast double-click before paint). This is the actual guard.
    let inFlight = false;

    function setBusy(busy) {
        inFlight = busy;
        btn.disabled = busy;
        label.innerHTML = busy
            ? '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> Writing questions…'
            : 'Get more questions';
    }

    /// Shows a generate outcome in the banner, and as a toast when the banner is off-screen.
    ///
    /// The banner lives in the filter card at the top of the page, but "Get more" can now be pressed
    /// from the foot of the list or from a group header — hundreds of pixels below it. Pressing one of
    /// those and having the only feedback render off-screen looks exactly like the button doing
    /// nothing, which is how a rate-limited generate got mistaken for a broken progress card.
    function show(el, text, tone) {
        if (!text) { el.hidden = true; return; }
        el.textContent = text;
        el.hidden = false;

        const box = el.getBoundingClientRect();
        const onScreen = box.top >= 0 && box.bottom <= (window.innerHeight || document.documentElement.clientHeight);
        if (!onScreen && typeof showAppToast === 'function') showAppToast(tone || 'info', text);
    }

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        generate(null);
    });

    // The twin at the foot of the list. Same action, so it goes through the same guard and spinner.
    document.addEventListener('click', function (e) {
        if (e.target.closest('[data-practice-generate-more]')) generate(null);
    });

    // "Get more for this role" on a group header: the same request with that posting's id, so the
    // questions are derived from its description and come back into the same group.
    if (list) {
        list.addEventListener('click', function (e) {
            const btn = e.target.closest('[data-practice-generate-for]');
            if (!btn) return;
            generate(btn.dataset.practiceGenerateFor);
        });
    }

    function generate(applicationId) {
        if (inFlight) return;

        setBusy(true);
        show(note, null);
        show(error, null);

        const body = new URLSearchParams(new FormData(form));
        if (applicationId) body.set('applicationId', applicationId);

        fetch('/Practice/GenerateMore', {
            method: 'POST',
            body: body,
            headers: { 'X-Requested-With': 'XMLHttpRequest' }
        })
            .then(function (res) {
                // The shared "ai" rate limiter answers 429 with a JSON body for XHR callers.
                return res.json().catch(function () {
                    throw new Error(res.status === 429
                        ? 'You have hit the AI request limit. Try again later.'
                        : 'Could not generate questions. Try again.');
                });
            })
            .then(function (data) {
                if (!data.success) {
                    show(error, data.error || 'Could not generate questions. Try again.', 'error');
                    return;
                }

                show(note, data.note, 'info');

                if (!data.html || !data.added) return;

                // The new questions belong to a group — the posting they were generated for, or
                // general practice. If that group is not on the page yet there is nowhere correct to
                // put them, and building a group header here would be a second copy of markup the
                // server already owns, so reload instead. Rare: the group button always has its group.
                const target = list.querySelector(
                    '.practice-group[data-group-id="' + (applicationId || '') + '"] .practice-group-list');

                if (!target) { window.location.reload(); return; }

                // Newest first, matching the page's own ordering.
                const holder = document.createElement('div');
                holder.innerHTML = data.html;
                const added = Array.from(holder.children);

                added.reverse().forEach(function (card) {
                    card.classList.add('practice-card--new');
                    target.prepend(card);
                });

                if (empty) empty.hidden = true;

                // Scroll to the first new card rather than the top: the user asked for more
                // questions, so put them where they are looking.
                added[added.length - 1].scrollIntoView({ behavior: 'smooth', block: 'center' });
                if (window.practiceRefreshBatchBar) window.practiceRefreshBatchBar();
            })
            .catch(function (err) {
                rethrowIfBug(err);
                show(error, err.message || 'Request failed. Check your connection and try again.', 'error');
            })
            .finally(function () { setBusy(false); });
    }
    // ── Answering ────────────────────────────────────────────────────────────
    // One set of listeners on the list, not one per card: cards arrive from "Get more" and are replaced
    // wholesale after a submission, and delegation means neither case needs rebinding.
    function wireAnswering(list) {
        const token = document.querySelector('#practiceGenerateForm [name="__RequestVerificationToken"]');

        // Enables the button once there is something to grade, and keeps the counter honest. The server
        // enforces the same minimum — this is so the user is not told off after waiting for a round trip.
        function refresh(cardForm) {
            const input   = cardForm.querySelector('textarea[name="answer"]');
            const submit  = cardForm.querySelector('[data-practice-submit]');
            const counter = cardForm.querySelector('[data-practice-counter]');
            if (!input || !submit) return;

            const length = input.value.trim().length;
            submit.disabled = cardForm.dataset.inFlight === '1' || length < MIN_ANSWER_CHARS;

            if (counter) {
                counter.textContent = length < MIN_ANSWER_CHARS
                    ? length + ' / ' + MIN_ANSWER_CHARS + ' characters'
                    : length + ' characters';
            }
        }

        list.addEventListener('input', function (e) {
            const cardForm = e.target.closest('[data-practice-answer-form]');
            if (!cardForm) return;
            refresh(cardForm);
            refreshBar();

            const card = cardForm.closest('.practice-card');
            if (card) {
                noteTyping(card.dataset.questionId);
                saveDraft(card.dataset.questionId, e.target.value);
            }
        });

        // ── Collapse / expand ────────────────────────────────────────────────
        const bulk = document.getElementById('practiceBulkToggle');

        function refreshBulk() {
            if (bulk) bulk.hidden = list.querySelectorAll('[data-practice-feedback]').length === 0;
        }

        document.addEventListener('click', function (e) {
            const toggle = e.target.closest('[data-practice-toggle-all]');
            if (!toggle) return;
            const open = toggle.dataset.practiceToggleAll === 'expand';
            list.querySelectorAll('[data-practice-feedback]').forEach(function (d) { d.open = open; });
        });

        // The index in the progress card. Jumps to the first card of that kind rather than scrolling.
        document.addEventListener('click', function (e) {
            const jump = e.target.closest('[data-practice-jump]');
            if (!jump) return;
            e.preventDefault();

            const wantAnswered = jump.dataset.practiceJump === 'answered';
            const target = Array.from(list.querySelectorAll('.practice-card'))
                .find(function (c) { return (c.dataset.answered === 'true') === wantAnswered; });

            if (target) {
                target.scrollIntoView({ behavior: 'smooth', block: 'center' });
                target.classList.add('practice-card--flash');
                setTimeout(function () { target.classList.remove('practice-card--flash'); }, 1200);
            }
        });

        // Star toggle. The server returns the state it landed in rather than the state asked for, so a
        // fast double-click cannot leave the button and the row disagreeing.
        list.addEventListener('click', function (e) {
            const star = e.target.closest('[data-practice-star]');
            if (!star || star.dataset.inFlight === '1') return;

            const card = star.closest('.practice-card');
            if (!card) return;

            star.dataset.inFlight = '1';

            const body = new URLSearchParams();
            body.set('questionId', card.dataset.questionId);

            const headers = { 'X-Requested-With': 'XMLHttpRequest' };
            if (token) headers['RequestVerificationToken'] = token.value;

            fetch('/Practice/ToggleSaved', { method: 'POST', body: body, headers: headers })
                .then(function (res) { return res.json(); })
                .then(function (data) {
                    if (!data.success) throw new Error(data.error || 'Could not save that question.');
                    paintStar(star, data.saved);
                })
                .catch(function (err) {
                    rethrowIfBug(err);
                    // Nothing destructive happened, and an error banner on a star is heavier than the
                    // action deserves. The button simply stays as it was.
                    showAppToast('error', 'Could not save that question. Try again.');
                })
                .finally(function () { star.dataset.inFlight = '0'; });
        });

        // ── Batch scoring ────────────────────────────────────────────────────
        // Fans out to the same /Practice/SubmitAnswer the per-question button uses. No batch endpoint:
        // one response per card is what lets each result land on its own card as it arrives, partial
        // failure is then just one rejected promise, and the rate limit applies per call so the ones
        // that do not fit come back 429 and say so. A server-side batch would need streaming or a
        // single ~20s response.
        const BATCH_CONCURRENCY = 3;

        const bar   = document.getElementById('practiceBatchBar');
        const batchBtn   = document.getElementById('practiceBatchBtn');
        const batchLabel = document.getElementById('practiceBatchLabel');
        const batchNote  = document.getElementById('practiceBatchNote');
        let batchRunning = false;

        /// Cards with enough typed text to be worth a call, and not already scored.
        function pending() {
            return Array.from(list.querySelectorAll('.practice-card')).filter(function (card) {
                const cardForm = card.querySelector('[data-practice-answer-form]');
                const input = card.querySelector('textarea[name="answer"]');
                if (!cardForm || !input || cardForm.hidden) return false;
                return input.value.trim().length >= MIN_ANSWER_CHARS;
            });
        }

        function refreshBar() {
            if (!bar || batchRunning) return;
            const n = pending().length;
            bar.hidden = n === 0;
            if (n > 0) {
                batchLabel.textContent = 'Get feedback on all ' + n + ' answer' + (n === 1 ? '' : 's');
                batchBtn.disabled = false;
            }
        }

        /// Scores one card and swaps it in. Resolves with the outcome either way — a rejected call is
        /// this card's problem, never the batch's.
        function scoreCard(card) {
            const cardForm = card.querySelector('[data-practice-answer-form]');
            const input = card.querySelector('textarea[name="answer"]');
            const answer = input.value.trim();

            cardForm.hidden = true;
            const busy = document.createElement('p');
            busy.className = 'practice-card-busy';
            busy.innerHTML = '<span class="spinner-border spinner-border-sm" style="width:12px;height:12px;border-width:2px"></span> Scoring…';
            card.appendChild(busy);

            const body = new URLSearchParams();
            body.set('questionId', card.dataset.questionId);
            body.set('answer', answer);
            const elapsed = elapsedFor(card.dataset.questionId);
            if (elapsed) body.set('elapsedSeconds', elapsed);

            const headers = { 'X-Requested-With': 'XMLHttpRequest' };
            if (token) headers['RequestVerificationToken'] = token.value;

            return fetch('/Practice/SubmitAnswer', { method: 'POST', body: body, headers: headers })
                .then(function (res) {
                    return res.json().catch(function () {
                        throw new Error(res.status === 429
                            ? 'You have hit the practice limit, so this one did not run.'
                            : 'Could not score that answer.');
                    });
                })
                .then(function (data) {
                    if (!data.success || !data.html) throw new Error(data.error || 'Could not score that answer.');

                    const holder = document.createElement('div');
                    holder.innerHTML = data.html;
                    const replacement = holder.firstElementChild;
                    if (!replacement) throw new Error('Could not score that answer.');

                    // Deliberately ignoring data.progress here: the card is swapped once per answer,
                    // the progress card once at the end.
                    clearDraft(card.dataset.questionId);
                    card.replaceWith(replacement);
                    return true;
                })
                .catch(function (err) {
                    rethrowIfBug(err);
                    busy.remove();
                    cardForm.hidden = false;
                    const error = cardForm.querySelector('[data-practice-answer-error]');
                    if (error) {
                        error.textContent = err.message || 'Could not score that answer.';
                        error.hidden = false;
                    }
                    return false;
                });
        }

        // Appended and replaced cards change what is pending, and a restored draft can make the bar
        // relevant before the user types anything.
        /// Puts saved drafts back into empty boxes. Runs on load and after cards are appended.
        function restoreDrafts() {
            list.querySelectorAll('.practice-card').forEach(function (card) {
                const cardForm = card.querySelector('[data-practice-answer-form]');
                const input = card.querySelector('textarea[name="answer"]');
                if (!cardForm || !input || input.value.trim().length > 0) return;

                const draft = readDraft(card.dataset.questionId);
                if (!draft) return;

                input.value = draft;
                refresh(cardForm);
            });
        }

        window.practiceRefreshBatchBar = function () { restoreDrafts(); refreshBar(); refreshBulk(); };
        restoreDrafts();
        refreshBar();
        refreshBulk();

        if (batchBtn) {
            batchBtn.addEventListener('click', function () {
                if (batchRunning) return;

                const cards = pending();
                if (cards.length === 0) return;

                batchRunning = true;
                batchBtn.disabled = true;
                batchLabel.innerHTML = '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> Scoring ' + cards.length + '…';
                batchNote.textContent = '';

                let scored = 0;
                let failed = 0;
                let next = 0;

                // A small pool rather than all at once: five answers become two waves instead of five
                // serial calls, without opening fifteen sockets or hammering the limiter in one burst.
                function worker() {
                    if (next >= cards.length) return Promise.resolve();
                    const card = cards[next++];
                    return scoreCard(card).then(function (ok) {
                        if (ok) scored++; else failed++;
                        return worker();
                    });
                }

                const pool = [];
                for (let i = 0; i < Math.min(BATCH_CONCURRENCY, cards.length); i++) pool.push(worker());

                Promise.all(pool).then(function () {
                    batchRunning = false;
                    batchLabel.textContent = 'Get feedback on all answers';

                    batchNote.textContent = failed === 0
                        ? 'Scored ' + scored + '.'
                        : 'Scored ' + scored + ' of ' + (scored + failed) + '. ' + failed +
                          " didn't run — see the message on each card.";

                    // Once, after everything has settled, so the numbers don't jitter as calls land.
                    fetch('/Practice/Progress', { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                        .then(function (r) { return r.ok ? r.text() : null; })
                        .then(function (html) {
                            if (!html) return;
                            const current = document.getElementById('practiceProgress');
                            if (!current) return;
                            const holder = document.createElement('div');
                            holder.innerHTML = html;
                            const fresh = holder.firstElementChild;
                            if (fresh) current.replaceWith(fresh);
                        })
                        .catch(function () { /* the cards are already right; the card can wait for a reload */ })
                        .finally(refreshBar);
                });
            });
        }

        function paintStar(star, saved) {
            const label = saved ? 'Saved — click to unsave' : 'Save this question';
            star.classList.toggle('is-saved', saved);
            star.setAttribute('aria-pressed', saved ? 'true' : 'false');
            star.setAttribute('aria-label', label);
            star.setAttribute('title', label);
            star.textContent = saved ? '★' : '☆';
        }

        // "Retry this question" is purely local: reveal the form again. The attempt on screen only moves
        // into the history list when the next one is submitted, so there is nothing to ask the server.
        list.addEventListener('click', function (e) {
            const retry = e.target.closest('[data-practice-retry]');
            if (!retry) return;

            const card = retry.closest('.practice-card');
            const cardForm = card && card.querySelector('[data-practice-answer-form]');
            if (!cardForm) return;

            cardForm.hidden = false;
            refresh(cardForm);
            const input = cardForm.querySelector('textarea[name="answer"]');
            if (input) input.focus();
        });

        list.addEventListener('submit', function (e) {
            const cardForm = e.target.closest('[data-practice-answer-form]');
            if (!cardForm) return;

            e.preventDefault();

            // Per-card, for the same reason the generate button has one: a disabled attribute does not
            // stop a second Enter landing before the first request returns.
            if (cardForm.dataset.inFlight === '1') return;

            const card   = cardForm.closest('.practice-card');
            const input  = cardForm.querySelector('textarea[name="answer"]');
            const submit = cardForm.querySelector('[data-practice-submit]');
            const label  = cardForm.querySelector('[data-practice-submit-label]');
            const error  = cardForm.querySelector('[data-practice-answer-error]');
            if (!card || !input) return;

            const answer = input.value.trim();
            if (answer.length < MIN_ANSWER_CHARS) return;

            cardForm.dataset.inFlight = '1';
            if (submit) submit.disabled = true;
            if (label) label.innerHTML = '<span class="spinner-border spinner-border-sm" style="width:12px;height:12px;border-width:2px"></span> Scoring…';
            if (error) error.hidden = true;

            const body = new URLSearchParams();
            body.set('questionId', card.dataset.questionId);
            body.set('answer', answer);
            const elapsed = elapsedFor(card.dataset.questionId);
            if (elapsed) body.set('elapsedSeconds', elapsed);

            const headers = { 'X-Requested-With': 'XMLHttpRequest' };
            if (token) headers['RequestVerificationToken'] = token.value;

            fetch('/Practice/SubmitAnswer', { method: 'POST', body: body, headers: headers })
                .then(function (res) {
                    return res.json().catch(function () {
                        throw new Error(res.status === 429
                            ? 'You have hit the AI request limit. Try again later.'
                            : 'Could not score that answer. Try again.');
                    });
                })
                .then(function (data) {
                    if (!data.success || !data.html) {
                        throw new Error(data.error || 'Could not score that answer. Try again.');
                    }

                    // The server returns the whole card in its answered state, so there is no client-side
                    // copy of that markup to keep in step.
                    const holder = document.createElement('div');
                    holder.innerHTML = data.html;
                    const replacement = holder.firstElementChild;
                    if (!replacement) throw new Error('Could not score that answer. Try again.');

                    clearDraft(card.dataset.questionId);
                    card.replaceWith(replacement);

                    // Answering changes every number on the progress card, so the server re-renders it
                    // and we swap it too. Without this it keeps showing the pre-answer figures until a
                    // reload, which is worse than showing nothing: they still look authoritative.
                    if (data.progress) {
                        const current = document.getElementById('practiceProgress');
                        if (current) {
                            const holder2 = document.createElement('div');
                            holder2.innerHTML = data.progress;
                            const fresh = holder2.firstElementChild;
                            if (fresh) current.replaceWith(fresh);
                        }
                    }

                    replacement.scrollIntoView({ behavior: 'smooth', block: 'center' });
                    if (window.practiceRefreshBatchBar) window.practiceRefreshBatchBar();
                })
                .catch(function (err) {
                    rethrowIfBug(err);
                    cardForm.dataset.inFlight = '0';
                    if (label) label.textContent = 'Get feedback';
                    refresh(cardForm);
                    if (error) {
                        error.textContent = err.message || 'Request failed. Check your connection and try again.';
                        error.hidden = false;
                    }
                });
        });
    }
})();
