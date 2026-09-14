// Landing hero preview (Views/Home/_HeroPreview.cshtml): the mini navbar is a real tablist that
// swaps four static page mocks. Auto-advances every 6s until the visitor picks a tab themselves.
// Crossfade and mobile sizing are CSS (.lp-shot-panel); reduced motion disables the rotation here.
(function () {
    'use strict';

    var root = document.getElementById('lp-preview');
    if (!root) return;

    var tabs = Array.prototype.slice.call(root.querySelectorAll('[role="tab"]'));
    var panels = tabs.map(function (t) { return document.getElementById(t.getAttribute('aria-controls')); });
    if (tabs.length < 2 || panels.some(function (p) { return !p; })) return;

    var reduceMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    var current = Math.max(0, tabs.findIndex(function (t) { return t.getAttribute('aria-selected') === 'true'; }));
    var timer = null;
    var userTookOver = false;

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
