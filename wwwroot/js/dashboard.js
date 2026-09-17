// Dashboard behaviour: the applications-over-time Chart.js line chart (rebuilt on theme
// change), the dismissible onboarding banner, and the stat-number count-up animation.
// Series data is rendered server-side into <script type="application/json" id="appsOverTimeData">.
(function () {
    var canvas = document.getElementById('appsOverTimeChart');
    if (!canvas || typeof Chart === 'undefined') return;

    var series = JSON.parse(document.getElementById('appsOverTimeData')?.textContent || '[]');
    var labels = series.map(function (p) { return p.label; });
    var values = series.map(function (p) { return p.value; });
    var maxVal = Math.max.apply(null, values.concat([0]));
    var chart;

    function cssVar(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }
    function isDark() { return document.documentElement.getAttribute('data-theme') === 'dark'; }

    function build() {
        if (chart) chart.destroy();
        var accent = cssVar('--accent') || '#0A84FF';
        var muted  = cssVar('--muted')  || '#6E6E73';
        var grid   = isDark() ? 'rgba(255,255,255,.07)' : 'rgba(0,0,0,.06)';
        var card   = cssVar('--card') || '#fff';
        var ctx    = canvas.getContext('2d');

        chart = new Chart(ctx, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    label: 'Applications',
                    data: values,
                    borderColor: accent,
                    borderWidth: 2.5,
                    tension: 0.4,
                    cubicInterpolationMode: 'monotone',
                    fill: true,
                    backgroundColor: function (c) {
                        var area = c.chart.chartArea; if (!area) return 'rgba(10,132,255,.08)';
                        var g = c.chart.ctx.createLinearGradient(0, area.top, 0, area.bottom);
                        g.addColorStop(0, 'rgba(10,132,255,.22)'); g.addColorStop(1, 'rgba(10,132,255,0)');
                        return g;
                    },
                    pointRadius: 3.5,
                    pointHoverRadius: 6,
                    pointBackgroundColor: card,
                    pointBorderColor: accent,
                    pointBorderWidth: 2,
                    pointHoverBackgroundColor: accent
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                animation: { duration: 500, easing: 'easeOutQuart' },
                interaction: { mode: 'index', intersect: false },
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        backgroundColor: isDark() ? '#2C2C2E' : '#1D1D1F',
                        titleColor: '#F5F5F7', bodyColor: '#F5F5F7',
                        padding: 10, cornerRadius: 10, displayColors: false,
                        callbacks: { label: function (i) { return i.parsed.y + (i.parsed.y === 1 ? ' application' : ' applications'); } }
                    }
                },
                scales: {
                    x: {
                        grid: { display: false, drawBorder: false },
                        border: { display: false },
                        ticks: { color: muted, font: { family: cssVar('--font'), size: 12 }, padding: 8 }
                    },
                    y: {
                        beginAtZero: true,
                        suggestedMax: Math.max(4, maxVal + 1),
                        grid: { color: grid, drawBorder: false, tickLength: 0 },
                        border: { display: false, dash: [4, 4] },
                        ticks: { color: muted, precision: 0, stepSize: 1, font: { family: cssVar('--font'), size: 12 }, padding: 10, maxTicksLimit: 5 }
                    }
                }
            }
        });
    }

    build();
    new MutationObserver(function (m) {
        if (m.some(function (r) { return r.attributeName === 'data-theme'; })) build();
    }).observe(document.documentElement, { attributes: true });
})();

(function () {
    var banner = document.getElementById('onboardingBanner');
    if (!banner) return;
    // Storage throws in private mode / with site data blocked. An unreadable flag
    // counts as "not dismissed" — the banner shows, and dismissing it still works
    // for this page view even if the choice can't be stored.
    var dismissed = null;
    try { dismissed = localStorage.getItem('onboardingDismissed'); } catch (e) { /* not fatal */ }
    if (dismissed === '1') {
        banner.style.display = 'none';
        return;
    }
    document.getElementById('onboardingClose').addEventListener('click', function () {
        try { localStorage.setItem('onboardingDismissed', '1'); } catch (e) { /* not fatal */ }
        banner.style.display = 'none';
    });
})();

(function () {
    function easeOutQuart(t) { return 1 - Math.pow(1 - t, 4); }

    document.querySelectorAll('.stat-number[data-count]').forEach(function (el) {
        var target = parseInt(el.dataset.count, 10);
        if (isNaN(target) || target === 0) return;
        var start = null;
        var duration = 850;
        function step(ts) {
            if (!start) start = ts;
            var p = Math.min((ts - start) / duration, 1);
            el.textContent = Math.round(easeOutQuart(p) * target);
            if (p < 1) requestAnimationFrame(step);
        }
        requestAnimationFrame(step);
    });
})();

// Resume performance: "Fewer than 5 applications" tooltip on greyed rows.
document.querySelectorAll('#resumePerformanceTable [data-bs-toggle="tooltip"]').forEach(function (el) {
    if (window.bootstrap && bootstrap.Tooltip) new bootstrap.Tooltip(el);
});
