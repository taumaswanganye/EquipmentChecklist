/*
 * OfflineVoucher · Phase 3
 *
 *   Stores the latest 24-hour offline voucher in IndexedDB, verifies it
 *   locally with Web Crypto using the pinned server public key, exposes
 *   a tiny PIN-verification helper that matches the C# PBKDF2 format,
 *   and refreshes the voucher in the background whenever the device
 *   gets back online.
 *
 *   Phase 4's Service Worker reads from the same IndexedDB store, so
 *   moving the cache there now (instead of localStorage) means no churn
 *   when the SW lands.
 *
 *   API on window.OfflineVoucher:
 *     await init()
 *     await getStored()                   → { voucher, expires_at, payload } | null
 *     await setStored(voucher, expires)   → persists + verifies
 *     await refresh()                     → fetches fresh voucher from server
 *     await verifyJwt(voucher)            → returns decoded payload or null
 *     await verifyPin(pin, pinHashStr)    → boolean
 *     secondsLeft(expires)                → integer
 *     formatLeft(expires)                 → "22h 14m"
 *     onChange(fn)                        → subscribe to voucher updates
 */
(function () {
    'use strict';

    // ── IndexedDB ────────────────────────────────────────────────────────────
    const DB_NAME = 'eq_offline';
    const DB_VER  = 1;
    const STORE   = 'kv';
    const KEY_VOUCHER = 'voucher';
    const KEY_PUBKEY  = 'pubkey';

    function openDb() {
        return new Promise(function (resolve, reject) {
            const req = indexedDB.open(DB_NAME, DB_VER);
            req.onupgradeneeded = function () {
                req.result.createObjectStore(STORE);
            };
            req.onsuccess = function () { resolve(req.result); };
            req.onerror   = function () { reject(req.error); };
        });
    }

    function dbGet(key) {
        return openDb().then(function (db) {
            return new Promise(function (resolve, reject) {
                const tx = db.transaction(STORE, 'readonly');
                const r  = tx.objectStore(STORE).get(key);
                r.onsuccess = function () { resolve(r.result); };
                r.onerror   = function () { reject(r.error); };
            });
        });
    }

    function dbPut(key, value) {
        return openDb().then(function (db) {
            return new Promise(function (resolve, reject) {
                const tx = db.transaction(STORE, 'readwrite');
                tx.objectStore(STORE).put(value, key);
                tx.oncomplete = function () { resolve(); };
                tx.onerror    = function () { reject(tx.error); };
            });
        });
    }

    function dbDel(key) {
        return openDb().then(function (db) {
            return new Promise(function (resolve, reject) {
                const tx = db.transaction(STORE, 'readwrite');
                tx.objectStore(STORE).delete(key);
                tx.oncomplete = function () { resolve(); };
                tx.onerror    = function () { reject(tx.error); };
            });
        });
    }

    // ── base64url ────────────────────────────────────────────────────────────
    function b64uToBytes(b64u) {
        const pad = '='.repeat((4 - (b64u.length % 4)) % 4);
        const b64 = (b64u + pad).replace(/-/g, '+').replace(/_/g, '/');
        const bin = atob(b64);
        const out = new Uint8Array(bin.length);
        for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
        return out;
    }

    // ── Pubkey fetch + cache ─────────────────────────────────────────────────
    async function getCachedPubkey(forceRefresh) {
        if (!forceRefresh) {
            const cached = await dbGet(KEY_PUBKEY);
            if (cached) return cached;
        }
        if (!navigator.onLine) return null; // can't fetch offline
        try {
            const resp = await fetch('/api/v1/auth-pubkey',
                { credentials: 'same-origin', cache: 'force-cache' });
            if (!resp.ok) return null;
            const jwk = await resp.json();
            await dbPut(KEY_PUBKEY, jwk);
            return jwk;
        } catch (_) {
            return null;
        }
    }

    // ── JWT verify (RS256) ───────────────────────────────────────────────────
    async function verifyJwt(token) {
        try {
            const parts = (token || '').split('.');
            if (parts.length !== 3) return null;

            const jwk = await getCachedPubkey(false);
            if (!jwk) return null;

            const key = await crypto.subtle.importKey(
                'jwk', jwk,
                { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
                false, ['verify']);
            const sig  = b64uToBytes(parts[2]);
            const data = new TextEncoder().encode(parts[0] + '.' + parts[1]);
            const ok   = await crypto.subtle.verify('RSASSA-PKCS1-v1_5', key, sig, data);
            if (!ok) return null;

            const payloadJson = new TextDecoder().decode(b64uToBytes(parts[1]));
            const payload     = JSON.parse(payloadJson);
            const nowS        = Math.floor(Date.now() / 1000);
            if (payload.exp && payload.exp <= nowS) return null;
            return payload;
        } catch (_) {
            return null;
        }
    }

    // ── PIN verify (matches C# Rfc2898DeriveBytes / SHA-256) ─────────────────
    //   Hash format: "pbkdf2$<iters>$<saltB64>$<hashB64>"
    async function verifyPin(pin, hashStr) {
        try {
            const parts = (hashStr || '').split('$');
            if (parts.length !== 4 || parts[0] !== 'pbkdf2') return false;
            const iters    = parseInt(parts[1], 10);
            const salt     = Uint8Array.from(atob(parts[2]), function (c) { return c.charCodeAt(0); });
            const expected = Uint8Array.from(atob(parts[3]), function (c) { return c.charCodeAt(0); });

            const pwKey = await crypto.subtle.importKey(
                'raw', new TextEncoder().encode(pin),
                { name: 'PBKDF2' }, false, ['deriveBits']);
            const bits = await crypto.subtle.deriveBits(
                { name: 'PBKDF2', salt: salt, iterations: iters, hash: 'SHA-256' },
                pwKey, expected.length * 8);
            const derived = new Uint8Array(bits);

            if (derived.length !== expected.length) return false;
            let diff = 0;
            for (let i = 0; i < derived.length; i++) diff |= derived[i] ^ expected[i];
            return diff === 0;
        } catch (_) {
            return false;
        }
    }

    // ── Voucher get/set ──────────────────────────────────────────────────────
    async function getStored() {
        const rec = await dbGet(KEY_VOUCHER);
        if (!rec) return null;
        // Lazy re-verify so a tampered IndexedDB doesn't fool us.
        const payload = await verifyJwt(rec.voucher);
        if (!payload) return null;
        return { voucher: rec.voucher, expires_at: rec.expires_at, payload: payload };
    }

    async function setStored(voucher, expiresAt) {
        const payload = await verifyJwt(voucher);
        if (!payload) throw new Error('Voucher failed signature verification — refusing to store.');
        await dbPut(KEY_VOUCHER, { voucher: voucher, expires_at: expiresAt || payload.exp });
        notify();
    }

    async function clearStored() {
        await dbDel(KEY_VOUCHER);
        notify();
    }

    // ── Refresh from server ──────────────────────────────────────────────────
    let refreshInFlight = null;
    async function refresh() {
        if (!navigator.onLine) return null;
        if (refreshInFlight) return refreshInFlight;
        refreshInFlight = (async function () {
            try {
                const resp = await fetch('/api/v1/offline-voucher', { credentials: 'same-origin' });
                if (!resp.ok) return null;
                const body = await resp.json();
                if (!body || !body.voucher) return null;
                await setStored(body.voucher, body.expires_at);
                return body;
            } catch (_) {
                return null;
            } finally {
                refreshInFlight = null;
            }
        })();
        return refreshInFlight;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    function secondsLeft(expiresAt) {
        const now = Math.floor(Date.now() / 1000);
        return Math.max(0, (expiresAt || 0) - now);
    }

    function formatLeft(expiresAt) {
        const s = secondsLeft(expiresAt);
        if (s <= 0) return 'expired';
        const h = Math.floor(s / 3600);
        const m = Math.floor((s % 3600) / 60);
        if (h > 0) return h + 'h ' + m + 'm';
        return m + 'm';
    }

    // ── Observer pattern so the layout badge stays current ───────────────────
    const listeners = [];
    function onChange(fn) { if (typeof fn === 'function') listeners.push(fn); }
    function notify() {
        getStored().then(function (rec) { listeners.forEach(function (fn) { try { fn(rec); } catch (_) {} }); });
    }

    // ── Lifecycle ────────────────────────────────────────────────────────────
    let initialized = false;
    async function init() {
        if (initialized) return;
        initialized = true;

        // Migrate any localStorage voucher dropped by Phase 2 into IndexedDB.
        try {
            const oldV = localStorage.getItem('eq_voucher');
            const oldE = parseInt(localStorage.getItem('eq_voucher_expires') || '0', 10);
            if (oldV) {
                await setStored(oldV, oldE).catch(function () {});
                localStorage.removeItem('eq_voucher');
                localStorage.removeItem('eq_voucher_expires');
            }
        } catch (_) {}

        // Warm the cache.
        getCachedPubkey(false).catch(function () {});

        // Refresh when the device first comes online or when network returns.
        if (navigator.onLine) { refresh().catch(function () {}); }
        window.addEventListener('online', function () {
            refresh().catch(function () {});
            getCachedPubkey(true).catch(function () {});
        });

        // Periodic refresh — every 30 min while online — so the voucher
        // never sits within 30 min of expiry if the user is active.
        setInterval(function () {
            if (navigator.onLine) refresh().catch(function () {});
        }, 30 * 60 * 1000);

        // First notification so any subscribed badge can render immediately.
        notify();
    }

    window.OfflineVoucher = {
        init: init,
        getStored: getStored,
        setStored: setStored,
        clearStored: clearStored,
        refresh: refresh,
        verifyJwt: verifyJwt,
        verifyPin: verifyPin,
        secondsLeft: secondsLeft,
        formatLeft: formatLeft,
        onChange: onChange
    };
})();
