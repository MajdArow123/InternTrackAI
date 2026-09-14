// Shared chip/tag input used by every tag list in the app (profile skills, profile target
// roles). One place decides what "already in the list" means — trimmed, internal whitespace
// collapsed, compared case-insensitively — mirroring Services/ProfileTags.cs on the server.
//
// A duplicate is never added: the existing chip flashes, an inline "<Term> is already in
// your list" message appears under the input, and the typed text stays so it can be edited.
// The same rules apply whether the value arrives via the Add button, Enter, a comma, blur,
// or a paste of several comma/newline-separated values.
//
//   const api = TagInput.create({ cloud, hidden, input, addButton, values, onChange, onAfterAdd });
//   api.values() / api.add(text) / api.set(list) / api.highlight(list) / api.render()
(function (global) {
    'use strict';

    function normalize(value) {
        return String(value == null ? '' : value).trim().replace(/\s+/g, ' ');
    }

    function sameTag(a, b) {
        return normalize(a).toLowerCase() === normalize(b).toLowerCase();
    }

    // Index of the existing value that matches `value`, or -1.
    function findDuplicate(values, value) {
        const key = normalize(value).toLowerCase();
        if (!key) return -1;
        for (let i = 0; i < values.length; i++) {
            if (normalize(values[i]).toLowerCase() === key) return i;
        }
        return -1;
    }

    // "C#, c#, SQL" / one-per-line pastes become separate candidate values.
    function splitValues(text) {
        return String(text == null ? '' : text)
            .split(/[,\n\r;]+/)
            .map(normalize)
            .filter(v => v.length > 0);
    }

    function escHtml(s) {
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function flash(el, cls) {
        if (!el) return;
        el.classList.remove(cls);
        void el.offsetWidth;
        el.classList.add(cls);
        el.addEventListener('animationend', () => el.classList.remove(cls), { once: true });
    }

    function create(opts) {
        const cloud  = opts.cloud;
        const hidden = opts.hidden;
        const input  = opts.input;
        const addBtn = opts.addButton;
        let values   = Array.isArray(opts.values) ? opts.values.slice() : [];

        // Inline duplicate message lives right under the input row.
        const row = input.closest('.tag-input-row') || input.parentElement;
        let msgEl = null;

        function showMessage(text) {
            if (!msgEl) {
                msgEl = document.createElement('div');
                msgEl.className = 'field-error-msg tag-dupe-msg';
                msgEl.setAttribute('role', 'status');
                msgEl.setAttribute('aria-live', 'polite');
                row.insertAdjacentElement('afterend', msgEl);
            }
            msgEl.textContent = text;
            input.classList.add('field-invalid');
        }

        function clearMessage() {
            if (msgEl) { msgEl.remove(); msgEl = null; }
            input.classList.remove('field-invalid');
        }

        function chipAt(index) {
            return cloud.querySelector('.tag-item[data-index="' + index + '"]');
        }

        function render() {
            cloud.innerHTML = '';
            values.forEach((val, i) => {
                const tag = document.createElement('span');
                tag.className = 'tag-item';
                tag.dataset.index = String(i);
                tag.innerHTML = '<span class="tag-item-label">' + escHtml(val) + '</span>' +
                                '<button type="button" data-i="' + i + '" aria-label="Remove ' + escHtml(val) + '">&times;</button>';
                cloud.appendChild(tag);
            });
            if (hidden) hidden.value = JSON.stringify(values);
        }

        function persist() {
            render();
            if (opts.onChange) opts.onChange(values.slice());
        }

        // Adds every non-duplicate candidate; returns the ones that were already present.
        function addMany(candidates) {
            const duplicates = [];
            let added = false;
            candidates.forEach(candidate => {
                const value = normalize(candidate);
                if (!value) return;
                const existing = findDuplicate(values, value);
                if (existing >= 0) {
                    duplicates.push({ value, index: existing });
                    return;
                }
                values.push(value);
                added = true;
            });
            if (added) persist();
            return duplicates;
        }

        // The single add path. `text` defaults to the input's content; comma/newline-separated
        // text is treated as several values.
        function add(text) {
            const raw = text !== undefined ? text : input.value;
            const candidates = splitValues(raw);
            if (candidates.length === 0) { input.value = ''; return; }

            const duplicates = addMany(candidates);
            if (duplicates.length === 0) {
                clearMessage();
                input.value = '';
            } else {
                // Keep what wasn't added so it can be edited; flash the chips it collided with.
                const seen = new Set();
                const unique = duplicates.filter(d => { const k = d.value.toLowerCase(); if (seen.has(k)) return false; seen.add(k); return true; });
                input.value = unique.map(d => d.value).join(', ');
                unique.forEach(d => flash(chipAt(d.index), 'tag-item-dupe'));
                const names = unique.map(d => '“' + d.value + '”');
                const list  = names.length === 1 ? names[0] : names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1];
                showMessage(list + (names.length === 1 ? ' is' : ' are') + ' already in your list');
                if (typeof global.showAppToast === 'function' && opts.toastOnDuplicate) {
                    global.showAppToast('info', list + (names.length === 1 ? ' is' : ' are') + ' already in your list');
                }
            }
            if (document.activeElement !== input && !opts.keepFocus) input.focus();
            if (opts.onAfterAdd) opts.onAfterAdd(duplicates.length === 0);
        }

        function set(list) {
            values = Array.isArray(list) ? list.slice() : [];
            render();
        }

        // Briefly flash the chips whose labels are in `list` (e.g. what a resume auto-fill just added).
        function highlight(list) {
            const wanted = new Set((list || []).map(v => normalize(v).toLowerCase()));
            if (!wanted.size) return;
            cloud.querySelectorAll('.tag-item').forEach(tag => {
                const label = tag.querySelector('.tag-item-label');
                if (label && wanted.has(normalize(label.textContent).toLowerCase())) flash(tag, 'tag-item-new');
            });
        }

        cloud.addEventListener('click', e => {
            const btn = e.target.closest('button[data-i]');
            if (!btn) return;
            values.splice(parseInt(btn.dataset.i, 10), 1);
            clearMessage();
            persist();
        });

        if (addBtn) addBtn.addEventListener('click', () => add());

        input.addEventListener('keydown', e => {
            if (e.key === 'Enter' || e.key === ',') { e.preventDefault(); add(); }
        });
        input.addEventListener('input', () => {
            // A comma typed via IME/mobile keyboards may not arrive as a keydown: split on it.
            if (input.value.includes(',')) { add(); return; }
            if (msgEl) clearMessage();
        });
        input.addEventListener('paste', e => {
            const text = (e.clipboardData || global.clipboardData)?.getData('text') || '';
            if (!text.trim()) return;
            e.preventDefault();
            const before = input.value.slice(0, input.selectionStart ?? input.value.length);
            const after  = input.value.slice(input.selectionEnd ?? input.value.length);
            add(before + text + after);
        });
        input.addEventListener('blur', () => {
            if (opts.addOnBlur === false) return;
            if (input.value.trim()) { opts.keepFocus = true; add(); opts.keepFocus = false; }
        });

        render();
        return { values: () => values.slice(), add, set, highlight, render, clearMessage };
    }

    global.TagInput = { normalize, sameTag, findDuplicate, splitValues, create };
})(window);
