// Applies the saved theme before first paint. Loaded as a blocking script in <head> of both layouts, on
// purpose: deferred, the page would paint light and then flash dark. It used to be inline; it is a file so
// the Content-Security-Policy can be enforced with script-src 'self' and no 'unsafe-inline'.
(function () {
    var t = 'light';
    try { t = localStorage.getItem('theme') || 'light'; } catch (e) { /* blocked site data: default theme */ }
    document.documentElement.setAttribute('data-theme', t);
})();
