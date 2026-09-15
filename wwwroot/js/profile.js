// Profile page behaviour: upload drop zones, skill/target-role chip inputs with AJAX
// autosave, the role preset combobox, basic-info save, resume AI analyze, score button
// loading state, and the calendar-link controls. Loaded from Views/Profile/Index.cshtml.
(function () {
// Upload drop zones: show the chosen filename and highlight on drag-over.
document.querySelectorAll('.upload-zone').forEach(function (zone) {
    var input = zone.querySelector('.upload-zone-input');
    var fileEl = zone.querySelector('.upload-zone-file');
    function showFile() {
        var f = input.files && input.files[0];
        if (fileEl) { fileEl.textContent = f ? f.name : ''; fileEl.hidden = !f; }
        zone.classList.toggle('has-file', !!f);
    }
    input.addEventListener('change', showFile);
    ['dragenter','dragover'].forEach(function (ev) { zone.addEventListener(ev, function (e) { e.preventDefault(); zone.classList.add('is-dragover'); }); });
    ['dragleave','drop'].forEach(function (ev) { zone.addEventListener(ev, function (e) { e.preventDefault(); zone.classList.remove('is-dragover'); }); });
    zone.addEventListener('drop', function (e) { if (e.dataTransfer && e.dataTransfer.files.length) { input.files = e.dataTransfer.files; showFile(); } });
});
// Disabled "Connect Gmail" on the demo account carries its explanation as a tooltip on the wrapper.
if (window.bootstrap?.Tooltip) {
    document.querySelectorAll('#gmailConnectDemoWrap[data-bs-toggle="tooltip"]').forEach(function (el) { new bootstrap.Tooltip(el); });
}

// ── Profile photo: the avatar circle opens the picker, uploads via fetch with an instant
//    preview, reverts on failure; "Remove photo" confirms then falls back to initials. ──
(function () {
    var wrap        = document.getElementById('avatarWrap');
    var btn         = document.getElementById('avatarBtn');
    var input       = document.getElementById('photoInput');
    var img         = document.getElementById('avatarImg');
    var placeholder = document.getElementById('avatarPlaceholder');
    var removeBtn   = document.getElementById('removePhotoBtn');
    if (!wrap || !btn || !input || !img || !placeholder) return;
    var token = document.querySelector('#photoForm input[name="__RequestVerificationToken"]')?.value || '';
    var MAX_BYTES = 2 * 1024 * 1024;
    var busy = false;

    function showPhoto(src) {
        img.src = src; img.hidden = false; placeholder.hidden = true;
        wrap.dataset.hasPhoto = 'true';
        if (removeBtn) removeBtn.hidden = false;
    }
    function showInitials(initials) {
        if (initials) placeholder.textContent = initials;
        img.hidden = true; img.removeAttribute('src'); placeholder.hidden = false;
        wrap.dataset.hasPhoto = 'false';
        if (removeBtn) removeBtn.hidden = true;
    }
    function snapshot() {
        return { hasPhoto: wrap.dataset.hasPhoto === 'true', src: img.getAttribute('src') || '', initials: placeholder.textContent };
    }
    function restore(s) { if (s.hasPhoto) showPhoto(s.src); else showInitials(s.initials); }
    function setBusy(on) { busy = on; wrap.classList.toggle('is-uploading', on); btn.setAttribute('aria-busy', on ? 'true' : 'false'); }

    // Enter/Space on the <button> fire click natively; the picker itself stays out of the tab order.
    btn.addEventListener('click', function () { if (!busy) input.click(); });

    input.addEventListener('change', function () {
        var file = input.files && input.files[0];
        input.value = '';   // so picking the same file again re-triggers change
        if (!file) return;
        if (!/^image\//.test(file.type)) { showAppToast('error', 'Please choose an image file.'); return; }
        if (file.size > MAX_BYTES)      { showAppToast('error', 'Photo must be under 2 MB.'); return; }

        var previous = snapshot();
        var previewUrl = URL.createObjectURL(file);
        showPhoto(previewUrl);
        setBusy(true);

        var fd = new FormData();
        fd.append('photo', file, file.name);
        fd.append('__RequestVerificationToken', token);
        fetch('/Profile/UploadPhoto', { method: 'POST', body: fd, headers: { 'X-Requested-With': 'XMLHttpRequest' } })
            .then(function (r) { return r.json().catch(function () { return {}; }).then(function (d) { return { ok: r.ok, data: d }; }); })
            .then(function (res) {
                if (res.ok && res.data.success && res.data.url) {
                    showPhoto(res.data.url);
                    showAppToast('success', 'Photo updated');
                } else {
                    restore(previous);
                    showAppToast('error', res.data.error || 'Could not update the photo. Try again.');
                }
            })
            .catch(function () { restore(previous); showAppToast('error', 'Could not update the photo. Check your connection and try again.'); })
            .finally(function () { setBusy(false); URL.revokeObjectURL(previewUrl); });
    });

    removeBtn?.addEventListener('click', function () {
        if (busy) return;
        appConfirm({ title: 'Remove profile photo?', text: 'Your initials will be shown instead.', okLabel: 'Remove' }).then(function (ok) {
            if (!ok) return;
            setBusy(true);
            var body = new URLSearchParams({ __RequestVerificationToken: token });
            fetch('/Profile/RemovePhoto', { method: 'POST', body: body, headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(function (r) { return r.json(); })
                .then(function (d) {
                    if (d && d.success) { showInitials(d.initials); showAppToast('success', 'Photo removed'); }
                    else showAppToast('error', (d && d.error) || 'Could not remove the photo.');
                })
                .catch(function () { showAppToast('error', 'Could not remove the photo. Try again.'); })
                .finally(function () { setBusy(false); });
        });
    });
})();

    (function () {

        const ROLE_PRESETS = [
            "Software Engineering Intern", "Backend Developer Intern", "Frontend Developer Intern",
            "Full-Stack Developer Intern", "Data Science Intern", "Machine Learning Intern",
            "DevOps Intern", "Cloud Engineering Intern", "Mobile Developer Intern", "QA Engineer Intern",
            "Cybersecurity Intern", "Product Manager Intern", "Data Engineer Intern",
            "iOS Developer Intern", "Android Developer Intern"
        ];

        // Initial chip values are rendered server-side into the hidden inputs' value attributes.
        function readJsonInput(id) {
            try { const arr = JSON.parse(document.getElementById(id)?.value || '[]'); return Array.isArray(arr) ? arr : []; }
            catch (_) { return []; }
        }

        const antiForgeryToken = document.querySelector('#infoForm input[name="__RequestVerificationToken"]')?.value || '';

        function postForm(url, fields) {
            const body = new URLSearchParams({ __RequestVerificationToken: antiForgeryToken, ...fields });
            return fetch(url, { method: 'POST', body, headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(r => r.json().then(data => {
                    if (r.status === 429 && data.error) showAppToast('error', data.error);
                    return data;
                }));
        }

        function flashSaveIndicator(el) {
            if (!el) return;
            el.classList.add('show');
            clearTimeout(el.__hideTimer);
            el.__hideTimer = setTimeout(() => el.classList.remove('show'), 2000);
        }

        // ── Tag inputs: shared TagInput module (wwwroot/js/tag-input.js) ──
        function initTagInput(cloudId, hiddenId, textId, addBtnId, initialValues, opts) {
            return TagInput.create({
                cloud:     document.getElementById(cloudId),
                hidden:    document.getElementById(hiddenId),
                input:     document.getElementById(textId),
                addButton: document.getElementById(addBtnId),
                values:    initialValues,
                onChange:  opts && opts.onChange,
                onAfterAdd: opts && opts.onAfterAdd
            });
        }

        // ── Skills: tag input with AJAX autosave ─────────
        const skillsApi = initTagInput('skillsCloud', 'skillsJsonInput', 'skillsText', 'skillsAdd',
            readJsonInput('skillsJsonInput'),
            {
                onChange: (values) => {
                    postForm('/Profile/SaveSkills', { skillsJson: JSON.stringify(values) })
                        .then(res => {
                            if (!res.success) return;
                            // The server returns the normalised list (stale duplicates merged); mirror it.
                            if (Array.isArray(res.skills)) skillsApi.set(res.skills);
                            flashSaveIndicator(document.getElementById('skillsSaveIndicator'));
                        });
                }
            });

        // ── Target roles: tag input + searchable combobox with AJAX autosave ──
        const rolesApi = initTagInput('rolesCloud', 'rolesJsonInput', 'rolesText', 'rolesAdd',
            readJsonInput('rolesJsonInput'),
            {
                onChange: (values) => {
                    postForm('/Profile/SaveTargetRoles', { targetRolesJson: JSON.stringify(values) })
                        .then(res => {
                            if (!res.success) return;
                            if (Array.isArray(res.targetRoles)) rolesApi.set(res.targetRoles);
                            flashSaveIndicator(document.getElementById('rolesSaveIndicator'));
                        });
                },
                onAfterAdd: () => closeComboList()
            });

        const rolesText = document.getElementById('rolesText');
        const comboList = document.getElementById('rolesComboList');

        function closeComboList() { comboList.classList.remove('open'); comboList.innerHTML = ''; }

        function renderComboList(filter) {
            const taken = rolesApi.values();
            const matches = ROLE_PRESETS.filter(r =>
                TagInput.findDuplicate(taken, r) < 0 && r.toLowerCase().includes(filter.toLowerCase()));

            comboList.innerHTML = '';
            if (matches.length === 0) {
                comboList.innerHTML = '<li class="role-combobox-empty">No presets match — press Enter to add as a custom role</li>';
            } else {
                matches.forEach(m => {
                    const li = document.createElement('li');
                    li.className = 'role-combobox-option';
                    li.textContent = m;
                    li.addEventListener('mousedown', e => { e.preventDefault(); rolesApi.add(m); closeComboList(); });
                    comboList.appendChild(li);
                });
            }
            comboList.classList.add('open');
        }

        rolesText.addEventListener('input', () => renderComboList(rolesText.value));
        rolesText.addEventListener('focus', () => renderComboList(rolesText.value));
        rolesText.addEventListener('blur', () => setTimeout(closeComboList, 120));

        // ── Personal Info: AJAX save with shake-on-required validation ──
        const infoForm     = document.getElementById('infoForm');
        const fullNameInput = document.getElementById('fullNameInput');
        const saveInfoBtn   = document.getElementById('saveInfoBtn');

        infoForm.addEventListener('submit', e => {
            e.preventDefault();

            if (!fullNameInput.value.trim()) {
                shakeField(fullNameInput, 'This field is required');
                return;
            }
            clearFieldError(fullNameInput);

            const fd = new FormData(infoForm);
            saveInfoBtn.disabled = true;
            postForm('/Profile/SaveInfo', Object.fromEntries(fd.entries()))
                .then(res => {
                    saveInfoBtn.disabled = false;
                    if (res.success) {
                        flashSaveIndicator(document.getElementById('infoSaveIndicator'));
                        const nowEl = document.getElementById('timeZoneNow');
                        if (nowEl && res.nowLocal) nowEl.textContent = res.nowLocal;
                        showAppToast('success', 'Profile info saved.');
                    } else if (res.field === 'displayName') {
                        shakeField(document.getElementById('displayNameInput'), res.error || 'Not available on the demo account.');
                    } else if (res.field === 'timeZoneId') {
                        shakeField(document.getElementById('timeZoneSelect'), res.error || 'Unknown time zone');
                    } else {
                        shakeField(fullNameInput, res.error || 'This field is required');
                    }
                })
                .catch(() => { saveInfoBtn.disabled = false; showAppToast('error', 'Could not save — try again.'); });
        });

        // ── Resume analyze with AI (Section 1a) ──
        const analyzeBtn      = document.getElementById('analyzeBtn');
        const analyzeBtnLabel = document.getElementById('analyzeBtnLabel');
        const analyzeError    = document.getElementById('analyzeError');

        if (analyzeBtn) {
            analyzeBtn.addEventListener('click', () => {
                analyzeBtn.disabled = true;
                analyzeBtnLabel.innerHTML = '<span class="spinner-border spinner-border-sm analyze-spinner"></span> Analyzing...';
                analyzeError.style.display = 'none';

                ['fullNameWrap', 'skillsWrap', 'rolesWrap'].forEach(id => {
                    const wrap = document.getElementById(id);
                    const skel = document.createElement('div');
                    skel.className = 'skeleton-line';
                    wrap.appendChild(skel);
                    wrap.classList.add('skeleton-active');
                });

                postForm('/Profile/AnalyzeResume', {})
                    .then(res => {
                        ['fullNameWrap', 'skillsWrap', 'rolesWrap'].forEach(id => {
                            const wrap = document.getElementById(id);
                            wrap.querySelector('.skeleton-line')?.remove();
                            wrap.classList.remove('skeleton-active');
                        });
                        analyzeBtn.disabled = false;
                        analyzeBtnLabel.textContent = 'Analyze with AI';

                        if (!res.success) {
                            analyzeError.textContent = res.error || 'Could not analyze resume.';
                            analyzeError.style.display = 'block';
                            return;
                        }

                        // The server has already merged and saved (add, never remove); mirror its state here.
                        if (res.fullName && !fullNameInput.value.trim()) fullNameInput.value = res.fullName;
                        if (Array.isArray(res.skills))      skillsApi.set(res.skills);
                        if (Array.isArray(res.targetRoles)) rolesApi.set(res.targetRoles);
                        skillsApi.highlight(res.addedSkills);
                        rolesApi.highlight(res.addedRoles);

                        const added = res.nameFilled || res.skillsAdded > 0 || res.rolesAdded > 0;
                        showAppToast(added ? 'success' : 'info', 'Resume analyzed \u2014 ' + (res.summary || (added ? 'profile updated' : 'nothing new to add')) + '.');
                    })
                    .catch(() => {
                        ['fullNameWrap', 'skillsWrap', 'rolesWrap'].forEach(id => {
                            const wrap = document.getElementById(id);
                            wrap.querySelector('.skeleton-line')?.remove();
                            wrap.classList.remove('skeleton-active');
                        });
                        analyzeBtn.disabled = false;
                        analyzeBtnLabel.textContent = 'Analyze with AI';
                        analyzeError.textContent = 'Request failed. Check your connection and try again.';
                        analyzeError.style.display = 'block';
                    });
            });
        }

        // After an upload the page reloads; flash the chips the auto-fill just added.
        const autoFillAdded = document.getElementById('autoFillAdded');
        if (autoFillAdded) {
            try {
                const added = JSON.parse(autoFillAdded.value || '{}');
                setTimeout(() => { skillsApi.highlight(added.skills); rolesApi.highlight(added.roles); }, 150);
            } catch (_) { /* malformed payload: nothing to highlight */ }
        }

        // Loading state for score button
        const scoreBtn = document.getElementById('scoreBtn');
        if (scoreBtn) {
            scoreBtn.closest('form').addEventListener('submit', () => {
                scoreBtn.disabled = true;
                scoreBtn.innerHTML =
                    '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> Scoring...';
            });
        }

        // Scroll to score result if present (it renders inside the Resume card after a full-page POST)
        const scoreCard = document.getElementById('resumeScoreResult');
        if (scoreCard) {
            setTimeout(() => scoreCard.scrollIntoView({ behavior: 'smooth', block: 'start' }), 200);
        }

        // ── Reminders: follow-up window ─────────────────
        const reminderForm  = document.getElementById('reminderForm');
        const reminderInput = document.getElementById('followUpAfterDaysInput');
        const reminderBtn   = document.getElementById('saveReminderBtn');

        reminderForm?.addEventListener('submit', e => {
            e.preventDefault();
            const days = parseInt(reminderInput.value, 10);
            const min = parseInt(reminderInput.min, 10), max = parseInt(reminderInput.max, 10);
            if (!Number.isInteger(days) || days < min || days > max) {
                shakeField(reminderInput, `Choose between ${min} and ${max} days`);
                return;
            }
            clearFieldError(reminderInput);
            reminderBtn.disabled = true;
            postForm('/Profile/SaveReminderSettings', { followUpAfterDays: days })
                .then(res => {
                    reminderBtn.disabled = false;
                    if (res.success) {
                        reminderInput.value = res.followUpAfterDays;
                        flashSaveIndicator(document.getElementById('reminderSaveIndicator'));
                        showAppToast('success', `Follow-up reminders now trigger after ${res.followUpAfterDays} days.`);
                    } else {
                        shakeField(reminderInput, res.error || 'Invalid value');
                    }
                })
                .catch(() => { reminderBtn.disabled = false; showAppToast('error', 'Could not save — try again.'); });
        });

        // ── Calendar feed link ───────────────────────────
        const calendarInput     = document.getElementById('calendarFeedInput');
        const copyCalendarBtn   = document.getElementById('copyCalendarBtn');
        const regenCalendarBtn  = document.getElementById('regenCalendarBtn');
        const calendarIndicator = document.getElementById('calendarIndicator');

        copyCalendarBtn?.addEventListener('click', () => {
            if (!calendarInput.value) return;
            navigator.clipboard.writeText(calendarInput.value).then(() => {
                calendarIndicator.textContent = 'Copied \u2713';
                flashSaveIndicator(calendarIndicator);
            });
        });

        regenCalendarBtn?.addEventListener('click', () => {
            appConfirm({
                title: 'Regenerate calendar link?',
                text: 'Calendars subscribed with the current link will stop updating until you add the new one.',
                okLabel: 'Regenerate'
            }).then(ok => {
                if (!ok) return;
                postForm('/Profile/RegenerateCalendarToken', {})
                    .then(res => {
                        if (!res.success) return;
                        calendarInput.value = res.url;
                        calendarIndicator.textContent = 'New link ready \u2713';
                        flashSaveIndicator(calendarIndicator);
                        showAppToast('success', 'Calendar link regenerated. Re-subscribe with the new URL.');
                    });
            });
        });

    })();

// ── Resume labels: click the pencil, type a name, Enter/blur saves, Esc cancels ──
(function () {
    const token = document.querySelector('#resumeRenameForm input[name="__RequestVerificationToken"]')?.value || '';
    document.querySelectorAll('.doc-label-row[data-resume-id]').forEach(function (row) {
        const id    = row.dataset.resumeId;
        const label = row.querySelector('[data-resume-label]');
        const btn   = row.querySelector('[data-resume-rename]');
        const input = row.querySelector('.doc-label-input');
        if (!label || !btn || !input) return;
        let saving = false;

        function open() {
            label.hidden = true; btn.hidden = true; input.hidden = false;
            input.focus(); input.select();
        }
        function close() {
            input.hidden = true; label.hidden = false; btn.hidden = false;
        }
        function save() {
            if (saving) return;
            saving = true;
            const body = new URLSearchParams({ __RequestVerificationToken: token, id: id, label: input.value });
            fetch('/Profile/RenameResume', { method: 'POST', body: body, headers: { 'X-Requested-With': 'XMLHttpRequest' } })
                .then(function (r) { return r.json(); })
                .then(function (d) {
                    if (d && d.success) {
                        label.textContent = d.label;
                        input.value = input.value.trim();
                        if (typeof showAppToast === 'function') showAppToast('success', 'Resume renamed.');
                    } else if (typeof showAppToast === 'function') {
                        showAppToast('error', (d && d.error) || 'Could not rename the resume.');
                    }
                })
                .catch(function () { if (typeof showAppToast === 'function') showAppToast('error', 'Could not rename the resume.'); })
                .finally(function () { saving = false; close(); });
        }

        btn.addEventListener('click', open);
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); save(); }
            if (e.key === 'Escape') { e.preventDefault(); input.value = input.dataset.last || input.value; close(); }
        });
        input.addEventListener('focus', function () { input.dataset.last = input.value; });
        input.addEventListener('blur', function () { if (!input.hidden) save(); });
    });
})();

})();
