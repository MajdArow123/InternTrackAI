    // Salary insight: estimates a typical pay range for the company/role/location currently
    // entered in the form, without requiring the form to be saved first.
    (function () {
        if (!document.getElementById('salaryInsightBtn')) return;
        const btn      = document.getElementById('salaryInsightBtn');
        const card     = document.getElementById('salaryInsightCard');
        const errorEl  = document.getElementById('salaryInsightError');
        const token    = document.querySelector('[name="__RequestVerificationToken"]').value;

        btn.addEventListener('click', async function () {
            const company  = document.getElementById('CompanyName').value.trim();
            const role     = document.getElementById('RoleTitle').value.trim();
            const location = document.getElementById('Location').value.trim();
            const workMode = document.getElementById('WorkMode').value;

            errorEl.hidden = true;
            if (!company || !role) {
                errorEl.textContent = 'Enter a company and role first.';
                errorEl.hidden = false;
                return;
            }

            const originalText = btn.textContent;
            btn.disabled = true;
            btn.textContent = 'Estimating...';
            card.hidden = true;

            try {
                const resp = await fetch('/SalaryInsight/Estimate', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                        'RequestVerificationToken': token
                    },
                    body: JSON.stringify({ role, company, location, workMode })
                });
                const data = await resp.json();
                if (resp.status === 429 && data.error) showAppToast('error', data.error);

                if (!data.success) {
                    errorEl.textContent = data.error || 'Estimate failed. Please try again.';
                    errorEl.hidden = false;
                    return;
                }

                card.innerHTML =
                    `<span class="salary-insight-range">${esc(data.range)}</span>` +
                    `<span class="salary-insight-note">${esc(data.note)}</span>`;
                card.hidden = false;
            } catch (err) {
                rethrowIfBug(err);
                errorEl.textContent = 'Request failed. Check your connection and try again.';
                errorEl.hidden = false;
            } finally {
                btn.disabled = false;
                btn.textContent = originalText;
            }
        });

        function esc(s) {
            return String(s || '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
        }
    })();
