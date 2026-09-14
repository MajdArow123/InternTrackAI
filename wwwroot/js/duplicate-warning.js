// "Save anyway" on the possible-duplicate warning shown by the Create and Edit application
// pages: flips the force flag (forceCreate / forceSave) and resubmits the form.
(function () {
    var btn = document.getElementById('saveAnywayBtn');
    if (!btn) return;
    btn.addEventListener('click', function () {
        var flag = document.getElementById('forceCreate') || document.getElementById('forceSave');
        var form = document.getElementById('appForm');
        if (!flag || !form) return;
        flag.value = 'true';
        form.submit();
    });
})();
