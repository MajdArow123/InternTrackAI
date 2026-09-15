// "Draft follow-up": any button with data-followup-open + data-app-id (dashboard Attention card, detail drawer on the
// list and board) opens the modal in Views/Shared/_FollowUpModal.cshtml, which POSTs /JobApplications/{id}/followup
// for a draft and /followup/improve to revise it. Nothing is saved; the user copies the email out. Model output is
// only ever written with textContent/value. Keyboard: focus moves in on open, Tab is trapped, Escape closes, focus
// returns to the opening button. Needs an antiforgery token input on the page.
(function () {
    const overlay = document.getElementById('followup-overlay');
    if (!overlay) return;

    const dialog      = document.getElementById('followup-dialog');
    const contextEl   = document.getElementById('followup-context');
    const statusEl    = document.getElementById('followup-status');
    const loading     = document.getElementById('followup-loading');
    const errorBox    = document.getElementById('followup-error');
    const errorText   = document.getElementById('followup-error-text');
    const retryBtn    = document.getElementById('followup-retry');
    const demoNote    = document.getElementById('followup-demo-note');
    const editor      = document.getElementById('followup-editor');
    const subjectIn   = document.getElementById('followup-subject');
    const bodyIn      = document.getElementById('followup-text');
    const words       = document.getElementById('followup-words');
    const instruction = document.getElementById('followup-instruction');
    const regenBtn    = document.getElementById('followup-regenerate');
    const restricted  = document.getElementById('followup-demo-restricted');
    const copyBtn     = document.getElementById('followup-copy');
    const copySubject = document.getElementById('followup-copy-subject');

    let appId = null;
    let opener = null;
    let lastAction = null;      // retried by the Retry button
    let controller = null;      // aborts the in-flight request on close / reopen
    let busy = false;
    let focusRetry = false;     // set by showError; applied once the buttons are enabled again
    let refocus = null;         // the button that was focused when it got disabled for a request
    let seq = 0;                // request generation: responses from an older request are ignored
    let bodyOverflow = '';

    function token() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    }

    function isOpen() { return overlay.classList.contains('active'); }

    // ── Open / close ─────────────────────────────────
    function open(btn) {
        appId  = btn.dataset.appId;
        opener = btn;
        const company = btn.dataset.company || '';
        const role    = btn.dataset.role || '';
        contextEl.textContent = [company, role].filter(Boolean).join(' — ');

        subjectIn.value = '';
        bodyIn.value = '';
        instruction.value = '';
        demoNote.hidden = true;
        restricted.hidden = true;
        editor.hidden = true;
        hideError();

        bodyOverflow = document.body.style.overflow;
        document.body.style.overflow = 'hidden';
        overlay.classList.add('active');
        overlay.setAttribute('aria-hidden', 'false');
        dialog.focus();
        generate();
    }

    function close() {
        if (!isOpen()) return;
        seq++;
        controller?.abort();
        controller = null;
        setBusy(false);
        loading.hidden = true;
        overlay.classList.remove('active');
        overlay.setAttribute('aria-hidden', 'true');
        document.body.style.overflow = bodyOverflow;
        const back = opener;
        opener = null;
        if (back && back.isConnected && !back.closest('[hidden]')) back.focus();
    }

    // ── Requests ─────────────────────────────────────
    function post(url, payload) {
        controller?.abort();
        controller = new AbortController();
        const signal = controller.signal;
        return fetch(url, {
            method: 'POST',
            signal: signal,
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token(),
                'X-Requested-With': 'XMLHttpRequest'
            },
            body: JSON.stringify(payload || {})
        }).then(async function (r) {
            let data = null;
            try { data = await r.json(); } catch (_) { /* HTML error page */ }
            if (r.status === 429 && data && data.error && typeof showAppToast === 'function') showAppToast('error', data.error);
            if (!data) data = { success: false, error: r.status === 404 ? 'This application could not be found.' : 'Something went wrong. Try again.' };
            return data;
        });
    }

    function generate() {
        lastAction = generate;
        const hadDraft = !editor.hidden;
        hideError();
        restricted.hidden = true;
        setBusy(true, hadDraft ? 'Writing a fresh draft…' : 'Drafting your follow-up…');
        if (!hadDraft) loading.hidden = false;
        const mine = ++seq;

        post('/JobApplications/' + encodeURIComponent(appId) + '/followup')
            .then(function (data) {
                if (mine !== seq) return;
                if (!data.success) return showError(data.error);
                demoNote.hidden = !data.demo;
                showDraft(data, hadDraft);
            })
            .catch(function (err) { if (mine === seq) onNetworkError(err); })
            .finally(function () { if (mine === seq) done(); });
    }

    function improve() {
        const text = instruction.value.trim();
        if (!text) return generate();
        lastAction = improve;
        hideError();
        restricted.hidden = true;
        setBusy(true, 'Revising the draft…');
        const mine = ++seq;

        post('/JobApplications/' + encodeURIComponent(appId) + '/followup/improve', {
            subject: subjectIn.value, body: bodyIn.value, instruction: text
        })
            .then(function (data) {
                if (mine !== seq) return;
                if (data.demoRestricted) {
                    restricted.textContent = data.error + ' Sign up to revise drafts with AI.';
                    restricted.hidden = false;
                    restricted.scrollIntoView({ block: 'nearest' });
                    return;
                }
                if (!data.success) return showError(data.error);
                instruction.value = '';
                showDraft(data, true);
            })
            .catch(function (err) { if (mine === seq) onNetworkError(err); })
            .finally(function () { if (mine === seq) done(); });
    }

    function onNetworkError(err) {
        if (err && err.name === 'AbortError') return;
        showError('Request failed. Check your connection and try again.');
    }

    function done() {
        loading.hidden = true;
        setBusy(false);
        if (isOpen() && focusRetry) retryBtn.focus();
        else if (isOpen() && refocus && document.activeElement === dialog && !refocus.closest('[hidden]')) refocus.focus();
        focusRetry = false;
        refocus = null;
    }

    // ── Rendering ────────────────────────────────────
    function showDraft(data, replacing) {
        subjectIn.value = data.subject || '';
        bodyIn.value = data.body || '';
        updateWords();
        editor.hidden = false;
        copyBtn.disabled = false;
        statusEl.textContent = replacing ? 'Draft updated.' : 'Draft ready.';
        if (replacing) {
            editor.classList.remove('followup-replaced');
            void editor.offsetWidth;           // restart the fade
            editor.classList.add('followup-replaced');
        } else {
            subjectIn.focus();
            subjectIn.setSelectionRange(0, 0);  // start of the line, so a long subject isn't shown scrolled to its end
            subjectIn.scrollLeft = 0;
        }
    }

    function showError(message) {
        errorText.textContent = message || 'Could not draft the email. Try again.';
        errorBox.hidden = false;
        errorBox.scrollIntoView({ block: 'nearest' });
        copyBtn.disabled = editor.hidden;
        statusEl.textContent = '';
        focusRetry = true;
    }

    function hideError() {
        errorBox.hidden = true;
        errorText.textContent = '';
    }

    function setBusy(on, label) {
        busy = on;
        if (on && (document.activeElement === regenBtn || document.activeElement === retryBtn)) {
            refocus = document.activeElement;
            dialog.focus();                     // a disabled button would drop focus out of the dialog
        }
        regenBtn.disabled = on;
        retryBtn.disabled = on;
        bodyIn.readOnly = on;
        subjectIn.readOnly = on;
        editor.classList.toggle('is-updating', on && !editor.hidden);
        dialog.setAttribute('aria-busy', on ? 'true' : 'false');
        if (on) {
            copyBtn.disabled = true;
            statusEl.textContent = label || '';
            regenBtn.innerHTML = '<span class="spinner-border spinner-border-sm analyze-spinner" aria-hidden="true"></span> ' +
                (instruction.value.trim() ? 'Improving…' : 'Regenerating…');
        } else {
            copyBtn.disabled = editor.hidden;
            regenBtn.textContent = 'Regenerate';
        }
    }

    function updateWords() {
        const n = bodyIn.value.trim() ? bodyIn.value.trim().split(/\s+/).length : 0;
        words.textContent = n + (n === 1 ? ' word' : ' words');
    }

    // ── Copy ─────────────────────────────────────────
    async function copy(text, message, fallbackField) {
        try {
            await navigator.clipboard.writeText(text);
        } catch (_) {
            fallbackField.select();
            document.execCommand('copy');
        }
        if (typeof showAppToast === 'function') showAppToast('success', message);
    }

    copyBtn.addEventListener('click', function () {
        const subject = subjectIn.value.trim();
        const text = (subject ? 'Subject: ' + subject + '\n\n' : '') + bodyIn.value.trim();
        copy(text, 'Email copied to clipboard.', bodyIn);
    });
    copySubject.addEventListener('click', function () {
        copy(subjectIn.value.trim(), 'Subject copied to clipboard.', subjectIn);
    });

    // ── Events ───────────────────────────────────────
    bodyIn.addEventListener('input', updateWords);
    regenBtn.addEventListener('click', function () { if (!busy) improve(); });
    retryBtn.addEventListener('click', function () { if (!busy && lastAction) lastAction(); });
    document.getElementById('followup-close').addEventListener('click', close);
    document.getElementById('followup-cancel').addEventListener('click', close);
    overlay.addEventListener('click', function (e) { if (e.target === overlay) close(); });

    document.addEventListener('click', function (e) {
        const btn = e.target.closest('[data-followup-open]');
        if (!btn || !btn.dataset.appId) return;
        e.preventDefault();
        e.stopPropagation();
        open(btn);
    });

    function focusables() {
        return Array.from(dialog.querySelectorAll('button, input, textarea, [href], select, [tabindex]:not([tabindex="-1"])'))
            .filter(function (el) { return !el.disabled && !el.closest('[hidden]') && el.getClientRects().length > 0; });
    }

    // Window capture phase so the drawer's own Escape handler, the board's keys and the global shortcuts
    // (N, D, C, P, S, ?) never see a key pressed while the modal is open. Typing is unaffected: only
    // propagation stops, never the default action.
    window.addEventListener('keydown', function (e) {
        if (!isOpen()) return;
        if (e.key === 'Escape') {
            e.preventDefault();
            close();
        } else if (e.key === 'Tab') {
            const items = focusables();
            if (!items.length) { e.preventDefault(); dialog.focus(); }
            else {
                const first = items[0], last = items[items.length - 1];
                const inside = dialog.contains(document.activeElement) && document.activeElement !== dialog;
                if (e.shiftKey && (!inside || document.activeElement === first)) { e.preventDefault(); last.focus(); }
                else if (!e.shiftKey && (!inside || document.activeElement === last)) { e.preventDefault(); first.focus(); }
            }
        } else if (e.key === 'Enter' && document.activeElement === instruction) {
            e.preventDefault();
            if (!busy) improve();
        }
        e.stopPropagation();
    }, true);

    // Keep focus inside if something outside grabs it (e.g. a click on the page behind a transparent edge).
    document.addEventListener('focusin', function (e) {
        if (isOpen() && !overlay.contains(e.target)) dialog.focus();
    });

    // A follow-up that stops being due (Mark contacted / Snooze in the drawer) loses its Draft button.
    document.addEventListener('reminder:updated', function (e) {
        if (e.detail && !e.detail.followUpDue) {
            document.querySelectorAll('#app-drawer [data-followup-open][data-app-id="' + e.detail.id + '"]')
                .forEach(function (b) { b.hidden = true; });
        }
    });
})();
