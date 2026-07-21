// Exists only so the browser considers this installable as a PWA (issue: "add as a PWA
// to separate it from the browser"). This app is a live SSH/vault client talking to a
// per-launch local backend - there is nothing here that should ever be served from a
// cache instead of the network, so this deliberately does no caching at all.
self.addEventListener('install', () => {
  self.skipWaiting()
})

self.addEventListener('activate', (event) => {
  event.waitUntil(self.clients.claim())
})

self.addEventListener('fetch', (event) => {
  event.respondWith(fetch(event.request))
})
