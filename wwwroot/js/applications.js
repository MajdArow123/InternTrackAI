// Applications page behaviour: match-ring tooltips, row selection + bulk actions, the
// side-by-side comparison modal, the CSV import modal, and the detail drawer with its
// notes timeline. Loaded from Views/JobApplications/Index.cshtml; everything is scoped
// inside this IIFE so nothing leaks onto window. Relies on appConfirm() from site.js
// and the Bootstrap bundle already being loaded.
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

// Slide-out panel showing the full job description and real AI match data
// (score, recommendation, matching/missing skills) for a clicked row, without
// a page navigation. All data is read from data-* attributes already rendered
// on each <tr> server-side (see the table markup above) — no extra fetch.
(function () {
    const drawer = document.getElementById('app-drawer');
    const backdrop = document.getElementById('app-drawer-backdrop');
    const closeBtn = document.getElementById('drawer-close');
    const closeFooterBtn = document.getElementById('drawer-close-btn');
    const appRows = document.querySelectorAll('.app-row');

    // Mirrors Models/Enums/ApplicationStatus.cs — numeric values are read out of
    // each row's data-status attribute and looked up against these maps to
    // render the status badge without a round trip to the server.
    const STATUS = {
        SAVED: 0,
        APPLIED: 1,
        INTERVIEW: 2,
        REJECTED: 3,
        OFFER: 4
    };

    const statusNames = {
        0: 'Saved',
        1: 'Applied',
        2: 'Interview',
        3: 'Rejected',
        4: 'Offer'
    };

    const statusBadgeClasses = {
        0: 'badge-saved',
        1: 'badge-applied',
        2: 'badge-interview',
        3: 'badge-rejected',
        4: 'badge-offer'
    };

    const statusIcons = {
        0: '<svg class="status-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><path d="M19 21l-7-5-7 5V5a2 2 0 0 1 2-2h10a2 2 0 0 1 2 2z"/></svg>',
        1: '<svg class="status-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><line x1="22" y1="2" x2="11" y2="13"/><polygon points="22 2 15 22 11 13 2 9 22 2"/></svg>',
        2: '<svg class="status-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="18" rx="2"/><line x1="16" y1="2" x2="16" y2="6"/><line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/></svg>',
        3: '<svg class="status-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="15" y1="9" x2="9" y2="15"/><line x1="9" y1="9" x2="15" y2="15"/></svg>',
        4: '<svg class="status-icon" viewBox="0 0 24 24" fill="currentColor" stroke="none"><polygon points="12 2 15.09 8.26 22 9.27 17 14.14 18.18 21.02 12 17.77 5.82 21.02 7 14.14 2 9.27 8.91 8.26 12 2"/></svg>'
    };

    // Same 5-tier resume match scoring table as Create.cshtml's inline match
    // card. Intentionally duplicated rather than shared — this is a Razor view
    // with no JS module bundler, so each <script> block is self-contained.
    // Keep both copies in sync if the tier thresholds or labels ever change.
    const TIERS = [
        { min: 80, num: 5, label: 'APPLY',             desc: 'Strong fit — most required skills are present.' },
        { min: 60, num: 4, label: 'APPLY',             desc: 'Good fit — you have the core fundamentals for this role.' },
        { min: 40, num: 3, label: 'MAYBE',             desc: 'Moderate fit — some upskilling would strengthen your application.' },
        { min: 20, num: 2, label: 'CONSIDER SKIPPING', desc: 'Weak fit — several key requirements are missing from your profile.' },
        { min: 0,  num: 1, label: 'SKIP',              desc: 'Poor fit — the role requirements don\'t match your current profile.' },
    ];

    // Picks the first (highest) tier whose `min` the score meets or exceeds.
    function getTier(sc) {
        return TIERS.find(t => sc >= t.min) || TIERS[TIERS.length - 1];
    }

    // Minimal HTML-escaping for values interpolated into innerHTML below.
    function escHtml2(s) {
        return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
    }

    // Derives a 1-2 letter avatar initial from the company name, e.g.
    // "Shopify Inc" -> "SI", "Shopify" -> "SH". Falls back to "?" if blank.
    function initials(name) {
        const parts = (name || '').trim().split(/\s+/).filter(Boolean);
        if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase();
        if (name && name.length >= 2) return name.slice(0, 2).toUpperCase();
        return (name || '?').toUpperCase();
    }

    // Builds the drawer's "AI Analysis" card — score, tier badge/description,
    // AI-generated summary, and matching/missing skill tag columns. This
    // renders the same real AI output (not fabricated text) that the Create
    // page's resume match card shows, just laid out for the drawer.
    function buildDrawerMatchCard(score, summary, matchingSkills, missingSkills) {
        const tier = getTier(score);
        const matchTags = matchingSkills.map(s => '<span class="skill-tag skill-tag--match">' + escHtml2(s) + '</span>').join('');
        const missTags  = missingSkills.map(s  => '<span class="skill-tag skill-tag--missing">' + escHtml2(s) + '</span>').join('');

        const skillsHtml = (matchTags || missTags) ? (
            '<div class="match-skills-grid">' +
                (matchTags ? '<div class="match-skills-col">' +
                    '<div class="match-skills-header match-skills-header--match">Matching (' + matchingSkills.length + ')</div>' +
                    '<div class="match-skills-tags">' + matchTags + '</div>' +
                '</div>' : '') +
                (missTags ? '<div class="match-skills-col">' +
                    '<div class="match-skills-header match-skills-header--missing">Missing (' + missingSkills.length + ')</div>' +
                    '<div class="match-skills-tags">' + missTags + '</div>' +
                '</div>' : '') +
            '</div>'
        ) : '';

        return (
            '<div class="match-verdict-row">' +
                '<span class="match-ring match-ring--tier-' + tier.num + '" style="--p:' + Number(score) + '"><span>' + score + '</span></span>' +
                '<span class="match-rec-badge match-tier-' + tier.num + '">' + tier.label + '</span>' +
                '<span class="match-verdict-desc">' + tier.desc + '</span>' +
            '</div>' +
            (summary ? '<p class="match-summary-text">' + escHtml2(summary) + '</p>' : '') +
            skillsHtml
        );
    }

    // Renders the 4-stage pipeline (Saved -> Applied -> Interview -> Offer) as a
    // horizontal stepper. Rejected has no dedicated stage on the happy-path pipeline,
    // so it's shown by replacing the final stage with a red "Rejected" marker —
    // the prior stages are marked done since reaching Rejected implies the
    // application was at least Applied.
    function buildStatusTimeline(statusNum) {
        const isRejected = statusNum === STATUS.REJECTED;
        const stages = [
            { key: STATUS.SAVED,     label: 'Saved' },
            { key: STATUS.APPLIED,   label: 'Applied' },
            { key: STATUS.INTERVIEW, label: 'Interview' },
            { key: STATUS.OFFER,     label: isRejected ? 'Rejected' : 'Offer' }
        ];
        const currentIndex = isRejected ? 3 : stages.findIndex(s => s.key === statusNum);

        return stages.map(function (s, i) {
            let cls = '';
            if (isRejected && i === 3) cls = 'is-rejected';
            else if (i < currentIndex) cls = 'is-done';
            else if (i === currentIndex) cls = 'is-current';

            const dotContent = (cls === 'is-done') ? '&#10003;' : String(i + 1);
            return (
                '<div class="status-timeline-step ' + cls + '">' +
                    '<div class="status-timeline-line"></div>' +
                    '<div class="status-timeline-dot">' + dotContent + '</div>' +
                    '<div class="status-timeline-label">' + s.label + '</div>' +
                '</div>'
            );
        }).join('');
    }

    function parseSkillsJson(raw) {
        try {
            const arr = JSON.parse(raw || '[]');
            return Array.isArray(arr) ? arr : [];
        } catch (_) {
            return [];
        }
    }

    function openDrawer(appRow) {
        const statusNum = parseInt(appRow.dataset.status) || 0;

        const data = {
            id: appRow.dataset.appId,
            company: appRow.dataset.company,
            role: appRow.dataset.role,
            location: appRow.dataset.location,
            salary: appRow.dataset.salary,
            mode: appRow.dataset.mode,
            status: statusNum,
            deadline: appRow.dataset.deadline,
            matchScore: appRow.dataset.matchScore ? parseInt(appRow.dataset.matchScore) : null,
            matchRec: appRow.dataset.matchRec,
            matchSummary: decodeURIComponent(appRow.dataset.matchSummary || ''),
            matchingSkills: parseSkillsJson(decodeURIComponent(appRow.dataset.matchingSkills || '[]')),
            missingSkills: parseSkillsJson(decodeURIComponent(appRow.dataset.missingSkills || '[]')),
            description: decodeURIComponent(appRow.dataset.jobDescription || '')
        };

        // Header
        document.getElementById('drawer-role').textContent = data.role;
        document.getElementById('drawer-company').textContent = data.company;
        document.getElementById('drawer-avatar').textContent = initials(data.company);

        // Status badge
        const statusBadge = document.getElementById('drawer-status-badge');
        const statusName = statusNames[statusNum] || 'Unknown';
        const statusIcon = statusIcons[statusNum] || '';
        const statusClass = statusBadgeClasses[statusNum] || '';

        statusBadge.className = 'status-badge ' + statusClass;
        statusBadge.innerHTML = statusIcon + ' ' + statusName;

        // Status progress timeline
        document.getElementById('drawer-status-timeline').innerHTML = buildStatusTimeline(statusNum);

        // Description
        document.getElementById('drawer-description').textContent = data.description || 'No description available';

        // AI Analysis
        const analysisSection = document.getElementById('drawer-analysis-section');
        if (data.matchScore != null) {
            document.getElementById('drawer-match-card').innerHTML =
                buildDrawerMatchCard(data.matchScore, data.matchSummary, data.matchingSkills, data.missingSkills);
            analysisSection.hidden = false;
        } else {
            analysisSection.hidden = true;
        }

        // Additional info
        document.getElementById('drawer-location').textContent = data.location || '—';
        document.getElementById('drawer-mode').textContent = data.mode || '—';
        document.getElementById('drawer-salary').textContent = data.salary || '—';
        document.getElementById('drawer-deadline').textContent = data.deadline || '—';

        // Action buttons
        const editBtn = document.getElementById('drawer-edit-btn');
        const clBtn = document.getElementById('drawer-cover-letter-btn');
        const intBtn = document.getElementById('drawer-interview-btn');

        editBtn.dataset.appId = data.id;
        editBtn.onclick = function() { window.location.href = '/JobApplications/Edit/' + data.id; };

        clBtn.dataset.appId = data.id;
        clBtn.onclick = function() { window.location.href = '/CoverLetter/Generate?appId=' + data.id; };

        intBtn.dataset.appId = data.id;
        intBtn.onclick = function() { window.location.href = '/InterviewPrep/Prep?appId=' + data.id; };

        // Notes / activity timeline
        loadNoteTimeline(data.id);

        // Show drawer
        drawer.classList.add('active');
        backdrop.classList.add('active');
        drawer.setAttribute('aria-hidden', 'false');
        backdrop.setAttribute('aria-hidden', 'false');
        document.body.style.overflow = 'hidden';
    }

    // ── Notes / activity timeline ──────────────────────
    const noteForm = document.getElementById('drawer-note-form');
    const noteInput = document.getElementById('drawer-note-input');
    const noteTimeline = document.getElementById('drawer-note-timeline');
    const antiForgeryToken = document.querySelector('#bulk-delete-form input[name="__RequestVerificationToken"]')?.value || '';
    let currentNoteAppId = null;

    function escHtml(s) {
        return (s || '').replace(/[&<>"']/g, function (c) {
            return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c];
        });
    }

    function renderNoteTimeline(notes) {
        if (!notes.length) {
            noteTimeline.innerHTML = '<p class="drawer-note-empty">No notes yet.</p>';
            return;
        }
        noteTimeline.innerHTML = notes.map(function (n) {
            return '<div class="drawer-note-item">' +
                '<div class="drawer-note-text">' + escHtml(n.text).replace(/\n/g, '<br>') + '</div>' +
                '<div class="drawer-note-time">' + escHtml(n.createdAt) + '</div>' +
                '</div>';
        }).join('');
    }

    function loadNoteTimeline(appId) {
        currentNoteAppId = appId;
        noteTimeline.innerHTML = '<p class="drawer-note-empty">Loading...</p>';
        fetch('/JobApplications/Notes?appId=' + encodeURIComponent(appId))
            .then(function (r) { return r.ok ? r.json() : []; })
            .then(renderNoteTimeline)
            .catch(function () { renderNoteTimeline([]); });
    }

    noteForm?.addEventListener('submit', function (e) {
        e.preventDefault();
        const text = noteInput.value.trim();
        if (!text || !currentNoteAppId) return;

        fetch('/JobApplications/AddNote', {
            method: 'POST',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: new URLSearchParams({
                appId: currentNoteAppId,
                text: text,
                __RequestVerificationToken: antiForgeryToken
            })
        })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (note) {
            if (!note) return;
            noteInput.value = '';
            loadNoteTimeline(currentNoteAppId);
        });
    });

    function closeDrawer() {
        drawer.classList.remove('active');
        backdrop.classList.remove('active');
        drawer.setAttribute('aria-hidden', 'true');
        backdrop.setAttribute('aria-hidden', 'true');
        document.body.style.overflow = '';
    }

    // Event listeners
    appRows.forEach(row => {
        row.addEventListener('click', function(e) {
            // Don't open drawer if clicking on checkboxes or buttons
            if (e.target.closest('.row-check, .row-action-btn')) return;
            openDrawer(this);
        });
        row.addEventListener('keydown', function(e) {
            if (e.key === 'Enter' || e.key === ' ') {
                e.preventDefault();
                openDrawer(this);
            }
        });
    });

    closeBtn.addEventListener('click', closeDrawer);
    closeFooterBtn.addEventListener('click', closeDrawer);
    backdrop.addEventListener('click', closeDrawer);
    document.addEventListener('keydown', function(e) {
        if (e.key === 'Escape' && drawer.classList.contains('active')) {
            closeDrawer();
        }
    });

    // ── Swipe-down-to-close (mobile full-screen drawer) ──
    // Only the drag handle initiates a swipe, so normal scrolling inside the
    // body content isn't hijacked. Below the close threshold the drawer
    // snaps back via the CSS transition (removed only while dragging).
    const drawerHandle = document.getElementById('drawer-handle');
    const CLOSE_THRESHOLD = 100;
    let dragStartY = null;

    function onTouchStart(e) {
        dragStartY = e.touches[0].clientY;
        drawer.classList.add('is-dragging');
    }
    function onTouchMove(e) {
        if (dragStartY == null) return;
        const delta = Math.max(0, e.touches[0].clientY - dragStartY);
        drawer.style.transform = 'translateY(' + delta + 'px)';
    }
    function onTouchEnd(e) {
        if (dragStartY == null) return;
        const delta = Math.max(0, (e.changedTouches[0].clientY - dragStartY));
        drawer.classList.remove('is-dragging');
        drawer.style.transform = '';
        dragStartY = null;
        if (delta > CLOSE_THRESHOLD) closeDrawer();
    }

    drawerHandle?.addEventListener('touchstart', onTouchStart, { passive: true });
    drawerHandle?.addEventListener('touchmove', onTouchMove, { passive: true });
    drawerHandle?.addEventListener('touchend', onTouchEnd);
})();
})();
