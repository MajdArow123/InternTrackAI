// Applications list behaviour: match-ring tooltips, row selection + bulk actions, the
// side-by-side comparison modal, and the CSV import modal. The detail drawer lives in
// app-drawer.js (shared with the Kanban board). Loaded from Views/JobApplications/Index.cshtml;
// everything is scoped inside this IIFE so nothing leaks onto window. Relies on appConfirm()
// from site.js and the Bootstrap bundle already being loaded.
(function () {
document.querySelectorAll('.match-ring[data-bs-toggle="tooltip"]').forEach(function (el) {
    new bootstrap.Tooltip(el);
});

// Row selection, bulk delete/status-change, and the side-by-side comparison
// modal for the Applications table.
(function () {
    const checkboxes   = Array.from(document.querySelectorAll('.row-check'));
    const selectAll    = document.getElementById('select-all-check');
    const bulkBar      = document.getElementById('bulk-bar');
    const bulkCount    = document.getElementById('bulk-count');
    const deleteBtn    = document.getElementById('bulk-delete-btn');
    const statusBtn    = document.getElementById('bulk-status-btn');
    const deselectBtn  = document.getElementById('bulk-deselect-btn');
    const statusSelect = document.getElementById('bulk-status-select');
    const compareBtn   = document.getElementById('compare-btn');
    const compareOverlay = document.getElementById('compare-overlay');

    function selectedRows() {
        return checkboxes.filter(cb => cb.checked).map(cb => cb.closest('tr'));
    }
    function selectedIds() {
        return checkboxes.filter(cb => cb.checked).map(cb => cb.value);
    }

    function updateBar() {
        const ids = selectedIds();
        const n   = ids.length;
        checkboxes.forEach(cb => cb.closest('tr')?.classList.toggle('is-selected', cb.checked));
        bulkCount.textContent = n + ' selected';
        bulkBar.classList.toggle('show', n > 0);
        if (compareBtn) compareBtn.style.display = (n >= 2 && n <= 3) ? 'inline-flex' : 'none';
        if (selectAll) {
            selectAll.indeterminate = n > 0 && n < checkboxes.length;
            selectAll.checked       = n === checkboxes.length && checkboxes.length > 0;
        }
    }

    checkboxes.forEach(cb => cb.addEventListener('change', updateBar));

    selectAll?.addEventListener('change', function () {
        checkboxes.forEach(cb => { cb.checked = this.checked; });
        updateBar();
    });

    deselectBtn?.addEventListener('click', function () {
        checkboxes.forEach(cb => { cb.checked = false; });
        if (selectAll) selectAll.checked = false;
        updateBar();
    });

    deleteBtn?.addEventListener('click', async function () {
        const ids = selectedIds();
        if (!ids.length) return;
        const ok = await appConfirm({
            title: 'Delete ' + ids.length + ' application' + (ids.length > 1 ? 's' : '') + '?',
            text: 'This permanently removes ' + (ids.length > 1 ? 'these applications' : 'this application') + ' and any notes. This cannot be undone.',
            okLabel: 'Delete'
        });
        if (!ok) return;
        submitBulk('bulk-delete-form', ids, null);
    });

    statusBtn?.addEventListener('click', function () {
        const newStatus = statusSelect.value;
        const ids       = selectedIds();
        if (!newStatus) { statusSelect.focus(); return; }
        if (!ids.length) return;
        submitBulk('bulk-status-form', ids, newStatus);
    });

    function submitBulk(formId, ids, status) {
        const form = document.getElementById(formId);
        form.querySelectorAll('input[name="ids"]').forEach(el => el.remove());
        if (status !== null) {
            const sv = document.getElementById('bulk-status-value');
            if (sv) sv.value = status;
        }
        ids.forEach(id => {
            const inp = document.createElement('input');
            inp.type  = 'hidden';
            inp.name  = 'ids';
            inp.value = id;
            form.appendChild(inp);
        });
        form.submit();
    }

    // ── Comparison ───────────────────────────────────────
    function getAppData(row) {
        return {
            company:    row.dataset.company    || '—',
            role:       row.dataset.role       || '—',
            location:   row.dataset.location   || '—',
            salary:     row.dataset.salary     || '—',
            matchScore: row.dataset.matchScore || null,
            matchRec:   row.dataset.matchRec   || '—',
            status:     row.dataset.status     || '—',
            deadline:   row.dataset.deadline   || '—',
            mode:       row.dataset.mode       || '—'
        };
    }

    function escHtml(s) {
        return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    }

    function renderComparison(apps) {
        const cols = apps.length;
        const fields = [
            { label: 'Company',      key: 'company' },
            { label: 'Role',         key: 'role' },
            { label: 'Location',     key: 'location' },
            { label: 'Salary',       key: 'salary' },
            { label: 'Work Mode',    key: 'mode' },
            { label: 'Status',       key: 'status' },
            { label: 'Match Score',  key: 'matchScore', render: v => v ? v + '%' : '—' },
            { label: 'Rec.',         key: 'matchRec' },
            { label: 'Deadline',     key: 'deadline' }
        ];

        let html = '<div class="compare-grid" data-cols="' + cols + '" style="grid-template-columns:repeat(' + cols + ',1fr)">';
        apps.forEach(function(app) {
            html += '<div class="compare-col">';
            html += '<div class="compare-col-header">';
            html += '<div class="compare-company">' + escHtml(app.company) + '</div>';
            html += '<div class="compare-role">' + escHtml(app.role) + '</div>';
            html += '</div>';
            fields.slice(2).forEach(function(f) {
                var val = app[f.key];
                var display = f.render ? f.render(val) : (val || '—');
                html += '<div class="compare-field">';
                html += '<span class="compare-field-label">' + escHtml(f.label) + '</span>';
                html += '<span class="compare-field-value">' + escHtml(display) + '</span>';
                html += '</div>';
            });
            html += '</div>';
        });
        html += '</div>';

        document.getElementById('compare-content').innerHTML = html;
    }

    function openCompare() {
        var rows = selectedRows();
        if (rows.length < 2 || rows.length > 3) return;
        renderComparison(rows.map(getAppData));
        compareOverlay.classList.add('active');
        compareOverlay.setAttribute('aria-hidden', 'false');
    }
    function closeCompare() {
        compareOverlay.classList.remove('active');
        compareOverlay.setAttribute('aria-hidden', 'true');
    }

    compareBtn?.addEventListener('click', openCompare);
    document.getElementById('compare-close')?.addEventListener('click', closeCompare);
    compareOverlay?.addEventListener('click', function(e) { if (e.target === compareOverlay) closeCompare(); });
    document.addEventListener('keydown', function(e) { if (e.key === 'Escape') closeCompare(); });
})();

(function () {
    const importOverlay = document.getElementById('import-overlay');
    function openImport() {
        importOverlay.classList.add('active');
        importOverlay.setAttribute('aria-hidden', 'false');
    }
    function closeImport() {
        importOverlay.classList.remove('active');
        importOverlay.setAttribute('aria-hidden', 'true');
    }
    document.getElementById('import-csv-btn')?.addEventListener('click', openImport);
    document.getElementById('import-close')?.addEventListener('click', closeImport);
    document.getElementById('import-cancel')?.addEventListener('click', closeImport);
    importOverlay?.addEventListener('click', function (e) { if (e.target === importOverlay) closeImport(); });
    document.addEventListener('keydown', function (e) { if (e.key === 'Escape') closeImport(); });
})();
})();
