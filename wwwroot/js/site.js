// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

// Re-throws an error that a catch block was never written to handle.
//
// Why this exists: a guard against one failure will happily hide a different one. An autosave helper
// here wrapped localStorage in try/catch to survive a private window, and that catch silently
// swallowed a ReferenceError from a const in its temporal dead zone — so drafts never restored and
// nothing anywhere reported a problem. The same shape is all over this file's neighbours: the job
// analyzer wraps ~39 statements in one try and answers every one of them with "Request failed. Check
// your connection", so a TypeError while rendering a result would send the user to look at their wifi.
//
// ReferenceError is the one that is *always* a bug: an undefined identifier or a TDZ access is never a
// runtime condition anyone writes a catch for. It is therefore safe to re-throw from any catch.
// TypeError and SyntaxError are deliberately NOT re-thrown here, because they are legitimate expected
// failures in this codebase — fetch() rejects with TypeError when the network is down, and JSON.parse
// throws SyntaxError on a bad body. Callers that know neither applies to them re-throw those too.
//
// Usage: `catch (err) { rethrowIfBug(err); ...normal handling... }`
function rethrowIfBug(err) {
    if (err instanceof ReferenceError) throw err;
}

// Global toast helper for AJAX flows (the server-rendered TempData toast in _Layout.cshtml
// only fires on a full page load, so AJAX-driven saves call this instead).
function showAppToast(type, message) {
    document.getElementById('app-toast')?.remove();

    const toast = document.createElement('div');
    toast.id = 'app-toast';
    toast.className = `app-toast app-toast-${type}`;
    toast.setAttribute('role', 'alert');
    toast.setAttribute('aria-live', 'polite');
    toast.innerHTML =
        '<span class="app-toast-dot"></span>' +
        `<span class="app-toast-msg"></span>` +
        '<button type="button" class="app-toast-close" aria-label="Dismiss">&times;</button>';
    toast.querySelector('.app-toast-msg').textContent = message;
    toast.querySelector('.app-toast-close').addEventListener('click', () => dismissAppToast());

    document.body.appendChild(toast);
    requestAnimationFrame(() => toast.classList.add('app-toast-show'));
    clearTimeout(window.__appToastTimer);
    window.__appToastTimer = setTimeout(dismissAppToast, 4500);
}

function dismissAppToast() {
    const t = document.getElementById('app-toast');
    if (t) { t.classList.remove('app-toast-show'); setTimeout(() => t.remove(), 280); }
}

// Shakes a field briefly and shows an inline "This field is required" message under it.
// Used by client-side validation that must keep the user at their current scroll position
// instead of redirecting to a fresh page with server-rendered validation summaries.
function shakeField(inputEl, message) {
    if (!inputEl) return;
    inputEl.classList.remove('field-shake');
    inputEl.classList.add('field-invalid');

    let msgEl = inputEl.parentElement.querySelector('.field-error-msg');
    if (!msgEl) {
        msgEl = document.createElement('div');
        msgEl.className = 'field-error-msg';
        inputEl.insertAdjacentElement('afterend', msgEl);
    }
    msgEl.textContent = message || 'This field is required';

    void inputEl.offsetWidth;
    inputEl.classList.add('field-shake');

    const rect = inputEl.getBoundingClientRect();
    const offscreen = rect.top < 0 || rect.bottom > window.innerHeight;
    if (offscreen) inputEl.scrollIntoView({ behavior: 'smooth', block: 'center' });

    inputEl.addEventListener('input', function clearInvalid() {
        inputEl.classList.remove('field-invalid');
        msgEl?.remove();
        inputEl.removeEventListener('input', clearInvalid);
    }, { once: true });
}

function clearFieldError(inputEl) {
    if (!inputEl) return;
    inputEl.classList.remove('field-invalid', 'field-shake');
    inputEl.parentElement.querySelector('.field-error-msg')?.remove();
}


// Promise-based confirm dialog backed by the #app-confirm markup (see JobApplications/Index).
// Falls back to window.confirm when the markup is not on the page.
function appConfirm(opts) {
    opts = opts || {};
    const overlay = document.getElementById('app-confirm');
    if (!overlay) return Promise.resolve(window.confirm(opts.text || opts.title || 'Are you sure?'));

    const titleEl  = overlay.querySelector('#app-confirm-title');
    const textEl   = overlay.querySelector('#app-confirm-text');
    const okBtn    = overlay.querySelector('#app-confirm-ok');
    const cancelBtn= overlay.querySelector('#app-confirm-cancel');
    if (titleEl) titleEl.textContent = opts.title || 'Are you sure?';
    if (textEl)  textEl.textContent  = opts.text  || '';
    if (okBtn)   okBtn.textContent   = opts.okLabel || 'Confirm';

    return new Promise(resolve => {
        const previouslyFocused = document.activeElement;
        function close(result) {
            overlay.classList.remove('active');
            overlay.setAttribute('aria-hidden', 'true');
            document.removeEventListener('keydown', onKey);
            okBtn.removeEventListener('click', onOk);
            cancelBtn.removeEventListener('click', onCancel);
            overlay.removeEventListener('click', onBackdrop);
            if (previouslyFocused && previouslyFocused.focus) previouslyFocused.focus();
            resolve(result);
        }
        function onOk() { close(true); }
        function onCancel() { close(false); }
        function onBackdrop(e) { if (e.target === overlay) close(false); }
        function onKey(e) { if (e.key === 'Escape') close(false); }
        okBtn.addEventListener('click', onOk);
        cancelBtn.addEventListener('click', onCancel);
        overlay.addEventListener('click', onBackdrop);
        document.addEventListener('keydown', onKey);
        overlay.classList.add('active');
        overlay.setAttribute('aria-hidden', 'false');
        setTimeout(() => cancelBtn.focus(), 30);
    });
}

// Forms with data-confirm="message" get the styled confirm dialog instead of window.confirm.
// Optional data-confirm-title / data-confirm-ok override the title and button label.
document.addEventListener('submit', function (e) {
    const form = e.target;
    if (!(form instanceof HTMLFormElement) || !form.dataset.confirm || form.dataset.confirmed === '1') return;
    e.preventDefault();
    appConfirm({ title: form.dataset.confirmTitle || 'Are you sure?', text: form.dataset.confirm, okLabel: form.dataset.confirmOk || 'Delete' })
        .then(ok => { if (ok) { form.dataset.confirmed = '1'; form.requestSubmit ? form.requestSubmit() : form.submit(); } });
}, true);

// Publishes the sticky navbar's height as --site-nav-h on <html>, so sticky table headers sit directly under it
// and anchors / scrollIntoView clear it (site.css). Re-measured whenever the navbar resizes (breakpoints, the
// mobile menu opening, font loading).
(function () {
    const header = document.querySelector('body > header');
    if (!header) return;
    const root = document.documentElement;
    const publish = () => root.style.setProperty('--site-nav-h', header.getBoundingClientRect().height + 'px');
    publish();
    if ('ResizeObserver' in window) new ResizeObserver(publish).observe(header);
})();
