/*
 * Equipment Checklist · Service Worker (Phase 4)
 *
 *  Goal: let an operator keep submitting checklists with no signal.
 *
 *  Strategy
 *    1. CACHE the operator UI shell (HTML + CSS + JS + auth pubkey) on install.
 *    2. NAVIGATE: network-first, fall back to the cached shell so /Checklist
 *       and /Checklist/Start/{id} still render offline.
 *    3. STATIC: stale-while-revalidate for /css/* /js/* /lib/* /item-images/*
 *       /icon-library/* — fast and works offline.
 *    4. SUBMIT: POST /Checklist/Submit is intercepted; if the network is dead
 *       we queue the request in IndexedDB and return a synthetic 202 so the
 *       page can show "queued for sync".
 *    5. REPLAY: when 'sync' fires (Background Sync API) OR when a client posts
 *       a 'replay' message, drain the queue against the server.
 *
 *  Auth note
 *    The SW doesn't inject the offline voucher as a Bearer header today —
 *    queued requests rely on the existing Identity cookie (14-day lifetime
 *    by default), which is usually still valid when the user reconnects.
 *    If you start seeing 401s on replay, that's the polish to add next.
 */

// Bump this constant any time a SHELL_URLS asset changes (offline-voucher.js,
// site.css, etc) OR when the SW itself changes behaviour. The ACTIVATE handler
// below evicts every cache whose key doesn't match the current SHELL/STATIC
// cache names — so the browser stops serving stale copies the next time the
// SW activates. Without a bump, clients keep getting the old offline-voucher.js
// from disk cache forever and IndexedDB version errors (and similar) survive
// any number of source edits.
const SW_VERSION  = 'eq-sw-v2';
const SHELL_CACHE = 'eq-shell-' + SW_VERSION;
const STATIC_CACHE = 'eq-static-' + SW_VERSION;

// Pages and assets we want available without a network round-trip.
const SHELL_URLS = [
    '/',
    '/Checklist',
    '/Account/Login',
    '/css/site.css',
    '/js/offline-voucher.js',
    '/js/webauthn.js',
    '/js/sw-register.js',
    '/api/v1/auth-pubkey'
];

// ── INSTALL · pre-cache the shell ────────────────────────────────────────────
self.addEventListener('install', (event) => {
    event.waitUntil((async () => {
        const cache = await caches.open(SHELL_CACHE);
        // Cache best-effort — a missing asset shouldn't break the SW install.
        await Promise.all(SHELL_URLS.map(async (url) => {
            try { await cache.add(new Request(url, { credentials: 'same-origin' })); }
            catch (_) { /* swallow */ }
        }));
        self.skipWaiting();
    })());
});

// ── ACTIVATE · evict old caches ──────────────────────────────────────────────
self.addEventListener('activate', (event) => {
    event.waitUntil((async () => {
        const keys = await caches.keys();
        await Promise.all(keys
            .filter(k => k !== SHELL_CACHE && k !== STATIC_CACHE)
            .map(k => caches.delete(k)));
        await self.clients.claim();
    })());
});

// ── FETCH · route by request shape ───────────────────────────────────────────
self.addEventListener('fetch', (event) => {
    const req = event.request;
    const url = new URL(req.url);

    // Only handle same-origin (don't proxy CDN scripts, signature_pad.js, etc.)
    if (url.origin !== location.origin) return;

    // 1 · Submission POSTs — queue when offline
    if (req.method === 'POST' && url.pathname === '/Checklist/Submit') {
        event.respondWith(handleSubmit(req));
        return;
    }

    // 2 · Navigations — try network, fall back to cached shell
    if (req.mode === 'navigate' && req.method === 'GET') {
        event.respondWith(handleNavigate(req));
        return;
    }

    // 3 · GET static assets — stale-while-revalidate
    if (req.method === 'GET' && isStaticPath(url.pathname)) {
        event.respondWith(handleStatic(req));
        return;
    }

    // Everything else: passthrough (don't break API calls)
});

function isStaticPath(p) {
    return p.startsWith('/css/')
        || p.startsWith('/js/')
        || p.startsWith('/lib/')
        || p.startsWith('/item-images/')
        || p.startsWith('/icon-library/')
        || p.startsWith('/uploads/')
        || p === '/api/v1/auth-pubkey';
}

// ── Strategies ───────────────────────────────────────────────────────────────
async function handleNavigate(req) {
    try {
        const fresh = await fetch(req);
        // Cache successful HTML for next time
        if (fresh.ok && fresh.type === 'basic') {
            const cache = await caches.open(SHELL_CACHE);
            cache.put(req, fresh.clone()).catch(() => {});
        }
        return fresh;
    } catch (_) {
        // Try exact URL first, then the /Checklist shell, then bare /
        return (await caches.match(req))
            || (await caches.match('/Checklist'))
            || (await caches.match('/'))
            || new Response(offlineFallbackHtml(), {
                status: 503,
                headers: { 'Content-Type': 'text/html; charset=utf-8' }
            });
    }
}

async function handleStatic(req) {
    const cache  = await caches.open(STATIC_CACHE);
    const cached = await cache.match(req);
    const network = fetch(req).then((resp) => {
        if (resp && resp.ok) cache.put(req, resp.clone()).catch(() => {});
        return resp;
    }).catch(() => null);
    return cached || (await network) || new Response('', { status: 504 });
}

async function handleSubmit(req) {
    // Optimistic online attempt first.
    try {
        const resp = await fetch(req.clone());
        return resp;
    } catch (_) {
        // Network unavailable — queue the request and tell the page we did.
        try {
            const entry = await snapshotRequest(req);
            await queuePut(entry);
            await broadcast({ type: 'queue-changed' });
            // Try to register a Background Sync if supported.
            try { await self.registration.sync.register('eq-submit-queue'); } catch (_) {}
            return new Response(
                JSON.stringify({ ok: true, queued: true, message: 'Submission queued for sync.' }),
                { status: 202, headers: { 'Content-Type': 'application/json' } });
        } catch (e) {
            return new Response(
                JSON.stringify({ ok: false, error: 'Could not queue submission: ' + e.message }),
                { status: 500, headers: { 'Content-Type': 'application/json' } });
        }
    }
}

// ── Queue store (IndexedDB) ──────────────────────────────────────────────────
//
// Database is SHARED with offline-voucher.js — bump QUEUE_DB_VER in lock-step
// with the DB_VER constant over there, and add any new store creation here
// in an idempotent block so concurrent openers don't double-create.
//
// If a future script bumps the on-disk version higher than ours, the
// VersionError fallback below opens the DB without a version argument so
// we just attach to whatever's there.
const QUEUE_DB     = 'eq_offline';
const QUEUE_DB_VER = 2;
const QUEUE_STORE  = 'submit_queue';

function openQueueDb() {
    return new Promise((resolve, reject) => {
        let req;
        try {
            req = indexedDB.open(QUEUE_DB, QUEUE_DB_VER);
        } catch (e) { reject(e); return; }

        req.onupgradeneeded = () => {
            const db = req.result;
            // Idempotent — only create the store if a concurrent opener
            // hasn't already done so during an earlier upgrade transaction.
            if (!db.objectStoreNames.contains(QUEUE_STORE)) {
                db.createObjectStore(QUEUE_STORE, { autoIncrement: true });
            }
            if (!db.objectStoreNames.contains('kv')) {
                db.createObjectStore('kv'); // shared with offline-voucher.js
            }
        };
        req.onsuccess = () => resolve(req.result);
        req.onerror   = () => {
            // The on-disk version is higher than what we asked for
            // (offline-voucher.js, or a future script, has bumped past us).
            // Attach without an upgrade — both stores are guaranteed to be
            // present because every version of this DB so far has created
            // them.
            if (req.error && req.error.name === 'VersionError') {
                let fb;
                try { fb = indexedDB.open(QUEUE_DB); }
                catch (e) { reject(e); return; }
                fb.onsuccess = () => resolve(fb.result);
                fb.onerror   = () => reject(fb.error);
                return;
            }
            reject(req.error);
        };
    });
}

async function snapshotRequest(req) {
    const headers = {};
    req.headers.forEach((v, k) => { headers[k] = v; });
    const contentType = headers['content-type'] || '';
    let bodyType = 'text';
    let body;
    if (req.body) {
        const buf = await req.clone().arrayBuffer();
        // Store as base64 to make replay trivial regardless of original encoding.
        body = bytesToB64(new Uint8Array(buf));
        bodyType = 'base64';
    }
    return {
        url: req.url,
        method: req.method,
        headers,
        body, bodyType, contentType,
        queuedAt: Date.now()
    };
}

function bytesToB64(bytes) {
    let s = '';
    for (let i = 0; i < bytes.length; i++) s += String.fromCharCode(bytes[i]);
    return btoa(s);
}
function b64ToBytes(b64) {
    const bin = atob(b64);
    const out = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
    return out;
}

function queuePut(entry) {
    return openQueueDb().then(db => new Promise((res, rej) => {
        const tx = db.transaction(QUEUE_STORE, 'readwrite');
        tx.objectStore(QUEUE_STORE).add(entry);
        tx.oncomplete = () => res();
        tx.onerror    = () => rej(tx.error);
    }));
}

function queueDelete(key) {
    return openQueueDb().then(db => new Promise((res, rej) => {
        const tx = db.transaction(QUEUE_STORE, 'readwrite');
        tx.objectStore(QUEUE_STORE).delete(key);
        tx.oncomplete = () => res();
        tx.onerror    = () => rej(tx.error);
    }));
}

function queueList() {
    return openQueueDb().then(db => new Promise((res, rej) => {
        const tx = db.transaction(QUEUE_STORE, 'readonly');
        const store = tx.objectStore(QUEUE_STORE);
        const out = [];
        store.openCursor().onsuccess = (ev) => {
            const cursor = ev.target.result;
            if (cursor) { out.push({ key: cursor.primaryKey, val: cursor.value }); cursor.continue(); }
            else { res(out); }
        };
        tx.onerror = () => rej(tx.error);
    }));
}

async function queueCount() {
    const items = await queueList();
    return items.length;
}

// ── Replay ───────────────────────────────────────────────────────────────────
async function replayQueue() {
    const items = await queueList();
    let pushed = 0;
    for (const item of items) {
        try {
            const init = {
                method: item.val.method,
                headers: item.val.headers || {},
                credentials: 'include'
            };
            if (item.val.body) {
                init.body = item.val.bodyType === 'base64'
                    ? b64ToBytes(item.val.body)
                    : item.val.body;
            }
            const resp = await fetch(item.val.url, init);
            if (resp.ok || (resp.status >= 300 && resp.status < 400)) {
                await queueDelete(item.key);
                pushed++;
            } else if (resp.status === 401 || resp.status === 403) {
                // Auth expired — leave queued; we'll need a fresh sign-in.
                break;
            }
            // Other 4xx/5xx: leave queued so admin can intervene; stop looping
            // to avoid hammering the server.
            else break;
        } catch (_) {
            // Network blip — stop, will retry on next 'online' or 'sync'.
            break;
        }
    }
    if (pushed > 0 || items.length === 0) {
        await broadcast({ type: 'queue-changed', pushed });
    }
}

self.addEventListener('sync', (event) => {
    if (event.tag === 'eq-submit-queue') event.waitUntil(replayQueue());
});

self.addEventListener('message', (event) => {
    const data = event.data || {};
    if (data.type === 'replay')        event.waitUntil(replayQueue());
    if (data.type === 'queue-count')   event.waitUntil(replyQueueCount(event));
    if (data.type === 'skipWaiting')   self.skipWaiting();
});

async function replyQueueCount(event) {
    const n = await queueCount();
    event.source?.postMessage({ type: 'queue-count', count: n });
}

async function broadcast(message) {
    const clients = await self.clients.matchAll({ includeUncontrolled: true });
    clients.forEach(c => c.postMessage(message));
}

// ── Minimal offline fallback page ────────────────────────────────────────────
function offlineFallbackHtml() {
    return `<!doctype html><html><head><meta charset="utf-8"><title>Offline</title>
<style>body{font-family:system-ui,sans-serif;background:#0b1726;color:#e5e7eb;
display:flex;align-items:center;justify-content:center;min-height:100vh;margin:0}
.card{background:#1f2937;border:1px solid #374151;padding:24px 28px;border-radius:10px;max-width:380px;text-align:center}
.card h1{font-size:18px;margin:0 0 8px}.card p{font-size:13px;color:#9ca3af;line-height:1.6}</style>
</head><body><div class="card"><h1>You're offline</h1>
<p>This page isn't cached on your device yet. Open <strong>/Checklist</strong> first while online so it's saved for offline use.</p>
</div></body></html>`;
}
