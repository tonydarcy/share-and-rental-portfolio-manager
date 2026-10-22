// Phone-friendly pages: wide tables scroll sideways inside their card instead of pushing the page wider,
// and the service worker lets the site be added to the home screen.
document.querySelectorAll('main table').forEach(t => {
    if (t.closest('.table-responsive')) return;
    const wrap = document.createElement('div');
    wrap.className = 'table-responsive';
    t.parentNode.insertBefore(wrap, t);
    wrap.appendChild(t);
});
if ('serviceWorker' in navigator) {
    window.addEventListener('load', () => navigator.serviceWorker.register('/sw.js').catch(() => { }));
}
