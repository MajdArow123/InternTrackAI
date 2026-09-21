// Practice page (/Practice). Two jobs, both built the same way: the server renders question cards and
// this file moves them into place. "Get more" appends new ones; submitting an answer replaces the card
// it came from with its answered state. No page reload, and no client-side copy of the card markup.
(function () {
    'use strict';

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

    function show(el, text) {
        if (!text) { el.hidden = true; return; }
        el.textContent = text;
        el.hidden = false;
    }

    form.addEventListener('submit', function (e) {
        e.preventDefault();
        if (inFlight) return;

        setBusy(true);
        show(note, null);
        show(error, null);

        const body = new URLSearchParams(new FormData(form));

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
                    show(error, data.error || 'Could not generate questions. Try again.');
                    return;
                }

                show(note, data.note);

                if (!data.html || !data.added) return;

                // Newest first, matching the page's own ordering.
                const holder = document.createElement('div');
                holder.innerHTML = data.html;
                const added = Array.from(holder.children);

                added.reverse().forEach(function (card) {
                    card.classList.add('practice-card--new');
                    list.prepend(card);
                });

                if (empty) empty.hidden = true;

                // Scroll to the first new card rather than the top: the user asked for more
                // questions, so put them where they are looking.
                added[added.length - 1].scrollIntoView({ behavior: 'smooth', block: 'center' });
            })
            .catch(function (err) {
                show(error, err.message || 'Request failed. Check your connection and try again.');
            })
            .finally(function () { setBusy(false); });
    });
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
            if (cardForm) refresh(cardForm);
        });

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

                    card.replaceWith(replacement);
                    replacement.scrollIntoView({ behavior: 'smooth', block: 'center' });
                })
                .catch(function (err) {
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
