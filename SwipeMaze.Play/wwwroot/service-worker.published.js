// Offline support for Swipe Maze (based on the standard Blazor service worker).
// See https://aka.ms/blazor-offline-considerations for the caveats of offline support.
//
// One change from the standard worker, and it matters: some hosts (Cloudflare, Netlify) answer a request for
// /index.html with a redirect to /. A response that was reached through a redirect is marked "redirected", and
// a browser refuses to show a redirected response a service worker hands back for a page load: the first visit
// works (it comes from the network) and every later launch fails with "Website not available". So pages go
// into the cache, and come back out of it, as plain copies with the redirect marking removed.

self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/ ];

// Replace with your base path if you are hosting on a subfolder. Ensure there is a trailing '/'.
const base = "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

/** A copy of the response that was not reached through a redirect, which is safe to give back for a page load. */
async function cleanResponse(response) {
    if (!response.redirected) return response;
    const body = await response.blob();
    return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
}

async function onInstall(event) {
    console.info('Service worker: Install');

    // Take over from an older worker straight away, so a fix reaches people on their next launch.
    self.skipWaiting();

    const cache = await caches.open(cacheName);

    // Fetch and cache all matching items from the assets manifest, apart from the start page (handled below)
    const assetsRequests = self.assetsManifest.assets
        .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
        .filter(asset => asset.url !== 'index.html')
        .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
    await cache.addAll(assetsRequests);

    // The start page, stored as a plain copy (see the note at the top).
    const index = await fetch(new Request('index.html', { cache: 'no-cache' }));
    if (!index.ok) throw new Error('Could not fetch index.html');
    await cache.put('index.html', await cleanResponse(index));
}

async function onActivate(event) {
    console.info('Service worker: Activate');

    // Delete unused caches
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys
        .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
        .map(key => caches.delete(key)));

    // Start serving pages that are already open
    await self.clients.claim();
}

async function onFetch(event) {
    let cachedResponse = null;
    if (event.request.method === 'GET') {
        // For all navigation requests, try to serve index.html from cache,
        // unless that request is for an offline resource.
        // If you need some URLs to be server-rendered, edit the following check to exclude those URLs
        const shouldServeIndexHtml = event.request.mode === 'navigate'
            && !manifestUrlList.some(url => url === event.request.url);

        const request = shouldServeIndexHtml ? 'index.html' : event.request;
        const cache = await caches.open(cacheName);
        cachedResponse = await cache.match(request);
    }

    // Never hand back a redirected copy (an older worker may have stored one); fall back to the network instead.
    if (cachedResponse && cachedResponse.redirected) cachedResponse = await cleanResponse(cachedResponse);
    return cachedResponse || fetch(event.request);
}
