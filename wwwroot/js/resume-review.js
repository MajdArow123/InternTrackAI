// Review resume analysis (/Profile/ReviewResume).
// The screen works entirely without this file — every control is a real form input and the two
// buttons are real submits. This only adds the double-submit guard and the live count.
(function () {
    'use strict';

    const form = document.getElementById('resumeReviewForm');
    if (!form) return;

    const applyBtn = document.getElementById('applyReviewBtn');

    // Applying twice would merge twice. The second merge adds nothing (ProfileTags.Merge is a union)
    // and the server refuses an already-applied draft, but a spinner beats a confusing second redirect.
    form.addEventListener('submit', function (e) {
        // The Discard button posts the other form via form=""; leave it alone.
        if (e.submitter && e.submitter.getAttribute('form')) return;

        if (applyBtn.disabled) { e.preventDefault(); return; }
        applyBtn.disabled = true;
        applyBtn.innerHTML =
            '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> Applying...';
    });
})();
