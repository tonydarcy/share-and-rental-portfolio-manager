// Light or dark colours. Loaded in <head> so the page never flashes the wrong colours.
// The choice (light, dark or auto) is kept on this device; auto follows the device's own setting.
(function () {
    'use strict';
    var key = 'pm-theme';
    var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

    function stored() {
        try { var v = localStorage.getItem(key); return v === 'light' || v === 'dark' ? v : 'auto'; } catch (e) { return 'auto'; }
    }
    function resolved(choice) {
        return choice === 'auto' ? (media && media.matches ? 'dark' : 'light') : choice;
    }

    // Charts (Chart.js) draw text and grid lines in fixed colours, so give them the theme's colours
    function chartColours() {
        if (typeof Chart === 'undefined' || !document.body) return;
        var css = getComputedStyle(document.body);
        Chart.defaults.color = css.getPropertyValue('--bs-secondary-color').trim() || '#666';
        Chart.defaults.borderColor = css.getPropertyValue('--bs-border-color-translucent').trim() || 'rgba(0,0,0,.1)';
        Object.values(Chart.instances || {}).forEach(function (c) {
            c.data.datasets.forEach(function (d) { if (d.pmSliceBorder) d.borderColor = css.getPropertyValue('--bs-body-bg').trim(); });
            c.update('none');
        });
    }

    function markMenu(choice) {
        document.querySelectorAll('[data-pm-theme]').forEach(function (b) {
            var on = b.getAttribute('data-pm-theme') === choice;
            b.classList.toggle('active', on);
            b.setAttribute('aria-pressed', on ? 'true' : 'false');
        });
    }

    function apply(choice) {
        document.documentElement.setAttribute('data-bs-theme', resolved(choice));
        markMenu(choice);
        chartColours();
    }

    apply(stored());
    // Reports always print on white paper
    window.addEventListener('beforeprint', function () { document.documentElement.setAttribute('data-bs-theme', 'light'); chartColours(); });
    window.addEventListener('afterprint', function () { apply(stored()); });
    if (media) {
        var follow = function () { if (stored() === 'auto') apply('auto'); };
        if (media.addEventListener) media.addEventListener('change', follow); else if (media.addListener) media.addListener(follow);
    }

    document.addEventListener('DOMContentLoaded', function () {
        apply(stored());
        document.addEventListener('click', function (e) {
            var b = e.target.closest ? e.target.closest('[data-pm-theme]') : null;
            if (!b) return;
            e.preventDefault();
            var choice = b.getAttribute('data-pm-theme');
            try { if (choice === 'auto') localStorage.removeItem(key); else localStorage.setItem(key, choice); } catch (err) { }
            apply(choice);
        });
    });

    window.pmTheme = { chartColours: chartColours };
})();
