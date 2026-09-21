// Practice page (/Practice). "Get more" posts, the server returns rendered question cards, and we
// append them — no page reload, no client-side copy of the card markup.
(function () {
    'use strict';

    const form   = document.getElementById('practiceGenerateForm');
    if (!form) return;

    const btn    = document.getElementById('practiceGenerateBtn');
    const label  = document.getElementById('practiceGenerateLabel');
    const list   = document.getElementById('practiceList');
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
})();
