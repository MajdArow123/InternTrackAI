// Profile/Bookmarklet — the install page for the "Save to InternTrackAI" bookmarklet.
// The button's href is the real javascript: URL so dragging it to the bookmarks bar creates a
// working bookmark. Clicking it *here* would just capture this very page, so the click is turned
// into a hint instead; the copy button covers browsers where dragging is awkward (Safari on Mac).
(function () {
    const btn       = document.getElementById('bookmarkletBtn');
    const copyBtn   = document.getElementById('copyBookmarkletBtn');
    const code      = document.getElementById('bookmarkletCode');
    const indicator = document.getElementById('copyBookmarkletIndicator');

    if (btn) {
        btn.addEventListener('click', function (e) {
            e.preventDefault();
            showAppToast('info', 'Drag this button to your bookmarks bar, then click it on a job posting.');
        });
        // Make sure the dropped bookmark gets a sensible name in browsers that read the drag payload.
        btn.addEventListener('dragstart', function (e) {
            if (!e.dataTransfer) return;
            const label = btn.dataset.label || btn.textContent.trim();
            e.dataTransfer.setData('text/uri-list', btn.getAttribute('href'));
            e.dataTransfer.setData('text/plain', btn.getAttribute('href'));
            e.dataTransfer.setData('text/x-moz-url', btn.getAttribute('href') + '\n' + label);
            btn.classList.add('is-dragging');
        });
        btn.addEventListener('dragend', () => btn.classList.remove('is-dragging'));
    }

    if (copyBtn && code) {
        copyBtn.addEventListener('click', async function () {
            const text = code.value;
            let ok = false;
            try {
                if (navigator.clipboard && window.isSecureContext) {
                    await navigator.clipboard.writeText(text);
                    ok = true;
                }
            } catch { /* fall through to the selection fallback */ }
            if (!ok) {
                try {
                    code.focus();
                    code.select();
                    ok = document.execCommand('copy');
                } catch { ok = false; }
            }
            if (ok) {
                showAppToast('success', 'Bookmarklet code copied. Paste it as the address of a new bookmark.');
                if (indicator) {
                    indicator.classList.add('show');
                    clearTimeout(indicator.__hideTimer);
                    indicator.__hideTimer = setTimeout(() => indicator.classList.remove('show'), 2000);
                }
            } else {
                code.focus();
                code.select();
                showAppToast('error', 'Couldn\'t copy automatically — the code is selected, press Ctrl/Cmd+C.');
            }
        });
        code.addEventListener('focus', () => code.select());
    }
})();
