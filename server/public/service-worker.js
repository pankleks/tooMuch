const CACHE_NAME = "toomuch-admin-v1";
const APP_SHELL = [
  "/admin",
  "/admin-static/admin.js",
  "/admin-static/device-status.js",
  "/admin-static/format.js",
  "/admin-static/manifest.webmanifest",
  "/admin-static/icons/icon-192.png",
  "/admin-static/icons/icon-512.png",
  "/admin-static/icons/icon-512-maskable.png",
  "/admin-static/icons/apple-touch-icon.png",
];

self.addEventListener("install", event => {
  event.waitUntil(caches.open(CACHE_NAME).then(cache => cache.addAll(APP_SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", event => {
  event.waitUntil((async () => {
    const names = await caches.keys();
    await Promise.all(names.filter(name => name.startsWith("toomuch-admin-") && name !== CACHE_NAME)
      .map(name => caches.delete(name)));
    await self.clients.claim();
  })());
});

self.addEventListener("fetch", event => {
  const request = event.request;
  const url = new URL(request.url);
  if (request.method !== "GET" || url.origin !== self.location.origin || url.pathname.startsWith("/api/")) return;

  if (request.mode === "navigate" && url.pathname === "/admin") {
    event.respondWith(networkFirst(request, "/admin"));
    return;
  }

  if (url.pathname.startsWith("/admin-static/")) event.respondWith(networkFirst(request));
});

async function networkFirst(request, cacheKey = request) {
  try {
    const response = await fetch(request);
    if (response.ok) {
      const cache = await caches.open(CACHE_NAME);
      await cache.put(cacheKey, response.clone());
    }
    return response;
  } catch {
    const cached = await caches.match(cacheKey);
    if (cached) return cached;
    return new Response("You are offline. Reconnect to the tooMuch server and try again.", {
      status: 503,
      headers: { "Content-Type": "text/plain; charset=utf-8" },
    });
  }
}
