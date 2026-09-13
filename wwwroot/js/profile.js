// Profile page behaviour: upload drop zones, skill/target-role chip inputs with AJAX
// autosave, the role preset combobox, basic-info save, resume AI analyze, score button
// loading state, and the public-profile link controls. Loaded from Views/Profile/Index.cshtml.
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
document.querySelectorAll('.file-pick-input').forEach(function (input) {
    input.addEventListener('change', function () {
        var label = input.closest('.file-pick')?.querySelector('.file-pick-label');
        if (label) label.textContent = input.files[0] ? input.files[0].name : 'Choose photo';
    });
});

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

        // ── Tag input helper ─────────────────────────────
        function initTagInput(cloudId, hiddenId, textId, addBtnId, initialValues, opts) {
            opts = opts || {};
            const cloud   = document.getElementById(cloudId);
            const hidden  = document.getElementById(hiddenId);
            const textEl  = document.getElementById(textId);
            const addBtn  = document.getElementById(addBtnId);
            let values    = Array.isArray(initialValues) ? [...initialValues] : [];

            function render() {
                cloud.innerHTML = '';
                values.forEach((val, i) => {
                    const tag = document.createElement('span');
                    tag.className = 'tag-item';
                    tag.innerHTML = `<span class="tag-item-label">${escHtml(val)}</span><button type="button" data-i="${i}" aria-label="Remove">&times;</button>`;
                    cloud.appendChild(tag);
                });
                hidden.value = JSON.stringify(values);
            }

            function persist() {
                render();
                if (opts.onChange) opts.onChange(values);
            }

            cloud.addEventListener('click', e => {
                const btn = e.target.closest('button[data-i]');
                if (btn) { values.splice(parseInt(btn.dataset.i), 1); persist(); }
            });

            function add(value) {
                const v = (value !== undefined ? value : textEl.value).trim();
                if (v && !values.includes(v)) { values.push(v); persist(); }
                textEl.value = '';
                textEl.focus();
                if (opts.onAfterAdd) opts.onAfterAdd();
            }

            addBtn.addEventListener('click', () => add());
            textEl.addEventListener('keydown', e => { if (e.key === 'Enter') { e.preventDefault(); add(); } });

            render();
            return { values: () => values, add, render };
        }

        function escHtml(s) {
            return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
        }

        // ── Skills: tag input with AJAX autosave ─────────
        const skillsApi = initTagInput('skillsCloud', 'skillsJsonInput', 'skillsText', 'skillsAdd',
            readJsonInput('skillsJsonInput'),
            {
                onChange: (values) => {
                    postForm('/Profile/SaveSkills', { skillsJson: JSON.stringify(values) })
                        .then(res => { if (res.success) flashSaveIndicator(document.getElementById('skillsSaveIndicator')); });
                }
            });

        // ── Target roles: tag input + searchable combobox with AJAX autosave ──
        const rolesApi = initTagInput('rolesCloud', 'rolesJsonInput', 'rolesText', 'rolesAdd',
            readJsonInput('rolesJsonInput'),
            {
                onChange: (values) => {
                    postForm('/Profile/SaveTargetRoles', { targetRolesJson: JSON.stringify(values) })
                        .then(res => { if (res.success) flashSaveIndicator(document.getElementById('rolesSaveIndicator')); });
                },
                onAfterAdd: () => closeComboList()
            });

        const rolesText = document.getElementById('rolesText');
        const comboList = document.getElementById('rolesComboList');

        function closeComboList() { comboList.classList.remove('open'); comboList.innerHTML = ''; }

        function renderComboList(filter) {
            const taken = rolesApi.values();
            const matches = ROLE_PRESETS.filter(r =>
                !taken.includes(r) && r.toLowerCase().includes(filter.toLowerCase()));

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
                        showAppToast('success', 'Profile info saved.');
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

                        let filledSomething = false;

                        if (res.fullName && !fullNameInput.value.trim()) {
                            fullNameInput.value = res.fullName;
                            filledSomething = true;
                        }
                        (res.skills || []).forEach(s => { skillsApi.add(s); filledSomething = true; });
                        (res.targetRoles || []).forEach(r => { rolesApi.add(r); filledSomething = true; });

                        if (filledSomething) {
                            showAppToast('success', 'Profile auto-filled from your resume. Review and click Save Info to keep the name.');
                        } else {
                            showAppToast('info', 'No new details found to fill in.');
                        }
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

        // Loading state for score button
        const scoreBtn = document.getElementById('scoreBtn');
        if (scoreBtn) {
            scoreBtn.closest('form').addEventListener('submit', () => {
                scoreBtn.disabled = true;
                scoreBtn.innerHTML =
                    '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> Scoring...';
            });
        }

        // Scroll to score result if present
        const scoreCard = document.querySelector('.match-score-card');
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

        // ── Public Profile Link ──────────────────────────
        const publicToggle  = document.getElementById('publicProfileToggle');
        const publicLinkRow = document.getElementById('publicLinkRow');
        const publicLinkInput = document.getElementById('publicLinkInput');
        const copyLinkBtn   = document.getElementById('copyPublicLinkBtn');
        const regenLinkBtn  = document.getElementById('regenLinkBtn');
        const publicLinkIndicator = document.getElementById('publicLinkIndicator');

        publicToggle?.addEventListener('change', () => {
            postForm('/Profile/TogglePublic', { isPublic: publicToggle.checked })
                .then(res => {
                    if (!res.success) return;
                    if (res.url) publicLinkInput.value = res.url;
                    publicLinkRow.classList.toggle('d-none', !res.isPublic);
                });
        });

        copyLinkBtn?.addEventListener('click', () => {
            if (!publicLinkInput.value) return;
            navigator.clipboard.writeText(publicLinkInput.value).then(() => flashSaveIndicator(publicLinkIndicator));
        });

        regenLinkBtn?.addEventListener('click', () => {
            postForm('/Profile/RegenerateLink', {})
                .then(res => {
                    if (!res.success) return;
                    publicLinkInput.value = res.url;
                    flashSaveIndicator(publicLinkIndicator);
                });
        });

    })();
})();
