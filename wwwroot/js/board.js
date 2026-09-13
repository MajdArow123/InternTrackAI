// Kanban board behaviour: match-ring tooltips, column count / empty-state upkeep, deep links to a
// column (#status-<name>), drag-and-drop via SortableJS (vendored at wwwroot/lib/sortablejs,
// v1.15.6, MIT) with optimistic updates and revert-on-failure, and a keyboard alternative
// (Space to pick up / drop, arrows to move, Escape to cancel) announced through an aria-live
// region. The detail drawer comes from app-drawer.js; cards share the list rows' data-* contract.
// Server calls reuse the app's conventions: POST + JSON body + RequestVerificationToken header,
// errors surfaced through showAppToast() from site.js.
(function () {
    const board = document.getElementById('board');
    if (!board) return;

    const token   = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
    const live    = document.getElementById('board-live');
    const columns = Array.from(board.querySelectorAll('.board-column-body'));
    const TOAST_STATUSES = ['Offer', 'Rejected'];

    document.querySelectorAll('.match-ring[data-bs-toggle="tooltip"]').forEach(function (el) {
        new bootstrap.Tooltip(el);
    });

    // ── Column upkeep ────────────────────────────────────────────────────
    function cardsIn(body) { return Array.from(body.querySelectorAll('.board-card')); }
    function idsIn(body)   { return cardsIn(body).map(c => parseInt(c.dataset.appId, 10)); }
    function bodyFor(status) { return columns.find(c => c.dataset.status === status); }

    function refreshColumn(body) {
        const count = cardsIn(body).length;
        body.classList.toggle('is-empty', count === 0);
        const chip = body.closest('.board-column')?.querySelector('[data-count-for]');
        if (chip) chip.textContent = String(count);
    }
    columns.forEach(refreshColumn);

    function announce(text) {
        if (!live) return;
        live.textContent = '';
        setTimeout(() => { live.textContent = text; }, 30);
    }

    function positionText(card) {
        const body = card.parentElement;
        const cards = cardsIn(body);
        return `${card.dataset.company} moved to ${body.dataset.status}, position ${cards.indexOf(card) + 1} of ${cards.length}`;
    }

    // Dashboard funnel rows link here with #status-<name>: bring that column into view.
    if (location.hash && location.hash.startsWith('#status-')) {
        const col = document.getElementById(location.hash.slice(1));
        if (col) {
            col.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
            col.classList.add('is-highlighted');
            setTimeout(() => col.classList.remove('is-highlighted'), 1600);
        }
    }

    // ── Networking ───────────────────────────────────────────────────────
    function postJson(url, body) {
        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: JSON.stringify(body)
        }).then(async function (r) {
            let data = null;
            try { data = await r.json(); } catch (_) { /* no body */ }
            if (!r.ok) throw new Error((data && data.error) || ('The server returned ' + r.status + '.'));
            return data;
        });
    }

    // Drops are committed strictly in order: each move waits for the previous one to settle.
    let chain = Promise.resolve();
    function enqueue(fn) {
        const run = chain.then(fn, fn);
        chain = run.catch(function () {});
        return run;
    }

    // One reorder request in flight per column; further requests while it runs coalesce into a
    // single follow-up that re-reads the column's current order when the first one completes.
    const inflight = {};
    const pendingAgain = {};
    function reorderColumn(status) {
        const body = bodyFor(status);
        if (!body) return Promise.resolve();
        if (inflight[status]) { pendingAgain[status] = true; return inflight[status]; }

        inflight[status] = postJson('/JobApplications/reorder', { status: status, ids: idsIn(body) })
            .finally(function () {
                inflight[status] = null;
                if (pendingAgain[status]) {
                    pendingAgain[status] = false;
                    reorderColumn(status).catch(function (e) { showAppToast('error', e.message); });
                }
            });
        return inflight[status];
    }

    // ── Snapshot / revert ────────────────────────────────────────────────
    function snapshot(card) {
        return { parent: card.parentElement, next: card.nextElementSibling };
    }
    function restore(card, snap) {
        const current = card.parentElement;
        const anchor = snap.next && snap.next.parentElement === snap.parent ? snap.next : null;
        snap.parent.insertBefore(card, anchor);
        refreshColumn(snap.parent);
        if (current && current !== snap.parent) refreshColumn(current);
    }

    // Optimistic commit: the DOM already reflects the new position; persist it, revert on failure.
    function commitMove(card, fromStatus, toStatus, revert) {
        const id = parseInt(card.dataset.appId, 10);
        const toBody = bodyFor(toStatus);
        const newIndex = cardsIn(toBody).indexOf(card);

        return enqueue(async function () {
            try {
                await postJson('/JobApplications/' + id + '/move', { status: toStatus, boardOrder: newIndex });
                await reorderColumn(toStatus);
                if (fromStatus !== toStatus) await reorderColumn(fromStatus);
                card.dataset.status = String(statusIndex(toStatus));
                if (fromStatus !== toStatus && TOAST_STATUSES.includes(toStatus))
                    showAppToast('success', 'Moved to ' + toStatus);
            } catch (e) {
                revert();
                showAppToast('error', e.message || 'Could not move the application.');
                announce('Move failed: ' + (e.message || 'unknown error'));
            }
        });
    }

    // Mirrors Models/Enums/ApplicationStatus.cs so the drawer badge stays right after a move.
    const STATUS_NUM = { Saved: 0, Applied: 1, Interview: 2, Rejected: 3, Offer: 4 };
    function statusIndex(name) { return STATUS_NUM[name] ?? 0; }

    // ── Drag and drop (SortableJS) ───────────────────────────────────────
    let dragSnapshot = null;
    let justDragged = false;

    if (typeof Sortable !== 'undefined') {
        columns.forEach(function (body) {
            new Sortable(body, {
                group: 'board',
                draggable: '.board-card',
                filter: '.board-empty',
                preventOnFilter: false,
                animation: 150,
                // The fallback renders its own drag clone on every platform, so the lifted card can
                // be styled (tilt + shadow) and Playwright/mouse events drive it consistently.
                forceFallback: true,
                fallbackOnBody: true,
                fallbackTolerance: 4,
                delay: 150,
                delayOnTouchOnly: true,
                touchStartThreshold: 4,
                ghostClass: 'board-card--ghost',
                dragClass: 'board-card--drag',
                chosenClass: 'board-card--chosen',
                // Autoscroll only the board's horizontal strip, never the page: dragging near the
                // viewport edge must not scroll the document vertically (esp. on touch).
                scroll: board,
                scrollSensitivity: 80,
                bubbleScroll: false,
                onStart: function (evt) {
                    dragSnapshot = snapshot(evt.item);
                    document.body.classList.add('board-dragging');
                },
                onEnd: function (evt) {
                    document.body.classList.remove('board-dragging');
                    justDragged = true;
                    setTimeout(function () { justDragged = false; }, 250);

                    const from = evt.from.dataset.status;
                    const to   = evt.to.dataset.status;
                    refreshColumn(evt.from);
                    refreshColumn(evt.to);

                    const unchanged = evt.from === evt.to && evt.oldIndex === evt.newIndex;
                    if (unchanged) return;

                    const snap = dragSnapshot;
                    dragSnapshot = null;
                    announce(positionText(evt.item));
                    commitMove(evt.item, from, to, function () { restore(evt.item, snap); });
                }
            });
        });
    }

    // A drop ends with a mouseup on the card; swallow the click that follows so the drawer
    // doesn't open every time a card is dragged.
    board.addEventListener('click', function (e) {
        if (justDragged && e.target.closest('.board-card')) { e.stopPropagation(); e.preventDefault(); }
    }, true);

    // ── Keyboard moves ───────────────────────────────────────────────────
    // Space picks up / drops, arrows move within and across columns, Escape cancels. Runs in the
    // capture phase so Space never reaches the drawer's Enter/Space "open" handler on the card.
    let lifted = null;      // { card, snap, fromStatus }

    function lift(card) {
        lifted = { card: card, snap: snapshot(card), fromStatus: card.parentElement.dataset.status };
        card.classList.add('board-card--lifted');
        card.setAttribute('aria-grabbed', 'true');
        announce(card.dataset.company + ' picked up. Use the arrow keys to move, Space to drop, Escape to cancel.');
    }

    function release() {
        if (!lifted) return;
        lifted.card.classList.remove('board-card--lifted');
        lifted.card.removeAttribute('aria-grabbed');
        lifted = null;
    }

    function moveLifted(dx, dy) {
        const card = lifted.card;
        const body = card.parentElement;
        if (dy !== 0) {
            const cards = cardsIn(body);
            const i = cards.indexOf(card);
            const j = i + dy;
            if (j < 0 || j >= cards.length) return;
            if (dy < 0) body.insertBefore(card, cards[j]);
            else body.insertBefore(card, cards[j].nextSibling);
        } else if (dx !== 0) {
            const ci = columns.indexOf(body);
            const target = columns[ci + dx];
            if (!target) return;
            const i = cardsIn(body).indexOf(card);
            const targetCards = cardsIn(target);
            const anchor = targetCards[Math.min(i, targetCards.length)] || target.querySelector('.board-empty');
            target.insertBefore(card, anchor);
            refreshColumn(body);
            refreshColumn(target);
            target.closest('.board-column')?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        }
        card.focus();
        announce(positionText(card));
    }

    board.addEventListener('keydown', function (e) {
        const card = e.target.closest('.board-card');
        if (!card) return;

        if (e.key === ' ' || e.key === 'Spacebar') {
            e.preventDefault();
            e.stopPropagation();
            if (!lifted) { lift(card); return; }
            if (lifted.card !== card) return;

            const snap = lifted.snap;
            const fromStatus = lifted.fromStatus;
            const toStatus = card.parentElement.dataset.status;
            const unchanged = card.parentElement === snap.parent && card.nextElementSibling === snap.next;
            release();
            if (unchanged) { announce('Dropped. No change.'); return; }
            announce(positionText(card) + '. Saving.');
            commitMove(card, fromStatus, toStatus, function () { restore(card, snap); card.focus(); });
            return;
        }

        if (!lifted || lifted.card !== card) return;

        switch (e.key) {
            case 'ArrowUp':    e.preventDefault(); e.stopPropagation(); moveLifted(0, -1); break;
            case 'ArrowDown':  e.preventDefault(); e.stopPropagation(); moveLifted(0, 1);  break;
            case 'ArrowLeft':  e.preventDefault(); e.stopPropagation(); moveLifted(-1, 0); break;
            case 'ArrowRight': e.preventDefault(); e.stopPropagation(); moveLifted(1, 0);  break;
            case 'Escape': {
                e.preventDefault(); e.stopPropagation();
                const snap = lifted.snap;
                release();
                restore(card, snap);
                card.focus();
                announce('Move cancelled.');
                break;
            }
            case 'Enter': e.preventDefault(); e.stopPropagation(); break;   // no drawer while lifted
        }
    }, true);
})();
