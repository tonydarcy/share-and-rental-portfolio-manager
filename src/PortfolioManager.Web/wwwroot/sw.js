// Lets the site be installed on a phone's home screen. Pages always come from the network, so figures are never
// out of date and nothing private is kept on the phone; only the "you're offline" page is stored.
const CACHE = 'pm-offline-v1';
const OFFLINE = '/offline.html';

self.addEventListener('install', e => {
    e.waitUntil(caches.open(CACHE).then(c => c.add(OFFLINE)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', e => {
    e.waitUntil(caches.keys()
        .then(keys => Promise.all(keys.filter(k => k !== CACHE).map(k => caches.delete(k))))
        .then(() => self.clients.claim()));
});

self.addEventListener('fetch', e => {
    if (e.request.mode !== 'navigate') return;
    e.respondWith(fetch(e.request).catch(() => caches.match(OFFLINE)));
});
