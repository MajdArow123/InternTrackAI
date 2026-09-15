// Dashboard "Skills you're missing most" card: horizontal Chart.js bars per missing skill, role filter pills,
// and the per-skill application list. Everything comes from <script type="application/json" id="skillGapData">
// (SkillGapService, rendered in Dashboard.cshtml) — filtering and drill-down never hit the server.
// The canvas is decorative; each bar row is covered by a real <button> (#skillGapHits) so the drill-down works
// with a keyboard and screen readers, and so clicks land anywhere on the row, label included.
(function () {
    var card   = document.getElementById('skillGapCard');
    var island = document.getElementById('skillGapData');
    if (!card || !island || typeof Chart === 'undefined') return;

    var data = JSON.parse(island.textContent || '{}');
    var buckets = {};
    (data.buckets || []).forEach(function (b) { buckets[b.key] = b; });
    var apps = data.applications || {};

    var ROW_H = 34, BAR_H = 18, MAX_LISTED = 8;
    var reduceMotion = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;

    var canvas    = document.getElementById('skillGapCanvas');
    var wrap      = document.getElementById('skillGapChart');
    var hitsHost  = document.getElementById('skillGapHits');
    var body      = card.querySelector('.skill-gap-body');
    var takeaway  = document.getElementById('skillGapTakeaway');
    var hint      = document.getElementById('skillGapHint');
    var panel     = document.getElementById('skillGapPanel');
    var panelTitle = document.getElementById('skillGapPanelTitle');
    var panelSub  = document.getElementById('skillGapPanelSub');
    var list      = document.getElementById('skillGapApps');
    var more      = document.getElementById('skillGapMore');
    var closeBtn  = document.getElementById('skillGapPanelClose');
    var baseUrl   = card.dataset.applicationsUrl || '/JobApplications';

    var current = buckets.all;
    var selected = -1;   // index into current.skills of the open drill-down, -1 when closed
    var chart;

    function cssVar(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

    function valueLabel(s) { return s.count + ' of ' + current.analyzed; }

    // Long skill names are cut to fit the axis on narrow cards; the hit button's aria-label keeps the full name.
    function tickText(name) {
        var max = wrap.clientWidth < 420 ? 15 : 26;
        return name.length > max ? name.slice(0, max - 1) + '…' : name;
    }

    // Draws "8 of 31" just past the end of each bar.
    var valueLabels = {
        id: 'skillGapValueLabels',
        afterDatasetsDraw: function (c) {
            var meta = c.getDatasetMeta(0), ctx = c.ctx;
            ctx.save();
            ctx.font = '500 12px ' + (cssVar('--font') || 'sans-serif');
            ctx.fillStyle = cssVar('--text-2') || '#6E6E73';
            ctx.textBaseline = 'middle';
            meta.data.forEach(function (bar, i) {
                var s = current.skills[i]; if (!s) return;
                ctx.fillText(valueLabel(s), bar.x + 8, bar.y);
            });
            ctx.restore();
        }
    };

    function build() {
        if (chart) { chart.destroy(); chart = null; }
        var skills = current.skills || [];
        body.hidden = skills.length === 0;
        if (skills.length === 0) { renderHits(); return; }

        // Bars keep one height: the container grows with the number of rows instead of the bars stretching.
        wrap.style.height = (skills.length * ROW_H + 6) + 'px';

        var rgb   = cssVar('--accent-rgb') || '10,132,255';
        var font  = cssVar('--font');
        var ctx   = canvas.getContext('2d');
        ctx.font  = '500 12px ' + (font || 'sans-serif');
        var labelRoom = Math.ceil(Math.max.apply(null, skills.map(function (s) { return ctx.measureText(valueLabel(s)).width; }))) + 14;

        chart = new Chart(ctx, {
            type: 'bar',
            data: {
                labels: skills.map(function (s) { return s.skill; }),
                datasets: [{
                    data: skills.map(function (s) { return s.count; }),
                    barThickness: BAR_H,
                    borderRadius: BAR_H / 2,
                    borderSkipped: false,
                    backgroundColor: function (c) {
                        var area = c.chart.chartArea;
                        var dim = selected >= 0 && c.dataIndex !== selected;
                        var from = dim ? 0.22 : 0.78, to = dim ? 0.32 : 1;
                        if (!area) return 'rgba(' + rgb + ',' + to + ')';
                        var g = c.chart.ctx.createLinearGradient(area.left, 0, area.right, 0);
                        g.addColorStop(0, 'rgba(' + rgb + ',' + from + ')');
                        g.addColorStop(1, 'rgba(' + rgb + ',' + to + ')');
                        return g;
                    }
                }]
            },
            options: {
                indexAxis: 'y',
                responsive: true,
                maintainAspectRatio: false,
                events: [],                       // interaction goes through the hit buttons layered on top
                animation: reduceMotion ? false : { duration: 450, easing: 'easeOutQuart' },
                layout: { padding: { right: labelRoom, top: 3, bottom: 3 } },
                plugins: { legend: { display: false }, tooltip: { enabled: false } },
                scales: {
                    x: { display: false, beginAtZero: true, max: Math.max.apply(null, skills.map(function (s) { return s.count; })), grid: { display: false } },
                    y: {
                        grid: { display: false },
                        border: { display: false },
                        ticks: {
                            color: cssVar('--text') || '#1D1D1F',
                            font: { family: font, size: 13, weight: '500' },
                            padding: 10,
                            autoSkip: false,
                            callback: function (v, i) { return tickText(skills[i].skill); }
                        }
                    }
                },
                onResize: function () { requestAnimationFrame(positionHits); }
            },
            plugins: [valueLabels]
        });

        // Chart.js reserves some axis padding above and below the rows; grow the container by exactly that much so
        // every row gets ROW_H.
        var extra = wrap.clientHeight - chart.chartArea.height;
        var wanted = Math.round(skills.length * ROW_H + extra);
        if (Math.abs(wrap.clientHeight - wanted) > 0.5) {
            wrap.style.height = wanted + 'px';
            chart.resize();
        }
        renderHits();
    }

    function renderHits() {
        hitsHost.innerHTML = '';
        (current.skills || []).forEach(function (s, i) {
            var item = document.createElement('div');
            item.setAttribute('role', 'listitem');
            item.className = 'skill-gap-hit-row';
            var btn = document.createElement('button');
            btn.type = 'button';
            btn.className = 'skill-gap-hit';
            btn.dataset.index = i;
            btn.dataset.skill = s.skill;
            btn.setAttribute('aria-controls', 'skillGapPanel');
            btn.setAttribute('aria-expanded', i === selected ? 'true' : 'false');
            btn.setAttribute('aria-label', s.skill + ': missing from ' + s.count + ' of ' + current.analyzed + ' analyzed application' + (current.analyzed === 1 ? '' : 's') + '. Show applications.');
            if (i === selected) btn.classList.add('is-selected');
            item.appendChild(btn);
            hitsHost.appendChild(item);
        });
        positionHits();
    }

    function positionHits() {
        if (!chart) return;
        var y = chart.scales.y;
        var pitch = chart.chartArea.height / Math.max(1, current.skills.length);
        hitsHost.querySelectorAll('.skill-gap-hit-row').forEach(function (row, i) {
            row.style.top = (y.getPixelForTick(i) - pitch / 2) + 'px';
            row.style.height = pitch + 'px';
        });
    }

    function open(index, focusPanel) {
        var s = current.skills[index]; if (!s) return;
        selected = index;
        panelTitle.textContent = s.skill;
        panelSub.textContent = 'Missing from ' + s.count + ' of ' + current.analyzed + ' analyzed application' + (current.analyzed === 1 ? '' : 's') + ' (' + s.percent + '%)';
        list.innerHTML = '';
        s.applicationIds.slice(0, MAX_LISTED).forEach(function (id) {
            var app = apps[id]; if (!app) return;
            var li = document.createElement('li');
            var a = document.createElement('a');
            a.href = baseUrl + '#open-' + id;
            a.className = 'skill-gap-app';
            a.dataset.appId = id;
            var company = document.createElement('span'); company.className = 'skill-gap-app-company'; company.textContent = app.company;
            var role = document.createElement('span'); role.className = 'skill-gap-app-role'; role.textContent = app.role;
            a.appendChild(company); a.appendChild(role);
            li.appendChild(a);
            list.appendChild(li);
        });
        var extra = s.applicationIds.length - MAX_LISTED;
        more.hidden = extra <= 0;
        more.textContent = extra > 0 ? '+' + extra + ' more' : '';
        panel.hidden = false;
        hint.hidden = true;
        refreshSelection();
        if (focusPanel) closeBtn.focus();
    }

    function close(returnFocus) {
        var was = selected;
        selected = -1;
        panel.hidden = true;
        hint.hidden = false;
        refreshSelection();
        if (returnFocus && was >= 0) {
            var btn = hitsHost.querySelector('.skill-gap-hit[data-index="' + was + '"]');
            if (btn) btn.focus();
        }
    }

    function refreshSelection() {
        hitsHost.querySelectorAll('.skill-gap-hit').forEach(function (btn) {
            var on = +btn.dataset.index === selected;
            btn.classList.toggle('is-selected', on);
            btn.setAttribute('aria-expanded', on ? 'true' : 'false');
        });
        if (chart) chart.update(reduceMotion ? 'none' : undefined);
    }

    hitsHost.addEventListener('click', function (e) {
        var btn = e.target.closest('.skill-gap-hit'); if (!btn) return;
        var i = +btn.dataset.index;
        if (i === selected) close(false); else open(i, false);
    });

    // Arrow keys move between bars; Home/End jump to the ends.
    hitsHost.addEventListener('keydown', function (e) {
        var btn = e.target.closest('.skill-gap-hit'); if (!btn) return;
        var all = Array.prototype.slice.call(hitsHost.querySelectorAll('.skill-gap-hit'));
        var i = all.indexOf(btn), next = -1;
        if (e.key === 'ArrowDown') next = Math.min(all.length - 1, i + 1);
        else if (e.key === 'ArrowUp') next = Math.max(0, i - 1);
        else if (e.key === 'Home') next = 0;
        else if (e.key === 'End') next = all.length - 1;
        if (next < 0) return;
        e.preventDefault();
        all[next].focus();
    });

    closeBtn.addEventListener('click', function () { close(true); });
    // Escape closes the list only while focus is on a bar or inside the list (not, say, when dismissing the info tooltip).
    card.addEventListener('keydown', function (e) {
        if (e.key !== 'Escape' || selected < 0) return;
        if (!hitsHost.contains(e.target) && !panel.contains(e.target)) return;
        e.preventDefault();
        close(true);
    });

    var roles = document.getElementById('skillGapRoles');
    if (roles) {
        roles.addEventListener('click', function (e) {
            var pill = e.target.closest('[data-bucket]'); if (!pill || !buckets[pill.dataset.bucket]) return;
            roles.querySelectorAll('[data-bucket]').forEach(function (p) {
                var on = p === pill;
                p.classList.toggle('active', on);
                p.setAttribute('aria-pressed', on ? 'true' : 'false');
            });
            current = buckets[pill.dataset.bucket];
            selected = -1;
            panel.hidden = true;
            hint.hidden = false;
            takeaway.textContent = current.takeaway;
            build();
        });
    }

    var info = document.getElementById('skillGapInfo');
    if (info && window.bootstrap && bootstrap.Tooltip) new bootstrap.Tooltip(info);

    build();
    new MutationObserver(function (m) {
        if (m.some(function (r) { return r.attributeName === 'data-theme'; })) build();
    }).observe(document.documentElement, { attributes: true });
})();
