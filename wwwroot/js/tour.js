// ── Guided tour engine ────────────────────────────────────────────────────
// Drives the step lists in tour-steps.js. No framework, no dependencies, and
// no knowledge of any individual step: everything it needs comes from the
// definition object.
//
//   window.Tour.start(id)     run a tour; returns false if it can't
//   window.Tour.available(id) does that tour exist and have steps
//
// Notes for the next person in here:
//  * The overview tour spans real page loads. Before navigating we stash
//    {id, index, nav, tried, ctx} in sessionStorage and pick it back up on
//    the next load.
//  * A step can list fallback targets ("targets"), best first, each with its
//    own copy and optionally its own page. The first one that exists wins, so
//    an account with nothing generated yet gets the example card or the
//    button that makes one, instead of silently losing the step.
//  * Nothing auto-runs. The dashboard carries an invitation
//    ([data-tour-prompt]); taking or dismissing it is remembered per browser.
//  * Every storage call can throw (private mode, blocked site data). All of
//    them go through readStore/writeStore, which swallow it: an unreadable
//    "seen" flag counts as not-yet-seen, never as a crash.
//  * A step whose target matches nothing visible is skipped in the direction
//    of travel rather than dimming the page around nothing.
(function () {
    'use strict';

    var SEEN_KEY  = 'itai.tour.v1';        // localStorage: invitation taken or dismissed, once per browser
    var STATE_KEY = 'itai.tour.state';     // sessionStorage: resume point across a navigation
    var DOCK_MQ   = '(max-width: 639.98px)';
    var GAP       = 12;                    // tooltip ↔ spotlight
    var EDGE      = 12;                    // tooltip ↔ viewport edge

    var state = null;   // { id, steps, index, cand, nav, tried, ctx, prevFocus } while running
    var els   = null;   // { overlay, spot, tip, title, body, count, back, next }
    var frame = 0;      // rAF handle for the throttled reposition

    /* ── storage ─────────────────────────────────────────────────────── */
    function readStore(area, key) {
        try { var s = window[area]; return s ? s.getItem(key) : null; } catch (e) { return null; }
    }
    function writeStore(area, key, value) {
        try { var s = window[area]; if (s) s.setItem(key, value); } catch (e) { /* nothing to do */ }
    }
    function clearStore(area, key) {
        try { var s = window[area]; if (s) s.removeItem(key); } catch (e) { /* nothing to do */ }
    }

    /* ── definitions ─────────────────────────────────────────────────── */
    function tours() { return window.TourSteps || {}; }

    function tourOf(id) {
        var t = tours()[id];
        return t && t.steps && t.steps.length ? t : null;
    }

    function available(id) { return !!tourOf(id); }

    function normPath(p) {
        p = (p || '').toLowerCase();
        return p.length > 1 ? p.replace(/\/+$/, '') : p;
    }
    function samePath(a, b) { return normPath(a) === normPath(b); }

    // The tour the nav button runs here: a page tour if one claims this path,
    // otherwise the overview.
    function forPage() {
        var here = normPath(window.location.pathname);
        var all  = tours();
        for (var id in all) {
            if (!Object.prototype.hasOwnProperty.call(all, id)) continue;
            var match = all[id].match || [];
            for (var i = 0; i < match.length; i++) {
                if (normPath(match[i]) === here && available(id)) return id;
            }
        }
        return 'overview';
    }

    function reducedMotion() {
        try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; } catch (e) { return false; }
    }
    function docked() {
        try { return window.matchMedia(DOCK_MQ).matches; } catch (e) { return false; }
    }

    /* ── overlay ─────────────────────────────────────────────────────── */
    function build() {
        var overlay = document.createElement('div');
        overlay.className = 'tour-overlay';

        var spot = document.createElement('div');
        spot.className = 'tour-spotlight';

        var tip = document.createElement('div');
        tip.className = 'tour-tip';
        tip.setAttribute('role', 'dialog');
        tip.setAttribute('aria-modal', 'true');
        tip.setAttribute('aria-labelledby', 'tour-tip-title');
        tip.setAttribute('aria-describedby', 'tour-tip-body');
        tip.tabIndex = -1;
        tip.innerHTML =
            '<button type="button" class="tour-close" data-tour-act="exit" aria-label="End tour">' +
                '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" aria-hidden="true">' +
                '<line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>' +
            '</button>' +
            '<h2 class="tour-tip-title" id="tour-tip-title"></h2>' +
            '<p class="tour-tip-body" id="tour-tip-body"></p>' +
            '<div class="tour-tip-foot">' +
                '<span class="tour-count" id="tour-tip-count" aria-live="polite"></span>' +
                '<span class="tour-tip-actions">' +
                    '<button type="button" class="btn btn-sm btn-outline-secondary tour-back" data-tour-act="back">Back</button>' +
                    '<button type="button" class="btn btn-sm btn-primary tour-next" data-tour-act="next">Next</button>' +
                '</span>' +
            '</div>';

        overlay.appendChild(spot);
        overlay.appendChild(tip);
        document.body.appendChild(overlay);

        els = {
            overlay: overlay,
            spot: spot,
            tip: tip,
            title: tip.querySelector('#tour-tip-title'),
            body: tip.querySelector('#tour-tip-body'),
            count: tip.querySelector('#tour-tip-count'),
            back: tip.querySelector('.tour-back'),
            next: tip.querySelector('.tour-next')
        };

        tip.addEventListener('click', onClick);
        document.addEventListener('keydown', onKeydown, true);
        window.addEventListener('resize', schedule);
        window.addEventListener('scroll', schedule, true);
    }

    function teardown() {
        if (frame) { cancelAnimationFrame(frame); frame = 0; }
        document.removeEventListener('keydown', onKeydown, true);
        window.removeEventListener('resize', schedule);
        window.removeEventListener('scroll', schedule, true);
        if (els && els.overlay && els.overlay.parentNode) els.overlay.parentNode.removeChild(els.overlay);
        els = null;
    }

    /* ── running ─────────────────────────────────────────────────────── */
    function persist(index, path) {
        var tried = state.nav === index ? state.tried.slice() : [];
        if (tried.indexOf(normPath(path)) < 0) tried.push(normPath(path));
        writeStore('sessionStorage', STATE_KEY, JSON.stringify({
            id: state.id, index: index, nav: index, tried: tried, ctx: state.ctx
        }));
    }

    /* ── targets ─────────────────────────────────────────── */
    // A target counts only if it is actually on screen to point at. Existing is
    // not enough: at phone width the nav links sit inside a collapsed menu with
    // display:none, and spotlighting them drew a 16x16 speck in the corner
    // around nothing. The first *visible* match wins, so a selector that
    // matches several rows (an attention list) skips any that are hidden.
    function visible(el) {
        if (!el || !el.getClientRects().length) return false;
        var r = el.getBoundingClientRect();
        if (r.width < 1 || r.height < 1) return false;
        return getComputedStyle(el).visibility !== 'hidden';
    }
    function findVisible(selector) {
        if (!selector) return null;
        var all = document.querySelectorAll(selector);
        for (var i = 0; i < all.length; i++) if (visible(all[i])) return all[i];
        return null;
    }

    /* ── candidates ──────────────────────────────────────── */
    // What the current page knows that the tour needs before it leaves it —
    // today only whether a resume draft is waiting (the dashboard's
    // #tourContextData). Merged, so a page without the island changes nothing.
    function readContext() {
        var el = document.getElementById('tourContextData');
        if (!el) return;
        var data = null;
        try { data = JSON.parse(el.textContent || '{}'); } catch (e) { rethrowIfBug(e); data = null; }
        if (!data) return;
        for (var k in data) if (Object.prototype.hasOwnProperty.call(data, k)) state.ctx[k] = data[k];
    }

    // A step is either one target, or an ordered list of fallbacks. Either way
    // the engine sees a list of { view, target, title, body, when, pad, placement }.
    function candidatesOf(step) {
        var list = step.targets && step.targets.length ? step.targets : [step];
        var out = [];
        for (var i = 0; i < list.length; i++) {
            var c = list[i];
            if (c.when && !state.ctx[c.when]) continue;
            out.push({
                view:      c.view !== undefined ? c.view : step.view,
                target:    c.target !== undefined ? c.target : null,
                title:     c.title || step.title || '',
                body:      c.body || step.body || '',
                pad:       typeof c.pad === 'number' ? c.pad : step.pad,
                placement: c.placement || step.placement
            });
        }
        return out;
    }

    function exit() {
        var focus = state ? state.prevFocus : null;
        clearStore('sessionStorage', STATE_KEY);
        state = null;
        teardown();
        if (focus && document.contains(focus) && typeof focus.focus === 'function') {
            try { focus.focus(); } catch (e) { /* element went away mid-tour */ }
        }
    }

    // Walks from `index` in the direction of travel. For each step: take the
    // first candidate that lives on this page and whose target exists; failing
    // that, navigate to the first candidate page not yet tried for this step;
    // failing that, skip the step.
    function go(index, dir) {
        if (!state) return;
        dir = dir < 0 ? -1 : 1;
        if (index < 0 && dir < 0) return;   // Back on the first step: stay put

        var steps = state.steps;
        var here = window.location.pathname;
        while (index >= 0 && index < steps.length) {
            var cands = candidatesOf(steps[index] || {});
            var tried = state.nav === index ? state.tried : [];

            var hit = null, i, c;
            for (i = 0; i < cands.length && !hit; i++) {
                c = cands[i];
                if (c.view && !samePath(here, c.view)) continue;
                if (!c.target || findVisible(c.target)) hit = c;
            }
            if (hit) {
                state.index = index;
                state.cand = hit;
                render();
                return;
            }

            for (i = 0; i < cands.length; i++) {
                c = cands[i];
                if (!c.view || samePath(here, c.view) || tried.indexOf(normPath(c.view)) >= 0) continue;
                persist(index, c.view);
                window.location.assign(c.view);
                return;
            }
            index += dir;
        }
        if (dir < 0) { render(); return; }  // nothing earlier is reachable
        exit();
    }

    function render() {
        var step = state.cand || {};
        els.title.textContent = step.title || '';
        els.body.textContent  = step.body || '';
        els.count.textContent = (state.index + 1) + ' of ' + state.steps.length;
        els.back.disabled     = state.index === 0;
        els.next.textContent  = state.index === state.steps.length - 1 ? 'Done' : 'Next';

        // Position first, then scroll. Where the card ends up (docked at the bottom of a phone, pinned
        // beside a tall target) decides how much of the screen the target has to fit into, so the
        // scroll has to know it. Scrolling first is how the stats target ended up under the docked
        // sheet. position() runs again on every scroll frame, so the spotlight follows the scroll.
        position();
        reveal(findVisible(step.target));

        // Focus the dialog itself, not a control: screen readers then announce
        // the title and body before the buttons.
        try { els.tip.focus({ preventScroll: true }); } catch (e) { els.tip.focus(); }
    }

    function navHeight() {
        return parseInt(getComputedStyle(document.documentElement).getPropertyValue('--site-nav-h'), 10) || 53;
    }

    // Scrolls the target into the part of the screen the card leaves free: below the sticky nav and,
    // on a phone, above the docked sheet. Centred there when it fits; top-aligned under the nav when
    // it is taller than that space, so its beginning is what shows. Nothing moves if it is already
    // wholly inside.
    function reveal(el) {
        if (!el || !els) return;
        var top = navHeight() + 8;
        var bottom = window.innerHeight - 8;
        if (els.tip.classList.contains('tour-tip--docked')) bottom = els.tip.getBoundingClientRect().top - 8;

        var r = el.getBoundingClientRect();
        if (r.top >= top && r.bottom <= bottom) return;

        var room = bottom - top;
        var offset = r.height <= room ? (room - r.height) / 2 : 0;
        var y = Math.max(0, window.scrollY + r.top - top - offset);
        try { window.scrollTo({ top: y, behavior: reducedMotion() ? 'auto' : 'smooth' }); }
        catch (e) { window.scrollTo(0, y); }
    }

    function position() {
        if (!state || !els) return;
        var step = state.cand || {};
        var el   = findVisible(step.target);
        var dock = docked();

        els.tip.classList.toggle('tour-tip--docked', dock);

        if (!el) {
            els.spot.hidden = true;
            els.overlay.classList.add('tour-overlay--plain');
            els.tip.classList.add('tour-tip--center');
            els.tip.classList.remove('tour-tip--pinned');
            els.tip.style.top = els.tip.style.left = '';
            return;
        }

        els.overlay.classList.remove('tour-overlay--plain');
        els.tip.classList.remove('tour-tip--center');
        els.spot.hidden = false;

        var pad = typeof step.pad === 'number' ? step.pad : 8;
        var r   = el.getBoundingClientRect();
        var top = r.top - pad, left = r.left - pad;
        var w   = r.width + pad * 2, h = r.height + pad * 2;

        els.spot.style.top    = top + 'px';
        els.spot.style.left   = left + 'px';
        els.spot.style.width  = w + 'px';
        els.spot.style.height = h + 'px';

        if (dock) { els.tip.classList.remove('tour-tip--pinned'); els.tip.style.top = els.tip.style.left = ''; return; }

        var tip = els.tip.getBoundingClientRect();
        var vh  = window.innerHeight, vw = window.innerWidth;

        // A target too tall to ever have the card above or below it, however the page is scrolled:
        // pin the card to the corner instead of centring it on top of what it describes. Decided on
        // sizes alone, never on the current scroll, so it cannot flip back and forth mid-scroll.
        var pinned = h + GAP + tip.height > vh - navHeight() - EDGE * 2;
        els.tip.classList.toggle('tour-tip--pinned', pinned);
        if (pinned) { els.tip.style.top = els.tip.style.left = ''; return; }
        var below = top + h + GAP;
        var above = top - GAP - tip.height;
        var want  = step.placement === 'top' || step.placement === 'bottom' ? step.placement : 'auto';

        var y;
        if (want === 'bottom' && below + tip.height <= vh - EDGE)      y = below;
        else if (want === 'top' && above >= EDGE)                      y = above;
        else if (below + tip.height <= vh - EDGE)                      y = below;
        else if (above >= EDGE)                                        y = above;
        else y = Math.max(EDGE, Math.min(vh - tip.height - EDGE, top + h / 2 - tip.height / 2));

        var x = left + w / 2 - tip.width / 2;
        x = Math.max(EDGE, Math.min(x, vw - tip.width - EDGE));

        els.tip.style.top  = Math.round(y) + 'px';
        els.tip.style.left = Math.round(x) + 'px';
    }

    function schedule() {
        if (frame || !state) return;
        frame = requestAnimationFrame(function () { frame = 0; guard(position)(); });
    }

    /* ── input ───────────────────────────────────────────────────────── */
    function onClick(e) {
        var btn = e.target.closest ? e.target.closest('[data-tour-act]') : null;
        if (!btn || !state) return;
        var act = btn.getAttribute('data-tour-act');
        guard(function () {
            if (act === 'exit') exit();
            else if (act === 'back') go(state.index - 1, -1);
            else go(state.index + 1, 1);
        })();
    }

    function onKeydown(e) {
        if (!state || !els) return;
        var k = e.key;

        if (k === 'Escape')    { e.preventDefault(); e.stopPropagation(); guard(exit)(); return; }
        if (k === 'Tab')       { e.stopPropagation(); trap(e); return; }
        if (k === 'ArrowRight'){ e.preventDefault(); e.stopPropagation(); guard(function () { go(state.index + 1, 1); })(); return; }
        if (k === 'ArrowLeft') { e.preventDefault(); e.stopPropagation(); guard(function () { go(state.index - 1, -1); })(); return; }

        if (k === 'Enter' || k === ' ') {
            // A focused button handles its own Enter/Space; don't advance twice.
            if (e.target && els.tip.contains(e.target) && e.target.tagName === 'BUTTON') { e.stopPropagation(); return; }
            if (k === 'Enter') { e.preventDefault(); e.stopPropagation(); guard(function () { go(state.index + 1, 1); })(); }
            return;
        }

        // The layout binds bare letters to page navigation; a modal dialog is open.
        if (!e.ctrlKey && !e.metaKey && !e.altKey && k && k.length === 1) e.stopPropagation();
    }

    function trap(e) {
        var stops = els.tip.querySelectorAll('button:not([disabled])');
        if (!stops.length) { e.preventDefault(); return; }
        var first = stops[0], last = stops[stops.length - 1], active = document.activeElement;

        if (!els.tip.contains(active) || active === els.tip) {
            e.preventDefault();
            (e.shiftKey ? last : first).focus();
            return;
        }
        if (e.shiftKey && active === first) { e.preventDefault(); last.focus(); }
        else if (!e.shiftKey && active === last) { e.preventDefault(); first.focus(); }
    }

    /* ── entry points ────────────────────────────────────────────────── */
    // Any throw inside a running tour tears the overlay down: better no tour
    // than a dimmed page with no way out.
    function guard(fn) {
        return function () {
            try { return fn.apply(null, arguments); }
            catch (err) {
                if (window.console && console.error) console.error('[tour]', err);
                try { exit(); } catch (e) { teardown(); state = null; }
            }
        };
    }

    function start(id, index, nav, tried, ctx) {
        try {
            var tour = tourOf(id);
            if (!tour) return false;
            if (state) exit();

            state = {
                id: id,
                steps: tour.steps,
                index: 0,
                cand: null,
                nav: typeof nav === 'number' ? nav : -1,
                tried: tried && tried.length ? tried : [],
                ctx: ctx && typeof ctx === 'object' ? ctx : {},
                prevFocus: document.activeElement
            };
            readContext();
            build();
            go(typeof index === 'number' && index > 0 ? index : 0, 1);
            return true;
        } catch (err) {
            rethrowIfBug(err);
            if (window.console && console.error) console.error('[tour]', err);
            try { teardown(); } catch (e) { /* already gone */ }
            state = null;
            return false;
        }
    }

    // The dashboard's invitation. Shown until this browser takes the tour or
    // dismisses it; per browser rather than per account because the demo
    // account is shared. Browsers that saw the old auto-run count as done.
    function wireInvitation() {
        var prompt = document.querySelector('[data-tour-prompt]');
        if (prompt && !readStore('localStorage', SEEN_KEY)) prompt.hidden = false;

        document.addEventListener('click', function (e) {
            var starter = e.target.closest ? e.target.closest('[data-tour-start]') : null;
            if (starter) {
                markSeen();
                start(starter.getAttribute('data-tour-start') || 'overview');
                return;
            }
            var dismiss = e.target.closest ? e.target.closest('[data-tour-dismiss]') : null;
            if (dismiss) markSeen();
        });
    }

    // On a phone the Tour button lives inside the collapsed nav menu, so pressing it leaves that menu
    // open behind the overlay: a tall sticky header that every step then measures against, and a
    // target it can push under the docked card. Close the menu first and start once it has finished
    // closing. Bootstrap's own Collapse does the hiding, so its idea of the menu's state stays true.
    function closeMenuThen(fn) {
        var open = document.querySelector('.navbar-collapse.show');
        if (!open) { fn(); return; }
        var Collapse = window.bootstrap && window.bootstrap.Collapse;
        if (!Collapse) { open.classList.remove('show'); fn(); return; }
        open.addEventListener('hidden.bs.collapse', function once() {
            open.removeEventListener('hidden.bs.collapse', once);
            fn();
        });
        Collapse.getOrCreateInstance(open, { toggle: false }).hide();
    }

    // Taking the tour by any route counts: the invitation should not linger.
    function markSeen() {
        writeStore('localStorage', SEEN_KEY, '1');
        var prompt = document.querySelector('[data-tour-prompt]');
        if (prompt) prompt.hidden = true;
    }

    function init() {
        var btn = document.getElementById('tour-btn');
        if (btn) btn.addEventListener('click', function () { markSeen(); closeMenuThen(function () { start(forPage()); }); });

        wireInvitation();

        var raw = readStore('sessionStorage', STATE_KEY);
        if (raw) {
            clearStore('sessionStorage', STATE_KEY);
            var saved = null;
            try { saved = JSON.parse(raw); } catch (e) { rethrowIfBug(e); saved = null; }
            if (saved && available(saved.id)) start(saved.id, saved.index, saved.nav, saved.tried, saved.ctx);
        }
    }

    window.Tour = { start: function (id) { return start(id); }, available: available };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
