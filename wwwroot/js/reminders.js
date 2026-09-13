// "Mark contacted" / "Snooze follow-up" without a page reload. Any button carrying
// data-reminder-action="contacted|snooze" and data-app-id posts to
// /JobApplications/{id}/{action} (antiforgery header + X-Requested-With so the server answers
// JSON) and, on success, shows a toast and dispatches "reminder:updated" with the server's
// payload so the drawer, board card and dashboard list can patch themselves. Used by the
// dashboard Attention card and the detail drawer (list + board pages).
(function () {
    function token() {
        return document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    }

    window.reminderAction = function (action, id) {
        return fetch('/JobApplications/' + encodeURIComponent(id) + '/' + action, {
            method: 'POST',
            headers: { 'RequestVerificationToken': token(), 'X-Requested-With': 'XMLHttpRequest' }
        }).then(async function (r) {
            let data = null;
            try { data = await r.json(); } catch (_) { /* non-JSON error page */ }
            if (!r.ok || !data || !data.success) throw new Error((data && data.error) || 'Could not update the application.');
            return data;
        });
    };

    document.addEventListener('click', function (e) {
        const btn = e.target.closest('[data-reminder-action]');
        if (!btn) return;
        e.preventDefault();
        e.stopPropagation();
        const id = btn.dataset.appId;
        if (!id || btn.disabled) return;
        btn.disabled = true;
        window.reminderAction(btn.dataset.reminderAction, id)
            .then(function (data) {
                if (typeof showAppToast === 'function') showAppToast('success', data.message);
                document.dispatchEvent(new CustomEvent('reminder:updated', { detail: data }));
            })
            .catch(function (err) {
                if (typeof showAppToast === 'function') showAppToast('error', err.message);
            })
            .finally(function () { btn.disabled = false; });
    });

    // Dashboard Attention card: a follow-up row disappears once it is no longer due.
    document.addEventListener('reminder:updated', function (e) {
        const d    = e.detail;
        const list = document.getElementById('attention-list');
        if (!list || d.followUpDue) return;
        const rows = list.querySelectorAll('.attention-item[data-kind="FollowUpDue"][data-app-id="' + d.id + '"]');
        if (!rows.length) return;
        rows.forEach(function (li) { li.remove(); });

        const count = document.getElementById('attention-count');
        if (count) count.textContent = String(Math.max(0, (parseInt(count.textContent, 10) || 0) - rows.length));
        const remaining = list.querySelectorAll('.attention-item').length;
        const empty = document.getElementById('attention-empty');
        if (empty) empty.hidden = remaining > 0;
        if (remaining === 0) document.getElementById('attention-view-all')?.classList.add('d-none');
    });
})();
