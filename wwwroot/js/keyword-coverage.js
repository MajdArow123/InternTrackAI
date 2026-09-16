// Keyword coverage (Views/Shared/_KeywordCoverage.cshtml): POSTs to /JobApplications/KeywordCoverage and lists the
// posting terms the active resume doesn't literally contain, each chip carrying the posting sentence it came from in
// a popover. No AI call behind it, so there is no rate limit to handle — only network and 404.
//
// Every [data-keyword-coverage] root on the page is wired independently, so the drawer, Edit and the Create form can
// all use the same markup. A root with data-app-id loads itself; one without waits to be handed a posting:
//
//   window.keywordCoverage.load(rootEl, { appId: 12 })                                 // drawer, on open
//   window.keywordCoverage.load(rootEl, { description, company, role, location })      // Create, on blur
//
// Posting text is untrusted, so chips and popover bodies are built node by node with textContent, never innerHTML.
(function () {
    const roots = Array.from(document.querySelectorAll('[data-keyword-coverage]'));
    if (!roots.length) return;

    function token() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    }

    function setup(root) {
        const loading   = root.querySelector('.js-keyword-loading');
        const reasonEl  = root.querySelector('.js-keyword-reason');
        const body      = root.querySelector('.js-keyword-body');
        const countEl   = root.querySelector('.js-keyword-count');
        const chips     = root.querySelector('.js-keyword-chips');
        const footnote  = root.querySelector('.js-keyword-footnote');
        const errorBox  = root.querySelector('.js-keyword-error');
        const errorText = root.querySelector('.js-keyword-error-text');
        const retryBtn  = root.querySelector('.js-keyword-retry');
        const statusEl  = root.querySelector('.js-keyword-status');

        let controller = null;
        let seq = 0;
        let last = null;          // the payload to re-send when Retry is pressed
        let loadedOnce = false;
        let popovers = [];

        function disposePopovers() {
            popovers.forEach(function (p) { try { p.dispose(); } catch (_) { /* already gone */ } });
            popovers = [];
        }

        function setBusy(on) {
            root.setAttribute('aria-busy', on ? 'true' : 'false');
            // The skeleton is for the first load only; a refresh dims what is already there instead of
            // replacing a real answer with a placeholder.
            loading.hidden = !on || loadedOnce;
            body.classList.toggle('is-updating', on && loadedOnce);
            if (on) statusEl.textContent = 'Checking keyword coverage…';
        }

        function showError(message) {
            disposePopovers();
            errorText.textContent = message || 'Could not check keyword coverage. Try again.';
            errorBox.hidden = false;
            statusEl.textContent = '';
        }

        function showReason(reason) {
            disposePopovers();
            body.hidden = true;
            reasonEl.textContent = reason;
            reasonEl.hidden = false;
            statusEl.textContent = reason;
        }

        function render(data) {
            disposePopovers();
            reasonEl.hidden = true;
            errorBox.hidden = true;

            if (!data.available) {
                showReason(data.reason || 'Nothing to check yet.');
                return;
            }

            countEl.textContent = 'Your resume covers ' + data.covered + ' of ' + data.total +
                ' term' + (data.total === 1 ? '' : 's') + ' from this posting.';

            chips.replaceChildren();
            (data.missing || []).forEach(function (term) {
                chips.appendChild(chipFor(term));
            });

            const anyNearMiss = (data.missing || []).some(function (m) { return m.nearMiss; });
            footnote.textContent = anyNearMiss
                ? 'A term inside a longer word doesn’t count — a scanner matching the exact term won’t find it there.'
                : '';
            footnote.hidden = !anyNearMiss;

            body.hidden = false;
            statusEl.textContent = data.missing && data.missing.length
                ? data.missing.length + ' terms missing from your resume.'
                : 'Your resume covers every term in this posting.';
        }

        function chipFor(term) {
            const li = document.createElement('li');

            const chip = document.createElement('button');
            chip.type = 'button';
            chip.className = 'skill-tag skill-tag--keyword';
            chip.textContent = term.term;

            const popover = new bootstrap.Popover(chip, {
                trigger: 'focus hover',
                placement: 'top',
                container: 'body',
                customClass: 'keyword-popover',
                title: term.term,
                content: popoverBody(term)
            });
            popovers.push(popover);

            li.appendChild(chip);
            return li;
        }

        // Built as a detached element, never an HTML string: the context line is posting text the user pasted.
        function popoverBody(term) {
            const wrap = document.createElement('div');

            if (term.context) {
                const quote = document.createElement('p');
                quote.className = 'keyword-popover-context';
                quote.textContent = term.context;
                wrap.appendChild(quote);
            }

            if (term.nearMiss) {
                const near = document.createElement('p');
                near.className = 'keyword-popover-near';
                near.textContent = 'Your resume has “' + term.nearMiss + '”. A scanner looking for the exact term “' +
                    term.term + '” won’t match it there.';
                wrap.appendChild(near);
            }

            return wrap;
        }

        function load(payload) {
            last = payload;
            controller?.abort();
            controller = new AbortController();
            const mine = ++seq;

            errorBox.hidden = true;
            setBusy(true);

            const form = new URLSearchParams();
            if (payload.appId != null) form.set('AppId', payload.appId);
            if (payload.description != null) form.set('Description', payload.description);
            if (payload.company != null) form.set('Company', payload.company);
            if (payload.role != null) form.set('Role', payload.role);
            if (payload.location != null) form.set('Location', payload.location);
            form.set('__RequestVerificationToken', token());

            fetch('/JobApplications/KeywordCoverage', {
                method: 'POST',
                signal: controller.signal,
                headers: {
                    'Content-Type': 'application/x-www-form-urlencoded',
                    'X-Requested-With': 'XMLHttpRequest'
                },
                body: form
            }).then(async function (r) {
                let data = null;
                try { data = await r.json(); } catch (_) { /* an HTML error page */ }
                if (!data) {
                    throw new Error(r.status === 404
                        ? 'This application could not be found.'
                        : 'Could not check keyword coverage. Try again.');
                }
                return data;
            }).then(function (data) {
                if (mine !== seq) return;      // a newer request already answered
                loadedOnce = true;
                setBusy(false);
                render(data);
            }).catch(function (err) {
                if (err && err.name === 'AbortError') return;
                if (mine !== seq) return;
                setBusy(false);
                showError(err && err.message);
            });
        }

        retryBtn.addEventListener('click', function () {
            if (last) load(last);
        });

        root.__keywordLoad = load;

        // A root that names an application loads itself; the others wait to be handed a posting.
        const appId = root.getAttribute('data-app-id');
        if (appId) load({ appId: appId });
    }

    roots.forEach(setup);

    window.keywordCoverage = {
        load: function (root, payload) {
            if (root && typeof root.__keywordLoad === 'function') root.__keywordLoad(payload);
        }
    };
})();
