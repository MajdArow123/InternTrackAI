// The app layout's own behaviour: the dark-mode toggle and the keyboard shortcuts. Moved out of an inline
// block in _Layout.cshtml so the Content-Security-Policy can be enforced (script-src 'self').
(function () {
    'use strict';

    // Dark mode toggle
    var toggle = document.getElementById('theme-toggle');
    if (toggle) {
        toggle.addEventListener('click', function () {
            var curr = document.documentElement.getAttribute('data-theme');
            var next = curr === 'dark' ? 'light' : 'dark';
            document.documentElement.setAttribute('data-theme', next);
            // Blocked site data: the theme still switches, it just isn't remembered.
            try { localStorage.setItem('theme', next); } catch (e) { /* not fatal */ }
        });
    }

    // Keyboard shortcuts
    var kbdModal = document.getElementById('kbd-modal');
    if (!kbdModal) return;
    function openKbd() { kbdModal.classList.add('active'); kbdModal.setAttribute('aria-hidden', 'false'); }
    function closeKbd() { kbdModal.classList.remove('active'); kbdModal.setAttribute('aria-hidden', 'true'); }

    var close = document.getElementById('kbd-close');
    if (close) close.addEventListener('click', closeKbd);
    kbdModal.addEventListener('click', function (e) { if (e.target === kbdModal) closeKbd(); });

    function isTyping() {
        var el = document.activeElement;
        return el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT' || el.isContentEditable);
    }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') { closeKbd(); return; }
        if (isTyping()) return;
        if (e.ctrlKey || e.metaKey || e.altKey) return;
        switch (e.key) {
            case '?': e.preventDefault(); openKbd(); break;
            case 'n': case 'N': window.location.href = '/JobApplications/Create'; break;
            case 'd': case 'D': window.location.href = '/Home/Dashboard'; break;
            case 'c': case 'C': window.location.href = '/CoverLetter/Generate'; break;
            case 'p': case 'P': window.location.href = '/Profile'; break;
            case 's': case 'S':
                var s = document.querySelector('input[name="search"]');
                if (s) { e.preventDefault(); s.focus(); }
                break;
        }
    });
})();
