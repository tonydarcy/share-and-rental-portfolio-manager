// The top menu: the phone's bottom bar and side panel, marking the page you're on, and the Go to search
// (pages and portfolios). The links come from the side panel, which is on every page even on a computer.
(() => {
    const panel = document.getElementById('pmMenu');
    if (!panel || !window.bootstrap) return;

    const here = location.pathname.replace(/\/+$/, '').toLowerCase() || '/';
    const pathOf = a => { try { return new URL(a.href, location.href).pathname.replace(/\/+$/, '').toLowerCase() || '/'; } catch { return ''; } };

    // Mark the page you're on, and open its section in the side panel
    let hereSection = null;
    document.querySelectorAll('[data-pm-link]').forEach(a => {
        if (pathOf(a) !== here) return;
        a.classList.add('active');
        a.setAttribute('aria-current', 'page');
        const section = a.closest('[data-pm-section]');
        if (section && !hereSection) hereSection = section.dataset.pmSection;
        const host = a.closest('.pm-mega-host');
        if (host) host.querySelector('.nav-link')?.classList.add('active');
    });

    const openSection = key => {
        panel.querySelectorAll('.accordion-collapse.show').forEach(c => {
            if (c.id !== 'pmSec-' + key) bootstrap.Collapse.getOrCreateInstance(c, { toggle: false }).hide();
        });
        const target = key && document.getElementById('pmSec-' + key);
        if (target) bootstrap.Collapse.getOrCreateInstance(target, { toggle: false }).show();
    };

    document.querySelectorAll('[data-pm-open]').forEach(btn => btn.addEventListener('click', () => {
        const key = btn.dataset.pmOpen || hereSection || '';
        openSection(key);
        bootstrap.Offcanvas.getOrCreateInstance(panel).show();
        if (btn.dataset.pmFocus === 'search')
            panel.addEventListener('shown.bs.offcanvas', () => document.getElementById('pmGoToPanel')?.focus(), { once: true });
    }));

    // Go to: every page in the menu (including ones hidden until used) and every other portfolio
    const seen = new Set();
    const entries = [];
    panel.querySelectorAll('[data-pm-go]').forEach(a => {
        const key = a.dataset.pmPortfolio ? 'p' + a.dataset.pmPortfolio : a.getAttribute('href');
        if (seen.has(key)) return;
        seen.add(key);
        entries.push({
            label: a.textContent.trim(),
            where: a.dataset.pmWhere || '',
            text: a.dataset.pmGo.toLowerCase(),
            href: a.getAttribute('href'),
            portfolio: a.dataset.pmPortfolio || null,
        });
    });

    const find = query => {
        const words = query.toLowerCase().split(/\s+/).filter(Boolean);
        if (!words.length) return [];
        return entries
            .filter(e => words.every(w => e.text.includes(w)))
            .map(e => ({ e, score: (e.label.toLowerCase().startsWith(words[0]) ? 0 : 2) + (e.portfolio ? 1 : 0) }))
            .sort((a, b) => a.score - b.score)
            .slice(0, 10)
            .map(x => x.e);
    };

    const go = entry => {
        if (entry.portfolio) {
            const form = document.getElementById('pmPickPortfolio');
            form.elements.portfolioId.value = entry.portfolio;
            form.submit();
        } else {
            location.href = entry.href;
        }
    };

    document.querySelectorAll('.pm-goto-input').forEach(input => {
        const list = document.getElementById(input.getAttribute('aria-controls'));
        let results = [];
        let current = 0;
        const draw = () => {
            list.replaceChildren(...results.map((e, i) => {
                const item = document.createElement('a');
                item.href = e.href || '#';
                item.className = 'list-group-item list-group-item-action py-2' + (i === current ? ' active' : '');
                item.setAttribute('role', 'option');
                const label = document.createElement('div');
                label.textContent = e.label;
                const where = document.createElement('div');
                where.className = 'small opacity-75';
                where.textContent = e.where;
                item.append(label, where);
                item.addEventListener('mousedown', ev => { ev.preventDefault(); go(e); });
                return item;
            }));
            if (!results.length && input.value.trim()) {
                const none = document.createElement('div');
                none.className = 'list-group-item small text-body-secondary';
                none.textContent = 'Nothing found';
                list.append(none);
            }
            list.hidden = !input.value.trim();
        };
        input.addEventListener('input', () => { results = find(input.value); current = 0; draw(); });
        input.addEventListener('keydown', ev => {
            if (ev.key === 'ArrowDown' && results.length) { current = (current + 1) % results.length; draw(); ev.preventDefault(); }
            else if (ev.key === 'ArrowUp' && results.length) { current = (current - 1 + results.length) % results.length; draw(); ev.preventDefault(); }
            else if (ev.key === 'Enter' && results[current]) { go(results[current]); ev.preventDefault(); }
            else if (ev.key === 'Escape') { input.value = ''; results = []; draw(); input.blur(); }
        });
        input.addEventListener('blur', () => setTimeout(() => { list.hidden = true; }, 150));
        input.addEventListener('focus', () => { if (input.value.trim()) draw(); });
    });

    // Press / anywhere (outside a box you're typing in) to search on a computer
    document.addEventListener('keydown', ev => {
        if (ev.key !== '/' || ev.ctrlKey || ev.metaKey || ev.altKey) return;
        const t = ev.target;
        if (t.isContentEditable || /^(input|textarea|select)$/i.test(t.tagName)) return;
        const top = document.getElementById('pmGoToTop');
        if (top && top.offsetParent !== null) { top.focus(); ev.preventDefault(); }
    });
})();
