// "Rewrite a bullet" on the profile Resume card (Views/Profile/_BulletRewriter.cshtml): POSTs the pasted bullet and the
// chosen application to /Profile/RewriteBullet and shows 2–3 variants, each with a Copy button. Errors (including the
// AI rate limit) show inline with Retry and leave earlier results on screen. Model output is only ever written with
// textContent; bracketed placeholders like [X]% are wrapped in <mark> elements built node by node.
(function () {
    const form = document.getElementById('rewriteForm');
    if (!form) return;

    const bulletIn    = document.getElementById('rewriteBullet');
    const appSelect   = document.getElementById('rewriteApp');
    const count       = document.getElementById('rewriteCount');
    const btn         = document.getElementById('rewriteBtn');
    const btnLabel    = document.getElementById('rewriteBtnLabel');
    const statusEl    = document.getElementById('rewriteStatus');
    const errorBox    = document.getElementById('rewriteError');
    const errorText   = document.getElementById('rewriteErrorText');
    const editLink    = document.getElementById('rewriteEditLink');
    const retryBtn    = document.getElementById('rewriteRetry');
    const results     = document.getElementById('rewriteResults');
    const resultsHead = document.getElementById('rewriteResultsTitle');
    const demoNote    = document.getElementById('rewriteDemoNote');
    const discarded   = document.getElementById('rewriteDiscardedNote');
    const hint        = document.getElementById('rewritePlaceholderHint');
    const list        = document.getElementById('rewriteList');
    const section     = document.getElementById('bulletRewriter');

    const MAX = parseInt(bulletIn.getAttribute('maxlength'), 10) || 400;
    const PLACEHOLDER = /\[[^\]]+\]/g;

    let controller = null;
    let seq = 0;
    let busy = false;

    function token() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    }

    function updateCount() {
        count.textContent = bulletIn.value.length + ' / ' + MAX;
    }

    // ── Request ──────────────────────────────────────
    function rewrite() {
        const bullet = bulletIn.value.trim();
        if (!bullet) {
            showError('Paste a bullet to rewrite.', null, false);
            bulletIn.focus();
            return;
        }

        controller?.abort();
        controller = new AbortController();
        const mine = ++seq;
        hideError();
        setBusy(true);

        fetch('/Profile/RewriteBullet', {
            method: 'POST',
            signal: controller.signal,
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token(),
                'X-Requested-With': 'XMLHttpRequest'
            },
            body: JSON.stringify({ bullet: bullet, applicationId: parseInt(appSelect.value, 10) || 0 })
        })
            .then(async function (r) {
                let data = null;
                try { data = await r.json(); } catch (_) { /* HTML error page */ }
                if (!data) data = { success: false, error: r.status === 404 ? 'That application could not be found. Reload the page and try again.' : 'Something went wrong. Try again.' };
                return data;
            })
            .then(function (data) {
                if (mine !== seq) return;
                if (!data.success) {
                    // Retry can't fix a missing bullet or a missing job description; those point at the fix instead.
                    showError(data.error, data.needsDescription ? data.editUrl : null, data.field !== 'bullet' && !data.needsDescription);
                    if (data.field === 'bullet') bulletIn.focus();
                    return;
                }
                render(data);
            })
            .catch(function (err) {
                if (mine !== seq || (err && err.name === 'AbortError')) return;
                showError('Request failed. Check your connection and try again.', null, true);
            })
            .finally(function () {
                if (mine !== seq) return;
                setBusy(false);
                if (!errorBox.hidden && !retryBtn.hidden) retryBtn.focus();
            });
    }

    // ── Rendering ────────────────────────────────────
    function render(data) {
        const variants = Array.isArray(data.variants) ? data.variants : [];
        list.replaceChildren();
        let anyPlaceholder = false;

        variants.forEach(function (v, i) {
            const text = String(v.text || '');
            if (text.match(PLACEHOLDER)) anyPlaceholder = true;

            const li = document.createElement('li');
            li.className = 'rewrite-card';

            const top = document.createElement('div');
            top.className = 'rewrite-card-top';
            const angle = document.createElement('span');
            angle.className = 'rewrite-angle';
            angle.textContent = String(v.angle || ('Variant ' + (i + 1)));
            const copyBtn = document.createElement('button');
            copyBtn.type = 'button';
            copyBtn.className = 'btn btn-sm btn-outline-secondary rewrite-copy';
            copyBtn.textContent = 'Copy';
            copyBtn.setAttribute('aria-label', 'Copy variant ' + (i + 1));
            copyBtn.addEventListener('click', function () { copy(text); });
            top.append(angle, copyBtn);

            const p = document.createElement('p');
            p.className = 'rewrite-text';
            appendWithPlaceholders(p, text);

            li.append(top, p);
            list.appendChild(li);
        });

        if (data.demo) {
            demoNote.textContent = 'Demo account: these are sample rewrites of “' + (data.sampleBullet || '') + '”, not of your bullet. Sign up to rewrite your own.';
            demoNote.hidden = false;
        } else {
            demoNote.hidden = true;
            demoNote.textContent = '';
        }
        // Sent only when a guard dropped something, so a short list doesn't read as a glitch.
        discarded.textContent = data.note || '';
        discarded.hidden = !data.note;
        hint.hidden = !anyPlaceholder;
        results.hidden = false;
        statusEl.textContent = variants.length + (variants.length === 1 ? ' rewrite ready.' : ' rewrites ready.') + (data.note ? ' ' + data.note : '');
        resultsHead.focus({ preventScroll: true });
        results.scrollIntoView({ block: 'nearest' });
    }

    function appendWithPlaceholders(el, text) {
        let last = 0;
        text.replace(PLACEHOLDER, function (match, offset) {
            if (offset > last) el.appendChild(document.createTextNode(text.slice(last, offset)));
            const mark = document.createElement('mark');
            mark.className = 'rewrite-placeholder';
            mark.textContent = match;
            el.appendChild(mark);
            last = offset + match.length;
            return match;
        });
        if (last < text.length) el.appendChild(document.createTextNode(text.slice(last)));
    }

    function showError(message, editUrl, retryable) {
        errorText.textContent = message || 'Could not rewrite the bullet. Try again.';
        if (editUrl) {
            editLink.href = editUrl;
            editLink.hidden = false;
        } else {
            editLink.hidden = true;
            editLink.removeAttribute('href');
        }
        retryBtn.hidden = !retryable;
        errorBox.hidden = false;
        statusEl.textContent = '';
        errorBox.scrollIntoView({ block: 'nearest' });
    }

    function hideError() {
        errorBox.hidden = true;
        errorText.textContent = '';
        editLink.hidden = true;
    }

    function setBusy(on) {
        busy = on;
        btn.disabled = on;
        retryBtn.disabled = on;
        section.setAttribute('aria-busy', on ? 'true' : 'false');
        results.classList.toggle('is-updating', on && !results.hidden);
        if (on) {
            statusEl.textContent = 'Rewriting…';
            btnLabel.textContent = 'Rewriting…';
            if (!btn.querySelector('.analyze-spinner')) {
                const spin = document.createElement('span');
                spin.className = 'spinner-border spinner-border-sm analyze-spinner';
                spin.setAttribute('aria-hidden', 'true');
                btn.insertBefore(spin, btn.firstChild);
            }
            btn.querySelector('svg')?.setAttribute('hidden', '');
        } else {
            btnLabel.textContent = 'Rewrite';
            btn.querySelector('.analyze-spinner')?.remove();
            btn.querySelector('svg')?.removeAttribute('hidden');
        }
    }

    // ── Copy ─────────────────────────────────────────
    async function copy(text) {
        try {
            await navigator.clipboard.writeText(text);
        } catch (err) {
            rethrowIfBug(err);
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.setAttribute('readonly', '');
            ta.className = 'visually-hidden';
            document.body.appendChild(ta);
            ta.select();
            document.execCommand('copy');
            ta.remove();
        }
        if (typeof showAppToast === 'function') showAppToast('success', 'Bullet copied to clipboard.');
    }

    // ── Events ───────────────────────────────────────
    form.addEventListener('submit', function (e) {
        e.preventDefault();
        if (!busy) rewrite();
    });
    retryBtn.addEventListener('click', function () { if (!busy) rewrite(); });
    bulletIn.addEventListener('input', updateCount);
    bulletIn.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            if (!busy) rewrite();
        }
    });
    updateCount();
})();
