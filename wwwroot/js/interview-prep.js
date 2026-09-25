// Interview prep page: the Generate button, and nothing else.
//
// Answering, scoring, retries, drafts and stars are practice.js's, bound to #practiceList — the prep page
// renders the practice page's own card, so it gets all of that without a copy. This file posts to
// /InterviewPrep/Generate, which returns only the questions it stored, already rendered and grouped by
// category, and merges them into the sections on the page.
//
// Merging rather than replacing is the point. The inline script this replaced swapped the whole list
// for the response, so a regenerate that stored nothing blanked the page, and one that stored three
// showed only those three until a reload.
(function () {
    const btn = document.getElementById('prepGenerateBtn');
    const list = document.getElementById('practiceList');
    if (!btn || !list) return;

    const note      = document.getElementById('prepNote');
    const error     = document.getElementById('prepError');
    const shimmer   = document.getElementById('prepGenerating');
    const generated = document.getElementById('prepGeneratedAt');
    const token     = document.querySelector('[data-practice-token] [name="__RequestVerificationToken"]');

    // Server order, so a category that appears for the first time lands in its place, not at the end.
    const ORDER = ['Technical', 'Behavioral', 'CompanySpecific'];

    let inFlight = false;

    // The failures this page expects, each with the message the user sees. Anything else reaching the
    // catch below is a bug in this file and is re-thrown rather than dressed up as a network problem.
    function expected(message) {
        const err = new Error(message);
        err.expected = true;
        return err;
    }

    function setBusy(busy) {
        inFlight = busy;
        btn.disabled = busy;
        btn.innerHTML = busy
            ? '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> Generating…'
            : (list.querySelector('.practice-card') ? 'Regenerate' : 'Generate interview prep');
        if (shimmer) shimmer.hidden = !busy;
    }

    function show(el, text) {
        if (!el) return;
        el.textContent = text;
        el.hidden = !text;
    }

    function recount(section) {
        const count = section.querySelectorAll('[data-prep-list] > .practice-card').length;
        const label = section.querySelector('[data-prep-count]');
        if (label) label.textContent = count + ' question' + (count === 1 ? '' : 's');
    }

    /// Moves each incoming card into the section for its category, creating the section if this is
    /// the first question of that kind. Returns the cards that were added, in page order.
    function merge(html) {
        const holder = document.createElement('div');
        holder.innerHTML = html;
        const added = [];

        holder.querySelectorAll('[data-prep-category]').forEach(function (incoming) {
            const key = incoming.dataset.prepCategory;
            const existing = list.querySelector('[data-prep-category="' + key + '"]');
            const cards = Array.from(incoming.querySelectorAll('[data-prep-list] > .practice-card'));

            if (existing) {
                const target = existing.querySelector('[data-prep-list]');
                cards.forEach(function (card) { target.appendChild(card); });
                recount(existing);
            } else {
                const later = ORDER.slice(ORDER.indexOf(key) + 1)
                    .map(function (k) { return list.querySelector('[data-prep-category="' + k + '"]'); })
                    .find(Boolean);
                list.insertBefore(incoming, later || null);
            }
            added.push.apply(added, cards);
        });

        return added;
    }

    btn.addEventListener('click', function () {
        if (inFlight) return;

        show(error, '');
        show(note, '');
        setBusy(true);

        const headers = { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' };
        if (token) headers['RequestVerificationToken'] = token.value;

        fetch('/InterviewPrep/Generate', {
            method: 'POST',
            headers: headers,
            body: JSON.stringify({ appId: Number(btn.dataset.appId) })
        })
            .catch(function () { throw expected('Request failed. Check your connection and try again.'); })
            .then(function (res) {
                return res.json().catch(function () {
                    throw expected(res.status === 429
                        ? 'You have hit the AI request limit. Try again later.'
                        : 'Generation failed. Please try again.');
                });
            })
            .then(function (data) {
                if (!data.success) throw expected(data.error || 'Generation failed. Please try again.');

                if (!data.added) {
                    // The model answered, but everything it wrote repeats a question already here.
                    show(note, 'No new questions this time — everything generated repeated a question you already have.');
                    return;
                }

                const added = merge(data.html || '');
                show(note, 'Added ' + data.added + ' new question' + (data.added === 1 ? '' : 's') + '.');
                if (generated) generated.textContent = 'Generated just now. Regenerate to add more — questions you already have aren’t repeated.';

                // Restores any saved drafts into the new cards and refreshes practice.js's own state.
                if (window.practiceRefreshBatchBar) window.practiceRefreshBatchBar();

                if (added[0]) {
                    added[0].scrollIntoView({ behavior: 'smooth', block: 'center' });
                    added.forEach(function (card) {
                        card.classList.add('practice-card--flash');
                        setTimeout(function () { card.classList.remove('practice-card--flash'); }, 1200);
                    });
                }
            })
            .catch(function (err) {
                rethrowIfBug(err);
                if (!err.expected) {
                    show(error, 'Something went wrong showing the new questions. Reload the page to see them.');
                    throw err;
                }
                show(error, err.message);
            })
            .finally(function () { setBusy(false); });
    });
})();
