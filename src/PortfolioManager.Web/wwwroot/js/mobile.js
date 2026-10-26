// Phone-friendly pages: wide tables scroll sideways inside their card instead of pushing the page wider,
// and the service worker lets the site be added to the home screen.
document.querySelectorAll('main table').forEach(t => {
    if (t.closest('.table-responsive') || t.classList.contains('table-stack')) return;
    const wrap = document.createElement('div');
    wrap.className = 'table-responsive';
    t.parentNode.insertBefore(wrap, t);
    wrap.appendChild(t);
});

// Stacked tables (.table-stack) label each value with its column name on a phone
document.querySelectorAll('table.table-stack').forEach(t => {
    const names = [];
    t.querySelectorAll('thead > tr:last-child > th').forEach(th => {
        for (let i = 0; i < (th.colSpan || 1); i++) names.push(th.textContent.trim());
    });
    t.querySelectorAll('tbody > tr, tfoot > tr').forEach(tr => {
        let col = 0;
        for (const td of tr.children) {
            if (names[col] && td.colSpan === 1 && !td.hasAttribute('data-label')) td.dataset.label = names[col];
            col += td.colSpan || 1;
        }
    });
});

// Offline copies: when switched on for this device, the main read-only pages are saved as you open them, so they can
// still be looked at with no connection. Switched off, or once signed out, the saved copies are deleted.
(() => {
    if (!('serviceWorker' in navigator)) return;
    const KEY = 'pm-offline-copies';
    const get = () => { try { return localStorage.getItem(KEY) === 'on'; } catch { return false; } };
    const set = on => { try { on ? localStorage.setItem(KEY, 'on') : localStorage.removeItem(KEY); } catch { } };
    const tell = msg => navigator.serviceWorker.ready.then(r => r.active?.postMessage(msg)).catch(() => { });
    const signedOut = /^\/Account\/(Login|Logout|Register)/i.test(location.pathname);
    if (signedOut) set(false);

    window.addEventListener('load', () => navigator.serviceWorker.register('/sw.js').catch(() => { }));
    tell({ offline: signedOut ? false : get() });

    const toggle = document.getElementById('offlineCopies');
    if (toggle) {
        const show = () => toggle.querySelector('[data-state]').textContent = get() ? 'on' : 'off';
        show();
        toggle.addEventListener('click', e => {
            e.preventDefault();
            set(!get());
            tell({ offline: get() });
            show();
        });
    }
    document.querySelectorAll('a[href*="Logout"], form[action*="Logout"]').forEach(el =>
        el.addEventListener(el.tagName === 'FORM' ? 'submit' : 'click', () => { set(false); tell({ offline: false }); }));
})();
