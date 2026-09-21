// Add Application page: the AI Job Analyzer panel (analyze → autofill → resume auto-match),
// loaded from Views/JobApplications/Create.cshtml. The duplicate "Save anyway" button lives in
// duplicate-warning.js (shared with Edit).
(function () {
    // AI Job Analyzer panel: lets the user paste a job posting (URL or full text),
    // sends it to the backend for extraction, auto-fills the form, and then
    // kicks off a resume match against the user's active resume.
    (function () {
        const card       = document.getElementById('analyzerCard');
        const body       = document.getElementById('analyzerBody');
        const toggleBtn  = document.getElementById('analyzerToggleBtn');
        const textarea   = document.getElementById('analyzerTextarea');
        const analyzeBtn = document.getElementById('analyzeBtn');
        const resultEl   = document.getElementById('analyzerResult');
        const errorEl    = document.getElementById('analyzerError');

        // ── Toggle open/close ────────────────────────────────
        // Expands/collapses the analyzer body and flips the toggle button label.
        function setOpen(open) {
            card.classList.toggle('analyzer-open', open);
            toggleBtn.textContent = open ? 'Close' : 'Open Analyzer';
        }

        // Clicking anywhere on the header toggles the panel, except the Analyze
        // button itself (which has its own click handler below).
        document.getElementById('analyzerToggle').addEventListener('click', function (e) {
            if (e.target === analyzeBtn) return;
            setOpen(!card.classList.contains('analyzer-open'));
        });

        // ── Analyze ──────────────────────────────────────────
        // Sends the pasted text/URL to the AI analyzer endpoint, auto-fills the
        // form from the extracted fields, then triggers a resume match.
        analyzeBtn.addEventListener('click', async function (e) {
            e.stopPropagation();
            const text = textarea.value.trim();
            if (!text) { textarea.focus(); return; }

            // A URL means we only have the link, not the full posting text — the
            // backend will fetch and parse the page server-side. Pasted text is
            // analyzed directly. This distinction also drives how JobDescription
            // gets filled below.
            const isUrl = /^https?:\/\//i.test(text);
            setLoading(true, isUrl);
            resultEl.hidden = true;
            errorEl.hidden  = true;

            // CSRF is normally validated via this token; this AJAX endpoint is
            // marked [IgnoreAntiforgeryToken] server-side (it's a read-only,
            // authenticated-page-only analysis call), so the header is sent but
            // not strictly required — kept for defense in depth / consistency.
            const token = document.querySelector('[name="__RequestVerificationToken"]').value;

            try {
                const resp = await fetch('/Analyzer/Analyze', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': token
                    },
                    body: JSON.stringify({ jobDescription: text })
                });

                const data = await resp.json();
                if (resp.status === 429 && data.error) showAppToast('error', data.error);

                if (data.success) {
                    autofill('CompanyName',   data.companyName);
                    autofill('RoleTitle',     data.roleTitle);
                    autofill('Location',      data.location);
                    autofill('Salary',        data.salary);
                    // Only a strict ISO date is accepted by <input type="date">; anything else is ignored.
                    if (/^\d{4}-\d{2}-\d{2}$/.test(data.deadline || '')) autofill('Deadline', data.deadline);

                    // If input was a URL, auto-fill the Job Link field with it
                    if (isUrl) {
                        autofill('JobLink', text);
                        // We don't have the full posting text for a URL — fall back to
                        // the extracted skills so the field isn't left blank.
                        if (data.skills && data.skills.length) {
                            autofill('JobDescription',
                                'Key Skills:\n' + data.skills.map(s => '• ' + s).join('\n'));
                        }
                    } else {
                        // User pasted the full job description — keep it verbatim,
                        // don't overwrite it with the extracted skills list.
                        autofill('JobDescription', text);
                    }

                    showResult(data);
                    setTimeout(() => {
                        document.getElementById('appForm')
                            .scrollIntoView({ behavior: 'smooth', block: 'start' });
                    }, 300);

                    // Auto-match against saved resume
                    runAutoMatch(text, token);
                } else {
                    showError(data.error || 'Analysis failed. Please try again.');
                }
            } catch (err) {
                rethrowIfBug(err);
                showError('Request failed. Check your connection and try again.');
            } finally {
                setLoading(false);
            }
        });

        // ── Helpers ──────────────────────────────────────────
        // Sets a form field's value and re-triggers the highlight animation
        // (removing then re-adding the class forces the CSS animation to restart).
        function autofill(id, value) {
            if (!value) return;
            const el = document.getElementById(id);
            if (!el) return;
            el.value = value;
            el.classList.remove('ai-autofilled');
            void el.offsetWidth; // force reflow to restart animation
            el.classList.add('ai-autofilled');
        }

        // Toggles the Analyze button's disabled/spinner state and shows a
        // shimmer placeholder in the result area while the request is in flight.
        function setLoading(on, isUrl) {
            analyzeBtn.disabled = on;
            if (on) {
                const label = isUrl ? 'Fetching page...' : 'Analyzing...';
                analyzeBtn.innerHTML =
                    '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> ' + label;
                resultEl.innerHTML =
                    '<div class="ai-shimmer-wrap">' +
                    '<div class="shimmer-bar w-three"></div>' +
                    '<div class="shimmer-bar w-full"></div>' +
                    '<div class="shimmer-bar w-half"></div>' +
                    '</div>';
                resultEl.hidden = false;
            } else {
                analyzeBtn.innerHTML = 'Analyze with AI';
                if (resultEl.querySelector('.ai-shimmer-wrap')) resultEl.hidden = true;
            }
        }

        // Renders the "form auto-filled" success banner plus the extracted skill tags.
        function showResult(data) {
            const filled = [data.companyName, data.roleTitle, data.location]
                .filter(Boolean).join(' · ');
            const skillsHtml = data.skills && data.skills.length
                ? `<div class="analyzer-skills">` +
                  data.skills.map(s => `<span class="analyzer-skill-tag">${s}</span>`).join('') +
                  `</div>`
                : '';

            resultEl.innerHTML =
                `<div class="analyzer-result-top">` +
                `<span class="analyzer-check"></span>` +
                `<span>Form auto-filled${filled ? ' — ' + filled : ''}</span>` +
                `</div>` + skillsHtml;
            resultEl.hidden = false;
        }

        function showError(msg) {
            errorEl.textContent = msg;
            errorEl.hidden = false;
        }

        // ── Auto-match against saved resume ──────────────
        // 5-tier resume match scoring system, ordered highest score first.
        // getTier() picks the first tier whose `min` the score meets or exceeds.
        // This same table is duplicated in Index.cshtml for the detail drawer —
        // kept in sync manually since these are separate Razor views with no
        // shared JS module to import from.
        const TIERS = [
            { min: 80, num: 5, label: 'APPLY',            desc: 'Strong fit — most required skills are present.' },
            { min: 60, num: 4, label: 'APPLY',            desc: 'Good fit — you have the core fundamentals for this role.' },
            { min: 40, num: 3, label: 'MAYBE',            desc: 'Moderate fit — some upskilling would strengthen your application.' },
            { min: 20, num: 2, label: 'CONSIDER SKIPPING',desc: 'Weak fit — several key requirements are missing from your profile.' },
            { min: 0,  num: 1, label: 'SKIP',             desc: 'Poor fit — the role requirements don\'t match your current profile.' },
        ];

        function getTier(sc) {
            return TIERS.find(t => sc >= t.min) || TIERS[TIERS.length - 1];
        }

        // Minimal HTML-escaping for values interpolated into innerHTML below.
        function esc(s) {
            return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
        }

        // Renders the resume match card markup (score, tier badge, summary,
        // matching/missing skill tags) from the AutoMatch response object `d`.
        function buildMatchCard(d) {
            const sc   = d.score;
            const tier = getTier(sc);
            const matchingSkills = d.matchingSkills || [];
            const missingSkills  = d.missingSkills  || [];

            const matchTags = matchingSkills.map(s => `<span class="skill-tag skill-tag--match">${esc(s)}</span>`).join('');
            const missTags  = missingSkills.map(s  => `<span class="skill-tag skill-tag--missing">${esc(s)}</span>`).join('');

            const skillsHtml = (matchTags || missTags) ? `
                <div class="match-skills-grid">
                    ${matchTags ? `<div class="match-skills-col">
                        <div class="match-skills-header match-skills-header--match">Matching (${matchingSkills.length})</div>
                        <div class="match-skills-tags">${matchTags}</div>
                    </div>` : ''}
                    ${missTags ? `<div class="match-skills-col">
                        <div class="match-skills-header match-skills-header--missing">Missing (${missingSkills.length})</div>
                        <div class="match-skills-tags">${missTags}</div>
                    </div>` : ''}
                </div>` : '';

            return `
                <div class="match-result-title">Resume match</div>
                <div class="match-verdict-row">
                    <span class="match-ring match-ring--tier-${tier.num}" style="--p:${Number(sc)}"><span>${sc}</span></span>
                    <span class="match-rec-badge match-tier-${tier.num}">${tier.label}</span>
                    <span class="match-verdict-desc">${tier.desc}</span>
                </div>
                ${d.summary ? `<p class="match-summary-text">${esc(d.summary)}</p>` : ''}
                ${skillsHtml}`;
        }

        // Calls /Profile/AutoMatch to score the just-analyzed job description
        // against the user's active resume, then populates both the visible
        // match card and the hidden form fields that carry the AI's results
        // (score, recommendation, summary, skill lists) through to Create POST.
        //
        // NOTE: the response is serialized by ASP.NET Core's default JSON
        // policy, which camelCases C# property names (HasResume -> hasResume,
        // MatchingSkills -> matchingSkills, etc). All property access below
        // must stay camelCase to match the actual wire format — a prior bug
        // here used PascalCase and silently read `undefined` for every field.
        async function runAutoMatch(jobDescription, token) {
            const wrap = document.getElementById('inlineMatchWrap');
            const card = document.getElementById('inlineMatchCard');

            // Show loading state
            card.innerHTML = '<p class="match-loading-text">Analyzing resume match…</p>';
            wrap.hidden = false;

            try {
                const resp = await fetch('/Profile/AutoMatch', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': token
                    },
                    body: JSON.stringify({ jobDescription })
                });
                const d = await resp.json();
                if (resp.status === 429 && d.error) showAppToast('error', d.error);
                console.log('AutoMatch response:', JSON.stringify(d));

                // No active resume on file — point the user to the Profile page
                // instead of silently hiding the match card.
                if (!d.hasResume) {
                    card.innerHTML = `
                        <div class="match-no-resume">
                            <p class="match-no-resume-title">Upload your resume to see a match score</p>
                            <p class="match-no-resume-text">
                                Go to your <a href="/Profile">Profile</a> and upload a resume to get AI-powered job match analysis.
                            </p>
                        </div>`;
                    return;
                }

                // AI call failed (rate limit, network, invalid key, etc).
                if (!d.success) {
                    card.innerHTML = `<p class="js-error-text">${esc(d.error || 'Resume match failed.')}</p>`;
                    return;
                }

                // Success: render the match card and stash the computed values
                // in hidden inputs so they get submitted along with the form
                // and persisted on the JobApplication record.
                card.innerHTML = buildMatchCard(d);
                document.getElementById('hiddenMatchScore').value = d.score;
                document.getElementById('hiddenMatchRecommendation').value = d.recommendation;
                document.getElementById('hiddenMatchSummary').value = d.summary || '';
                document.getElementById('hiddenMatchingSkillsJson').value = JSON.stringify(d.matchingSkills || []);
                document.getElementById('hiddenMissingSkillsJson').value = JSON.stringify(d.missingSkills || []);
            } catch (err) {
                rethrowIfBug(err);
                card.innerHTML = '<p class="js-error-text">Resume match request failed. Please try again.</p>';
            }
        }
    })();

    // ── Keyword coverage ─────────────────────────────────
    // Deliberately not tied to the analyzer: the check is free and instant, so requiring a paid AI analysis
    // before showing a free result would be backwards. It runs off the Job description field itself — after
    // the analyzer fills it in, and whenever the user finishes pasting or editing a posting by hand.
    (function () {
        const wrap    = document.getElementById('keywordCoverageWrap');
        const field   = document.querySelector('#appForm [name="JobDescription"]');
        if (!wrap || !field || !window.keywordCoverage) return;

        const root = wrap.querySelector('[data-keyword-coverage]');
        if (!root) return;

        // Below this there is nothing a keyword check could say; the server's own floor then decides
        // whether the posting is substantial enough to be worth a list.
        const MIN_CHARS = 200;
        // Sent with the posting so the employer's own name and city are filtered out of the terms, exactly as
        // they are once the application is saved. Without them the count changes on save, which reads as a bug.
        const company  = document.querySelector('#appForm [name="CompanyName"]');
        const role     = document.querySelector('#appForm [name="RoleTitle"]');
        const location = document.querySelector('#appForm [name="Location"]');

        let lastSent = null;
        let timer = null;

        function check() {
            const text = field.value.trim();
            if (text.length < MIN_CHARS) {
                wrap.hidden = true;
                lastSent = null;
                return;
            }

            const payload = {
                description: text,
                company: company?.value.trim() || '',
                role: role?.value.trim() || '',
                location: location?.value.trim() || ''
            };
            const key = JSON.stringify(payload);
            if (key === lastSent) return;      // nothing changed since the last look
            lastSent = key;
            wrap.hidden = false;
            window.keywordCoverage.load(root, payload);
        }

        // Blur catches the paste-then-click-away case; the debounced input handler catches the analyzer
        // filling the field in, which never fires a blur.
        field.addEventListener('blur', check);
        field.addEventListener('input', function () {
            clearTimeout(timer);
            timer = setTimeout(check, 800);
        });
        // Filling in the company or role after the posting changes which terms are the employer's own.
        [company, role, location].forEach(function (el) { el?.addEventListener('blur', check); });
    })();
})();
