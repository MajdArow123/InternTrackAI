// Kanban board: match-ring tooltips, column count/empty-state upkeep, and deep links to a
// column (#status-<name>). Drag-and-drop and keyboard moves are layered on in the same file.
// The detail drawer comes from app-drawer.js (cards carry the same data-* contract as list rows).
(function () {
    const board = document.getElementById('board');
    if (!board) return;

    document.querySelectorAll('.match-ring[data-bs-toggle="tooltip"]').forEach(function (el) {
        new bootstrap.Tooltip(el);
    });

    // Keep each column's count chip and "No applications" placeholder in sync with its cards.
    function refreshColumn(body) {
        const count = body.querySelectorAll('.board-card').length;
        body.classList.toggle('is-empty', count === 0);
        const chip = body.closest('.board-column')?.querySelector('[data-count-for]');
        if (chip) chip.textContent = String(count);
    }
    board.querySelectorAll('.board-column-body').forEach(refreshColumn);

    // Dashboard funnel rows link here with #status-<name>: bring that column into view.
    if (location.hash && location.hash.startsWith('#status-')) {
        const col = document.getElementById(location.hash.slice(1));
        if (col) {
            col.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'center' });
            col.classList.add('is-highlighted');
            setTimeout(() => col.classList.remove('is-highlighted'), 1600);
        }
    }

})();
