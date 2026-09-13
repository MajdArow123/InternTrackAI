// Accept / Dismiss for inbox status suggestions. Any button with data-suggestion-action="accept|dismiss"
// and data-suggestion-id posts to /Suggestions/{id}/{action} (antiforgery header, JSON back), shows a
// toast and dispatches "suggestion:resolved" with the server payload so the dashboard card, the
// drawer, the list row / board card and the navbar badge can patch themselves without a reload.
(function () {
    function token() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    }

    window.suggestionAction = function (action, id) {
        return fetch('/Suggestions/' + encodeURIComponent(id) + '/' + action, {
            method: 'POST',
            headers: { 'RequestVerificationToken': token(), 'X-Requested-With': 'XMLHttpRequest' }
        }).then(async function (r) {
            let data = null;
            try { data = await r.json(); } catch (_) { /* non-JSON error page */ }
            if (!r.ok || !data || !data.success) throw new Error((data && data.error) || 'Could not update the suggestion.');
            return data;
        });
    };

    document.addEventListener('click', function (e) {
        const btn = e.target.closest('[data-suggestion-action]');
        if (!btn) return;
        e.preventDefault();
        e.stopPropagation();
        const id = btn.dataset.suggestionId;
        if (!id || btn.disabled) return;
        const group = btn.closest('.attention-actions, .drawer-suggestion-actions');
        const buttons = group ? Array.from(group.querySelectorAll('button')) : [btn];
        buttons.forEach(function (b) { b.disabled = true; });
        window.suggestionAction(btn.dataset.suggestionAction, id)
            .then(function (data) {
                if (typeof showAppToast === 'function') showAppToast(data.accepted ? 'success' : 'info', data.message);
                document.dispatchEvent(new CustomEvent('suggestion:resolved', { detail: data }));
            })
            .catch(function (err) {
                if (typeof showAppToast === 'function') showAppToast('error', err.message);
                buttons.forEach(function (b) { b.disabled = false; });
            });
    });

    // Bootstrap tooltips on the board/list dots (Bootstrap only wires tooltips that are initialised).
    if (window.bootstrap?.Tooltip) {
        document.querySelectorAll('[data-suggestion-dot][data-bs-toggle="tooltip"]').forEach(function (el) { new bootstrap.Tooltip(el); });
    }

    // Navbar badge on the Dashboard link.
    document.addEventListener('suggestion:resolved', function (e) {
        const badge = document.getElementById('nav-suggestions-badge');
        if (!badge) return;
        const n = e.detail.pendingCount || 0;
        badge.textContent = String(n);
        badge.dataset.count = String(n);
        badge.hidden = n === 0;
    });

    // Dashboard card: drop the resolved row, and the card once it is empty.
    document.addEventListener('suggestion:resolved', function (e) {
        const list = document.getElementById('inbox-suggestions-list');
        if (!list) return;
        list.querySelector('.suggestion-item[data-suggestion-id="' + e.detail.id + '"]')?.remove();
        const remaining = list.querySelectorAll('.suggestion-item').length;
        const count = document.getElementById('inbox-suggestions-count');
        if (count) count.textContent = String(remaining);
        if (remaining === 0) document.getElementById('inbox-suggestions')?.remove();
    });
})();
