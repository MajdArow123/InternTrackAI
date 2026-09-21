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
//    {id, index, nav} in sessionStorage and pick it back up on the next load.
//  * Every storage call can throw (private mode, blocked site data). All of
//    them go through readStore/writeStore, which swallow it: an unreadable
//    "seen" flag counts as not-yet-seen, never as a crash.
//  * A step whose target matches nothing is skipped in the direction of
//    travel rather than dimming the page around nothing.
(function () {
    'use strict';

    var SEEN_KEY  = 'itai.tour.v1';        // localStorage: overview auto-run, once per browser
    var STATE_KEY = 'itai.tour.state';     // sessionStorage: resume point across a navigation
    var DOCK_MQ   = '(max-width: 639.98px)';
    var GAP       = 12;                    // tooltip ↔ spotlight
    var EDGE      = 12;                    // tooltip ↔ viewport edge

    var state = null;   // { id, steps, index, nav, prevFocus } while running
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
    function persist(index) {
        writeStore('sessionStorage', STATE_KEY, JSON.stringify({ id: state.id, index: index, nav: index }));
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

    // Walks from `index` in the direction of travel, skipping steps whose
    // target isn't on the page, and navigating when a step lives elsewhere.
    function go(index, dir) {
        if (!state) return;
        dir = dir < 0 ? -1 : 1;
        if (index < 0 && dir < 0) return;   // Back on the first step: stay put

        var steps = state.steps;
        while (index >= 0 && index < steps.length) {
            var step = steps[index] || {};

            if (step.view && !samePath(window.location.pathname, step.view)) {
                if (state.nav === index) { index += dir; continue; }   // we already tried: unreachable
                persist(index);
                window.location.assign(step.view);
                return;
            }
            if (step.target && !document.querySelector(step.target)) { index += dir; continue; }

            state.index = index;
            render();
            return;
        }
        if (dir < 0) { render(); return; }  // nothing earlier is reachable
        exit();
    }

    function render() {
        var step = state.steps[state.index] || {};
        els.title.textContent = step.title || '';
        els.body.textContent  = step.body || '';
        els.count.textContent = (state.index + 1) + ' of ' + state.steps.length;
        els.back.disabled     = state.index === 0;
        els.next.textContent  = state.index === state.steps.length - 1 ? 'Done' : 'Next';

        reveal(step.target ? document.querySelector(step.target) : null);
        position();

        // Focus the dialog itself, not a control: screen readers then announce
        // the title and body before the buttons.
        try { els.tip.focus({ preventScroll: true }); } catch (e) { els.tip.focus(); }
    }

    function reveal(el) {
        if (!el) return;
        var r = el.getBoundingClientRect();
        var navH = parseInt(getComputedStyle(document.documentElement).getPropertyValue('--site-nav-h'), 10) || 53;
        if (r.top >= navH + 8 && r.bottom <= window.innerHeight - 8) return;
        try { el.scrollIntoView({ block: 'center', behavior: reducedMotion() ? 'auto' : 'smooth' }); }
        catch (e) { el.scrollIntoView(); }
    }

    function position() {
        if (!state || !els) return;
        var step = state.steps[state.index] || {};
        var el   = step.target ? document.querySelector(step.target) : null;
        var dock = docked();

        els.tip.classList.toggle('tour-tip--docked', dock);

        if (!el) {
            els.spot.hidden = true;
            els.overlay.classList.add('tour-overlay--plain');
            els.tip.classList.add('tour-tip--center');
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

        if (dock) { els.tip.style.top = els.tip.style.left = ''; return; }

        var tip = els.tip.getBoundingClientRect();
        var vh  = window.innerHeight, vw = window.innerWidth;
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

    function start(id, index, nav) {
        try {
            var tour = tourOf(id);
            if (!tour) return false;
            if (state) exit();

            state = {
                id: id,
                steps: tour.steps,
                index: 0,
                nav: typeof nav === 'number' ? nav : -1,
                prevFocus: document.activeElement
            };
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

    function autoStart() {
        var all = tours();
        for (var id in all) {
            if (!Object.prototype.hasOwnProperty.call(all, id)) continue;
            if (!all[id].auto || !samePath(window.location.pathname, all[id].auto)) continue;
            if (readStore('localStorage', SEEN_KEY)) return;   // already shown on this browser
            // Written before the run, so a mid-tour reload doesn't restart it
            // forever. Unwritable storage means it runs again next visit.
            writeStore('localStorage', SEEN_KEY, '1');
            start(id);
            return;
        }
    }

    function init() {
        var btn = document.getElementById('tour-btn');
        if (btn) btn.addEventListener('click', function () { start(forPage()); });

        var raw = readStore('sessionStorage', STATE_KEY);
        if (raw) {
            clearStore('sessionStorage', STATE_KEY);
            var saved = null;
            try { saved = JSON.parse(raw); } catch (e) { saved = null; }
            if (saved && available(saved.id)) { start(saved.id, saved.index, saved.nav); return; }
        }
        autoStart();
    }

    window.Tour = { start: function (id) { return start(id); }, available: available };

    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
    else init();
})();
