/*
 * Service Worker registrar (operator-only).
 *
 *  - Registers /sw.js with root scope.
 *  - Asks the SW for the current queue count on load, and again whenever the
 *    SW pushes a 'queue-changed' message.
 *  - On reconnect, asks the SW to replay queued submissions.
 *  - Drives the queued-submissions badge in the top bar (#queuedBadge).
 */
(function () {
    'use strict';

    if (!('serviceWorker' in navigator)) return;

    let swReady = null;

    window.addEventListener('load', function () {
        navigator.serviceWorker.register('/sw.js', { scope: '/' })
            .then(function (reg) {
                swReady = reg;
                askCount();
            })
            .catch(function (err) {
                console.warn('[sw] register failed', err);
            });
    });

    // ── Messages from the SW ─────────────────────────────────────────────────
    navigator.serviceWorker.addEventListener('message', function (ev) {
        const data = ev.data || {};
        if (data.type === 'queue-count')    paintBadge(data.count);
        if (data.type === 'queue-changed')  askCount();
    });

    // ── Reconnect → ask SW to replay ─────────────────────────────────────────
    window.addEventListener('online', function () {
        sendToSW({ type: 'replay' });
        askCount();
    });

    function sendToSW(message) {
        if (navigator.serviceWorker.controller) {
            navigator.serviceWorker.controller.postMessage(message);
        }
    }

    function askCount() {
        sendToSW({ type: 'queue-count' });
    }

    // ── Badge painter ────────────────────────────────────────────────────────
    function paintBadge(n) {
        const badge = document.getElementById('queuedBadge');
        const count = document.getElementById('queuedCount');
        if (!badge || !count) return;

        if (n > 0) {
            count.textContent     = String(n);
            badge.style.display   = 'inline-flex';
            badge.title           = n + ' submission(s) waiting to sync.';
        } else {
            badge.style.display   = 'none';
        }
    }

    // Expose a manual "replay now" trigger for the badge click handler.
    window.OfflineQueue = {
        replayNow: function () { sendToSW({ type: 'replay' }); },
        refreshCount: askCount
    };
})();
