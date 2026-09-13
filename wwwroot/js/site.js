// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

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
