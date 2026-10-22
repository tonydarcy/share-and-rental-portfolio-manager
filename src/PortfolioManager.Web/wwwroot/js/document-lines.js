// The line editor shared by invoices, bills, quotes and purchase orders (Pages/Shared/_DocumentLines.cshtml).
// Works out the same totals as the server: quantity x price, then the tax in it or on top of it.
(() => {
    const table = document.querySelector('table.document-lines');
    if (!table)
        return;
    const body = table.querySelector('tbody');
    const card = table.closest('.card');
    const includes = document.getElementById('includesTax');
    const currency = document.getElementById('currencyCode');
    const code = () => (currency?.value || '').trim().toUpperCase() || window.pmCurrency;
    const money = v => {
        try { return (Math.round(v * 100) / 100).toLocaleString(window.pmLocale, { style: 'currency', currency: code() }); }
        catch { return (Math.round(v * 100) / 100).toFixed(2); }
    };
    const round = v => Math.sign(v) * Math.round(Math.abs(v) * 100) / 100;
    const rows = () => body.querySelectorAll('tr.document-line');

    function renumber() {
        rows().forEach((row, i) => row.querySelectorAll('[name]').forEach(el => {
            el.name = el.name.replace(/Lines\[\d+\]/, `Lines[${i}]`);
            el.id = el.name.replace(/[\[\].]/g, '_');
        }));
    }

    function totals() {
        let net = 0, tax = 0, total = 0;
        rows().forEach(row => {
            const amount = round((parseFloat(row.querySelector('.qty').value) || 0) * (parseFloat(row.querySelector('.price').value) || 0));
            const rate = parseFloat(row.querySelector('.tax-code')?.selectedOptions[0]?.dataset.rate) || 0;
            const t = rate > 0 ? (includes?.checked ? round(amount * rate / (100 + rate)) : round(amount * rate / 100)) : 0;
            const lineNet = includes?.checked ? amount - t : amount;
            const lineTotal = includes?.checked ? amount : amount + t;
            net += lineNet; tax += t; total += lineTotal;
            row.querySelector('.amount').textContent = amount ? money(lineTotal) : '';
        });
        card.querySelector('[data-net]').textContent = money(net);
        const taxCell = card.querySelector('[data-tax]');
        if (taxCell) taxCell.textContent = money(tax);
        card.querySelector('[data-total]').textContent = money(total);
    }

    // Picking an item fills in its description, price, account and tax code
    body.addEventListener('change', e => {
        const select = e.target.closest('select.item');
        if (!select || !select.value)
            return;
        const o = select.selectedOptions[0].dataset;
        const row = select.closest('tr');
        const description = row.querySelector('.description');
        if (!description.value) description.value = o.name || '';
        if (o.price) row.querySelector('.price').value = o.price;
        if (!row.querySelector('.qty').value) row.querySelector('.qty').value = '1';
        if (o.account) row.querySelector('.account').value = o.account;
        const taxCode = row.querySelector('.tax-code');
        if (taxCode && o.tax) taxCode.value = o.tax;
    });

    card.querySelector('[data-add-line]').addEventListener('click', () => {
        const all = rows();
        const last = all[all.length - 1];
        const row = last.cloneNode(true);
        row.querySelectorAll('input').forEach(el => el.value = '');
        row.querySelector('.qty').value = '1';
        const selects = last.querySelectorAll('select');
        row.querySelectorAll('select').forEach((el, i) => el.value = el.classList.contains('item') ? '' : selects[i].value);
        body.appendChild(row);
        renumber();
        totals();
        row.querySelector('input.description').focus();
    });

    body.addEventListener('click', e => {
        const button = e.target.closest('.remove-line');
        if (!button)
            return;
        const row = button.closest('tr');
        if (rows().length > 1)
            row.remove();
        else
            row.querySelectorAll('input').forEach(el => el.value = '');
        renumber();
        totals();
    });

    body.addEventListener('input', totals);
    body.addEventListener('change', totals);
    includes?.addEventListener('change', totals);
    currency?.addEventListener('input', totals);
    totals();
})();
