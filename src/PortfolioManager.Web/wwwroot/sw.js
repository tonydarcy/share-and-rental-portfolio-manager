// Lets the site be installed on a phone's home screen. Pages come from the network whenever there is one, so figures
// are never out of date. Only if the person switches on "Offline copies" for this device are the main read-only pages
// also saved as they're opened, to be shown (marked as a saved copy) when there's no connection. Switching it off, or
// signing out, deletes them.
const CACHE = 'pm-offline-v2';
const PAGES = 'pm-pages';
const OFFLINE = '/offline.html';
const SAVE = [/^\/$/, /^\/Holdings/i, /^\/Portfolio/i, /^\/Trades$/i, /^\/Payouts$/i, /^\/Cash$/i, /^\/Accounting\/Journals$/i,
    /^\/Accounting\/Journal$/i, /^\/Accounting\/Unallocated$/i, /^\/Accounting\/(TrialBalance|BalanceSheet|ProfitLoss)$/i,
    /^\/Reports\/(Performance|Consolidated|TaxableIncome|CapitalGains|UnrealisedGains)$/i, /^\/Property/i];
const MAX = 40;
let saving = false;

self.addEventListener('install', e => {
    e.waitUntil(caches.open(CACHE).then(c => c.add(OFFLINE)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', e => {
    e.waitUntil(caches.keys()
        .then(keys => Promise.all(keys.filter(k => k !== CACHE && k !== PAGES).map(k => caches.delete(k))))
        .then(() => self.clients.claim()));
});

self.addEventListener('message', e => {
    if (typeof e.data?.offline !== 'boolean') return;
    saving = e.data.offline;
    if (!saving) caches.delete(PAGES);
});

async function save(request, response) {
    const cache = await caches.open(PAGES);
    const body = await response.text();
    const stamp = new Date().toLocaleString();
    const marked = body.replace(/<body([^>]*)>/i, `<body$1><div class="offline-copy">You're offline. This is the copy saved on this device on ${stamp}; nothing can be changed until you're back online.</div>`);
    await cache.put(request.url, new Response(marked, { headers: { 'Content-Type': 'text/html; charset=utf-8' } }));
    const keys = await cache.keys();
    for (const old of keys.slice(0, Math.max(0, keys.length - MAX))) await cache.delete(old);
}

self.addEventListener('fetch', e => {
    if (e.request.mode !== 'navigate' || e.request.method !== 'GET') return;
    const path = new URL(e.request.url).pathname;
    e.respondWith((async () => {
        try {
            const response = await fetch(e.request);
            if (saving && response.ok && !response.redirected && SAVE.some(r => r.test(path))
                && (response.headers.get('Content-Type') || '').startsWith('text/html'))
                e.waitUntil(save(e.request, response.clone()));
            return response;
        } catch {
            return (await caches.open(PAGES).then(c => c.match(e.request.url))) || caches.match(OFFLINE);
        }
    })());
});
