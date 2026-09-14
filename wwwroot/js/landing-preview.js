// Landing hero preview (Views/Home/_HeroPreview.cshtml): the mini navbar is a real tablist that
// swaps four static page mocks, and the Applications mock has a working List | Board toggle.
// Only the active panel/view is in flow (CSS), so the stage is measured from the active panel and
// kept at an explicit pixel height that transitions instead of jumping. Auto-advances every 6s
// until the visitor interacts; reduced motion disables the rotation (crossfades are CSS).
(function () {
    'use strict';

    var root = document.getElementById('lp-preview');
    if (!root) return;

    var stage = root.querySelector('.lp-shot-panels');
    var tabs = Array.prototype.slice.call(root.querySelectorAll('[role="tab"]'));
    var panels = tabs.map(function (t) { return document.getElementById(t.getAttribute('aria-controls')); });
    if (!stage || tabs.length < 2 || panels.some(function (p) { return !p; })) return;

    var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var current = Math.max(0, tabs.findIndex(function (t) { return t.getAttribute('aria-selected') === 'true'; }));
    var timer = null;
    var userTookOver = false;

    // ── Stage height: follows the active panel (tab switch, List/Board toggle, viewport resize) ──
    function fit() {
        stage.style.height = panels[current].offsetHeight + 'px';
    }

    // ── Page tabs ──
    function select(index, focus) {
        index = (index + tabs.length) % tabs.length;
        tabs.forEach(function (tab, k) {
            var on = k === index;
            tab.classList.toggle('on', on);
            tab.setAttribute('aria-selected', on ? 'true' : 'false');
            tab.tabIndex = on ? 0 : -1;
            panels[k].classList.toggle('is-active', on);
            if (on) panels[k].removeAttribute('aria-hidden');
            else panels[k].setAttribute('aria-hidden', 'true');
        });
        current = index;
        fit();
        if (focus) tabs[index].focus();
    }

    function stop() {
        if (timer) { clearInterval(timer); timer = null; }
    }

    function start() {
        if (reduceMotion || userTookOver || timer) return;
        timer = setInterval(function () {
            if (!document.hidden) select(current + 1, false);
        }, 6000);
    }

    function takeOver() {
        userTookOver = true;
        stop();
    }

    tabs.forEach(function (tab, k) {
        tab.addEventListener('click', function () {
            takeOver();
            select(k, false);
        });
        tab.addEventListener('keydown', function (e) {
            var next = null;
            if (e.key === 'ArrowRight' || e.key === 'ArrowDown') next = current + 1;
            else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') next = current - 1;
            else if (e.key === 'Home') next = 0;
            else if (e.key === 'End') next = tabs.length - 1;
            if (next === null) return;
            e.preventDefault();
            takeOver();
            select(next, true);
        });
    });

    // ── List | Board toggle inside the Applications mock ──
    // Board is the default on load; the DOM keeps whichever view was picked while the page is open.
    var toggles = Array.prototype.slice.call(root.querySelectorAll('.lp-view-toggle [aria-pressed]'));
    var views = toggles.map(function (b) { return document.getElementById(b.getAttribute('aria-controls')); });
    if (toggles.length && views.every(Boolean)) {
        function setView(index, focus) {
            index = (index + toggles.length) % toggles.length;
            toggles.forEach(function (btn, k) {
                var on = k === index;
                btn.classList.toggle('active', on);
                btn.setAttribute('aria-pressed', on ? 'true' : 'false');
                views[k].classList.toggle('is-active', on);
                if (on) views[k].removeAttribute('aria-hidden');
                else views[k].setAttribute('aria-hidden', 'true');
            });
            fit();
            if (focus) toggles[index].focus();
        }
        toggles.forEach(function (btn, k) {
            btn.addEventListener('click', function () {
                takeOver();
                setView(k, false);
            });
            btn.addEventListener('keydown', function (e) {
                var next = null;
                if (e.key === 'ArrowRight') next = k + 1;
                else if (e.key === 'ArrowLeft') next = k - 1;
                if (next === null) return;
                e.preventDefault();
                takeOver();
                setView(next, true);
            });
        });
    }

    // Initial height (no transition: it equals the panel's natural height), then keep it in sync
    // with the active panel as content wraps differently at other widths.
    fit();
    if ('ResizeObserver' in window) {
        panels.forEach(function (p) {
            new ResizeObserver(function () { if (p === panels[current]) fit(); }).observe(p);
        });
    } else {
        window.addEventListener('resize', fit);
    }

    // Only rotate while the frame is actually on screen.
    if ('IntersectionObserver' in window) {
        new IntersectionObserver(function (entries) {
            entries.forEach(function (entry) {
                if (entry.isIntersecting) start(); else stop();
            });
        }, { threshold: 0.25 }).observe(root);
    } else {
        start();
    }
})();
