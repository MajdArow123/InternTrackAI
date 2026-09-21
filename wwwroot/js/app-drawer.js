// Application detail drawer, shared by the Applications list (rows) and the Kanban board
// (cards). Any element with class .app-row and the data-* attributes rendered by
// _Table.cshtml / _BoardCard.cshtml opens it; notes are loaded and added via fetch.
// Requires the _Drawer partial and an antiforgery token input to be on the page.
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
    let lastTrigger = null;   // the control that opened the drawer, so focus can go back to it

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
        } catch (err) {
            rethrowIfBug(err);
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
            resume: appRow.dataset.resume,
            status: statusNum,
            deadline: appRow.dataset.deadline,
            interviewAt: appRow.dataset.interviewAt,
            followUpAt: appRow.dataset.followUpAt,
            lastContactAt: appRow.dataset.lastContactAt,
            hasDates: appRow.dataset.hasDates === '1',
            matchScore: appRow.dataset.matchScore ? parseInt(appRow.dataset.matchScore) : null,
            matchRec: appRow.dataset.matchRec,
            matchSummary: decodeURIComponent(appRow.dataset.matchSummary || ''),
            matchingSkills: parseSkillsJson(decodeURIComponent(appRow.dataset.matchingSkills || '[]')),
            missingSkills: parseSkillsJson(decodeURIComponent(appRow.dataset.missingSkills || '[]')),
            description: decodeURIComponent(appRow.dataset.jobDescription || ''),
            suggestions: parseSuggestions(appRow.dataset.suggestions)
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

        // Inbox suggestions (Accept / Dismiss handled by suggestions.js)
        renderSuggestions(data.suggestions);

        // Additional info
        document.getElementById('drawer-location').textContent = data.location || '—';
        document.getElementById('drawer-mode').textContent = data.mode || '—';
        document.getElementById('drawer-salary').textContent = data.salary || '—';
        document.getElementById('drawer-resume').textContent = data.resume || '—';
        document.getElementById('drawer-deadline').textContent = data.deadline || '—';
        setReminderValues(data);
        currentRow = appRow;

        // Reminder actions (handled by reminders.js through data-reminder-action)
        document.querySelectorAll('#app-drawer [data-reminder-action]').forEach(function (b) { b.dataset.appId = data.id; });
        const followUpBtn = document.getElementById('drawer-followup-btn');
        if (followUpBtn) {
            followUpBtn.dataset.appId   = data.id;
            followUpBtn.dataset.company = data.company || '';
            followUpBtn.dataset.role    = data.role || '';
            followUpBtn.hidden = appRow.dataset.followUpDue !== '1';
        }
        const calBtn = document.getElementById('drawer-calendar-btn');
        if (calBtn) {
            calBtn.href   = '/Calendar/application/' + data.id + '.ics';
            calBtn.hidden = !data.hasDates;
        }

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

        // Keyword coverage — fetched on open rather than rendered into the row's data-* like everything
        // else here, because it reads the resume and only matters once the drawer is actually open.
        const keywordRoot = document.querySelector('#drawer-keywords-section [data-keyword-coverage]');
        if (keywordRoot && window.keywordCoverage) window.keywordCoverage.load(keywordRoot, { appId: data.id });

        // Notes / activity timeline
        loadNoteTimeline(data.id);

        // Show drawer
        drawer.classList.add('active');
        backdrop.classList.add('active');
        drawer.setAttribute('aria-hidden', 'false');
        drawer.setAttribute('aria-modal', 'true');
        backdrop.setAttribute('aria-hidden', 'false');
        document.body.style.overflow = 'hidden';

        // role="dialog" is a promise: focus moves in, stays in, and comes back out to whatever
        // opened it. Without this a keyboard user tabs straight through to the page behind and a
        // screen reader is never told the panel opened.
        // Clicking the row background focuses nothing, so activeElement is <body> on a mouse open.
        // Fall back to that row's own opener button: closing then lands the caret somewhere useful
        // instead of dumping it at the top of the document.
        // Only a real tab stop counts as the thing that opened this. Clicking a row focuses the
        // nearest focusable ancestor, which since the skip link is <main tabindex="-1"> - returning
        // focus there would drop the user at the top of the page instead of back on their row.
        var active = document.activeElement;
        var isTabStop = active instanceof HTMLElement && active !== drawer && active.tabIndex >= 0;
        lastTrigger = isTabStop
            ? active
            : (appRow && appRow.querySelector ? appRow.querySelector(".row-open") : null);
        focusFirst();
    }

    /** Everything inside the drawer a keyboard can land on, in DOM order. */
    function focusables() {
        return Array.prototype.filter.call(
            drawer.querySelectorAll('a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'),
            function (el) { return el.offsetParent !== null || el === document.activeElement; }
        );
    }

    function focusFirst() {
        // The close button is the honest first stop: it is the way out, and it is the first
        // control in the panel either way.
        var first = closeBtn && closeBtn.offsetParent !== null ? closeBtn : focusables()[0];
        if (first) first.focus();
    }

    // ── Inbox suggestions ──────────────────────────────
    function parseSuggestions(raw) {
        if (!raw) return [];
        try { const arr = JSON.parse(decodeURIComponent(raw)); return Array.isArray(arr) ? arr : []; }
        catch (_) { return []; }
    }

    function renderSuggestions(list) {
        const section = document.getElementById('drawer-suggestions-section');
        const host = document.getElementById('drawer-suggestions');
        if (!section || !host) return;
        if (!list || !list.length) { section.hidden = true; host.innerHTML = ''; return; }
        host.innerHTML = list.map(function (s) {
            const badgeClass = statusBadgeClasses[s.status] || '';
            const icon = statusIcons[s.status] || '';
            return '<div class="drawer-suggestion" data-suggestion-id="' + s.id + '">' +
                '<div class="drawer-suggestion-head">' +
                    '<span class="suggestion-arrow" aria-hidden="true">&rarr;</span>' +
                    '<span class="status-badge ' + badgeClass + '">' + icon + ' ' + escHtml2(s.statusName) + '</span>' +
                    '<span class="suggestion-confidence" title="Model confidence">' + escHtml2(String(s.confidence)) + '%</span>' +
                '</div>' +
                '<div class="suggestion-summary">' + escHtml2(s.summary) + '</div>' +
                '<div class="suggestion-email">' + escHtml2(s.subject || '(no subject)') +
                    (s.date ? ' <span class="attention-sep">&middot;</span> ' + escHtml2(s.date) : '') +
                    (s.interviewAt ? ' <span class="attention-sep">&middot;</span> Interview ' + escHtml2(s.interviewAt) : '') +
                '</div>' +
                '<div class="drawer-suggestion-actions">' +
                    '<button type="button" class="btn btn-sm btn-primary" data-suggestion-action="accept" data-suggestion-id="' + s.id + '">Accept</button>' +
                    '<button type="button" class="btn btn-sm btn-outline-secondary" data-suggestion-action="dismiss" data-suggestion-id="' + s.id + '">Dismiss</button>' +
                '</div>' +
            '</div>';
        }).join('');
        section.hidden = false;
    }

    // A suggestion was accepted or dismissed (from the drawer or the dashboard): update the owning
    // row/card's data-*, the dot, and — when accepted — the status shown everywhere on this page.
    document.addEventListener('suggestion:resolved', function (e) {
        const d = e.detail;
        const row = document.querySelector('.app-row[data-app-id="' + d.applicationId + '"]');
        if (!row) return;

        const remaining = parseSuggestions(row.dataset.suggestions).filter(function (s) { return String(s.id) !== String(d.id); });
        row.dataset.suggestions = remaining.length ? encodeURIComponent(JSON.stringify(remaining)) : '';
        if (!remaining.length) row.querySelectorAll('[data-suggestion-dot]').forEach(function (dot) { dot.remove(); });

        if (d.accepted) {
            row.dataset.status = String(d.status);
            if (d.interviewAt) { row.dataset.interviewAt = d.interviewAt; row.dataset.hasDates = '1'; }
            const badge = row.querySelector('td .status-badge');   // list row only; the board moves the card instead (board.js)
            if (badge) {
                badge.className = 'status-badge ' + (statusBadgeClasses[d.status] || '');
                badge.innerHTML = (statusIcons[d.status] || '') + ' <span class="row-status-name">' + escHtml2(d.statusName) + '</span>';
            }
        }

        if (currentRow && String(currentRow.dataset.appId) === String(d.applicationId)) {
            renderSuggestions(remaining);
            if (d.accepted) {
                const statusBadge = document.getElementById('drawer-status-badge');
                statusBadge.className = 'status-badge ' + (statusBadgeClasses[d.status] || '');
                statusBadge.innerHTML = (statusIcons[d.status] || '') + ' ' + escHtml2(d.statusName);
                document.getElementById('drawer-status-timeline').innerHTML = buildStatusTimeline(d.status);
                if (d.interviewAt) document.getElementById('drawer-interview').textContent = d.interviewAt;
                loadNoteTimeline(d.applicationId);
            }
        }
    });

    // ── Reminder values (interview / follow-up / last contact) ──
    let currentRow = null;

    function setReminderValues(d) {
        document.getElementById('drawer-interview').textContent    = d.interviewAt    || '—';
        document.getElementById('drawer-followup').textContent     = d.followUpAt     || '—';
        document.getElementById('drawer-last-contact').textContent = d.lastContactAt  || '—';
    }

    // reminders.js posted Mark contacted / Snooze for the open application: patch the drawer, the
    // row/card's data-* (so reopening shows fresh values) and the board's "Follow up" chip.
    document.addEventListener('reminder:updated', function (e) {
        const d = e.detail;
        if (!currentRow || String(currentRow.dataset.appId) !== String(d.id)) return;
        currentRow.dataset.followUpAt    = d.followUpAt    || '';
        currentRow.dataset.lastContactAt = d.lastContactAt || '';
        currentRow.dataset.followUpDue   = d.followUpDue ? '1' : '';
        currentRow.dataset.hasDates      = (currentRow.dataset.deadline || currentRow.dataset.interviewAt || d.followUpAt) ? '1' : '';
        setReminderValues(currentRow.dataset);
        const calBtn = document.getElementById('drawer-calendar-btn');
        if (calBtn) calBtn.hidden = currentRow.dataset.hasDates !== '1';
        if (!d.followUpDue) currentRow.querySelector('.board-tag--followup')?.remove();
    });

    // ── Notes / activity timeline ──────────────────────
    const noteForm = document.getElementById('drawer-note-form');
    const noteInput = document.getElementById('drawer-note-input');
    const noteTimeline = document.getElementById('drawer-note-timeline');
    // Any antiforgery input on the page works (same token pair per request): the list renders
    // one inside #bulk-delete-form, the board inside #board-token-form.
    const antiForgeryToken = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
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
        drawer.removeAttribute('aria-modal');
        backdrop.setAttribute('aria-hidden', 'true');
        document.body.style.overflow = '';

        // Back to whatever opened it, so the tab position is not lost. isConnected guards the
        // case where the row was re-rendered while the drawer was open.
        if (lastTrigger && lastTrigger.isConnected) lastTrigger.focus();
        lastTrigger = null;
    }

    /** Keeps Tab inside the open drawer, wrapping at either end. */
    function trapTab(e) {
        if (e.key !== 'Tab' || !drawer.classList.contains('active')) return;
        var items = focusables();
        if (!items.length) return;
        var first = items[0];
        var last  = items[items.length - 1];
        if (!drawer.contains(document.activeElement)) { e.preventDefault(); first.focus(); return; }
        if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
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

    // Deep link: /JobApplications#open-42 opens that application's drawer (used by the dashboard's skill gap
    // card). The fragment survives the list→board cookie redirect, so it works in either view.
    const deepLink = /^#open-(\d+)$/.exec(location.hash);
    if (deepLink) {
        const target = document.querySelector('.app-row[data-app-id="' + deepLink[1] + '"]');
        history.replaceState(null, '', location.pathname + location.search);
        if (target) {
            target.scrollIntoView({ block: 'center' });
            openDrawer(target);
        }
    }

    closeBtn.addEventListener('click', closeDrawer);
    closeFooterBtn.addEventListener('click', closeDrawer);
    backdrop.addEventListener('click', closeDrawer);
    document.addEventListener('keydown', trapTab);
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
