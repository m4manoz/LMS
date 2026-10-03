const CACHE_NAME = 'learning-platform-shell-v1'
const APP_SHELL = ['/', '/index.html', '/manifest.webmanifest']

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE_NAME).then(cache => cache.addAll(APP_SHELL)))
  self.skipWaiting()
})

self.addEventListener('activate', event => {
  event.waitUntil(
    caches.keys().then(keys => Promise.all(keys.filter(key => key !== CACHE_NAME).map(key => caches.delete(key))))
  )
  self.clients.claim()
})

self.addEventListener('fetch', event => {
  const request = event.request
  const url = new URL(request.url)
  const offlineAsset = url.searchParams.get('offline') === '1'
  if (request.method !== 'GET' || url.origin !== self.location.origin) return
  if (offlineAsset) {
    event.respondWith(caches.match(request).then(cached => cached || fetch(request)))
    return
  }
  if (request.url.includes('/api/')) return
  event.respondWith(
    fetch(request)
      .then(response => {
        const copy = response.clone()
        void caches.open(CACHE_NAME).then(cache => cache.put(request, copy))
        return response
      })
      .catch(() => caches.match(request).then(cached => cached || caches.match('/index.html')))
  )
})
