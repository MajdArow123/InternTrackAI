// Cover letter page: generate / improve via AJAX, word count, copy, jsPDF download, save-form
// sync, and loading a saved letter into the editor. Loaded from Views/CoverLetter/Generate.cshtml.
    (function () {

        const generateBtn  = document.getElementById('generateBtn');
        const appSelect    = document.getElementById('appSelect');
        const extraNotes   = document.getElementById('extraNotes');
        const errorBanner  = document.getElementById('generateError');
        const outputSection = document.getElementById('outputSection');
        const outputTA     = document.getElementById('coverLetterOutput');
        const wordDisplay  = document.getElementById('wordCountDisplay');
        const outputMeta   = document.getElementById('outputMeta');
        const copyBtn      = document.getElementById('copyBtn');
        const copyConfirm  = document.getElementById('copyConfirm');
        const downloadBtn  = document.getElementById('downloadPdfBtn');
        const saveForm     = document.getElementById('saveForm');
        const token        = document.querySelector('[name="__RequestVerificationToken"]').value;

        // Current generation metadata
        let currentCompany = '';
        let currentRole    = '';
        let currentAppId   = null;

        // ── Word count ────────────────────────────────────────
        function updateWordCount() {
            const words = outputTA.value.trim()
                ? outputTA.value.trim().split(/\s+/).length : 0;
            wordDisplay.textContent = words + ' words';
        }
        outputTA.addEventListener('input', updateWordCount);

        // ── Generate ──────────────────────────────────────────
        generateBtn.addEventListener('click', async function () {
            errorBanner.hidden = true;
            setLoading(true);

            const appId = appSelect.value ? parseInt(appSelect.value, 10) : null;

            try {
                const resp = await fetch('/CoverLetter/GenerateAjax', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': token
                    },
                    body: JSON.stringify({
                        applicationId: appId,
                        extraNotes: extraNotes.value.trim()
                    })
                });

                const data = await resp.json();
                if (resp.status === 429 && data.error) showAppToast('error', data.error);

                if (!data.success) {
                    showError(data.error || 'Generation failed. Please try again.');
                    return;
                }

                // Show output
                currentCompany = data.company || '';
                currentRole    = data.role    || '';
                currentAppId   = data.applicationId || null;

                outputTA.value = data.content;
                updateWordCount();

                const metaParts = [currentCompany, currentRole].filter(Boolean);
                outputMeta.textContent = metaParts.length
                    ? 'For: ' + metaParts.join(' — ')
                    : 'No application linked';

                // Pre-fill save form hidden inputs
                document.getElementById('saveAppId').value  = currentAppId ?? '';
                document.getElementById('saveCompany').value = currentCompany;
                document.getElementById('saveRole').value    = currentRole;

                outputSection.hidden = false;
                outputSection.scrollIntoView({ behavior: 'smooth', block: 'start' });

            } catch (err) {
                rethrowIfBug(err);
                showError('Request failed. Check your connection and try again.');
            } finally {
                setLoading(false);
            }
        });

        // ── Improve with AI ───────────────────────────────────
        const improveBtn   = document.getElementById('improveBtn');
        const improveInput = document.getElementById('improveInstructions');
        const improveError = document.getElementById('improveError');

        improveBtn.addEventListener('click', async function () {
            improveError.hidden = true;

            const content = outputTA.value.trim();
            if (!content) {
                improveError.textContent = 'Nothing to improve yet — generate or load a letter first.';
                improveError.hidden = false;
                return;
            }

            setImproveLoading(true);

            try {
                const resp = await fetch('/CoverLetter/ImproveAjax', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': token
                    },
                    body: JSON.stringify({
                        content: content,
                        applicationId: currentAppId,
                        company: currentCompany,
                        role: currentRole,
                        instructions: improveInput.value.trim()
                    })
                });

                const data = await resp.json();
                if (resp.status === 429 && data.error) showAppToast('error', data.error);

                if (!data.success) {
                    improveError.textContent = data.error || 'Improvement failed. Please try again.';
                    improveError.hidden = false;
                    return;
                }

                outputTA.value = data.content;
                updateWordCount();
                improveInput.value = '';

            } catch (err) {
                rethrowIfBug(err);
                improveError.textContent = 'Request failed. Check your connection and try again.';
                improveError.hidden = false;
            } finally {
                setImproveLoading(false);
            }
        });

        function setImproveLoading(on) {
            improveBtn.disabled = on;
            improveBtn.innerHTML = on
                ? '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> Improving...'
                : 'Improve letter';
        }

        // ── Sync save content before submit ───────────────────
        saveForm.addEventListener('submit', function () {
            document.getElementById('saveContent').value = outputTA.value;
        });

        // ── Copy ─────────────────────────────────────────────
        copyBtn.addEventListener('click', async function () {
            try {
                await navigator.clipboard.writeText(outputTA.value);
                copyConfirm.hidden = false;
                setTimeout(() => { copyConfirm.hidden = true; }, 2500);
            } catch (err) {
                rethrowIfBug(err);
                outputTA.select();
                document.execCommand('copy');
            }
        });

        // ── Download as PDF ───────────────────────────────────
        downloadBtn.addEventListener('click', function () {
            generatePdf(outputTA.value, buildFilename());
        });

        function buildFilename() {
            const parts = [currentCompany, currentRole].filter(Boolean);
            return (parts.length ? parts.join('_') : 'cover_letter')
                .replace(/[^a-z0-9_\- ]/gi, '')
                .replace(/\s+/g, '_')
                .toLowerCase() + '.pdf';
        }

        function generatePdf(text, filename) {
            const { jsPDF } = window.jspdf;
            const doc = new jsPDF({ unit: 'pt', format: 'letter' });

            const marginL  = 72;
            const marginT  = 72;
            const marginB  = 72;
            const maxW     = doc.internal.pageSize.getWidth() - marginL * 2;
            const pageH    = doc.internal.pageSize.getHeight();
            const lineH    = 16;
            const paraGap  = 10;

            doc.setFont('Helvetica', 'normal');
            doc.setFontSize(11);

            let y = marginT;

            const lines = text.split('\n');
            for (const line of lines) {
                if (line.trim() === '') {
                    y += paraGap;
                    continue;
                }
                const wrapped = doc.splitTextToSize(line, maxW);
                if (y + wrapped.length * lineH > pageH - marginB) {
                    doc.addPage();
                    y = marginT;
                }
                doc.text(wrapped, marginL, y);
                y += wrapped.length * lineH;
            }

            doc.save(filename);
        }

        // ── Load saved letter ─────────────────────────────────
        function loadLetter(id) {
            const ta = document.getElementById('cl-content-' + id);
            if (!ta) return;

            outputTA.value = ta.value;
            updateWordCount();

            currentCompany = '';
            currentRole    = '';
            currentAppId   = null;
            outputMeta.textContent = 'Loaded from saved version v' + id;

            document.getElementById('saveAppId').value   = '';
            document.getElementById('saveCompany').value = '';
            document.getElementById('saveRole').value    = '';

            outputSection.hidden = false;
            outputSection.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }

        document.querySelectorAll('[data-load-letter]').forEach(function (btn) {
            btn.addEventListener('click', function () { loadLetter(btn.dataset.loadLetter); });
        });

        // ── Helpers ───────────────────────────────────────────
        function setLoading(on) {
            generateBtn.disabled = on;
            generateBtn.innerHTML = on
                ? '<span class="spinner-border spinner-border-sm" style="width:14px;height:14px;border-width:2px"></span> Generating...'
                : 'Generate cover letter';
        }

        function showError(msg) {
            errorBanner.textContent = msg;
            errorBanner.hidden = false;
        }

    })();
