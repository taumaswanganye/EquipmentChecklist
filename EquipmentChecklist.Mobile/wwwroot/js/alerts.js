// ────────────────────────────────────────────────────────────────────────────
//  alerts.js — synthesised audio alerts for safety-critical moments
//
//  Plays a short, attention-grabbing alarm tone using the Web Audio API.
//  Synthesising in-browser means we don't have to ship an MP3 / WAV (an
//  unnecessary ~20 KB per app build) and the same code works identically
//  on Windows WebView2 and Android System WebView.
//
//  Browser autoplay rules:
//    Browsers block AudioContext playback until a user gesture has happened
//    in the page. In the operator flow this is the "Submit" button tap, so
//    when we play the NO-GO alarm we're well within the gesture window and
//    Chrome/Edge/Safari all let us through.
//
//  Why not just <audio> + a static file:
//    1. No file to fetch, no caching headache, works offline trivially.
//    2. We can shape the envelope (attack/release) and pitch precisely so
//       the tone reads as "alert", not "notification" — it's deliberately
//       harsher than a regular toast.
//    3. Volume is controllable via a GainNode; an audio file would need
//       per-device EQ to sound consistent.
// ────────────────────────────────────────────────────────────────────────────

window.MineAlerts = (function () {

    // Lazy-init the AudioContext on first use. Creating it eagerly at page
    // load can trip the "context was not allowed to start" warning that
    // Chromium logs to console; deferring until the first explicit play
    // keeps the console clean for ordinary operator sessions.
    let ctx = null;
    function ensureContext() {
        if (ctx == null) {
            const AC = window.AudioContext || window.webkitAudioContext;
            if (!AC) return null;
            ctx = new AC();
        }
        // If the context was suspended by the OS (Android does this when
        // the app backgrounds) resume it before scheduling tones — without
        // this the alarm fires silently with no error.
        if (ctx.state === "suspended") {
            ctx.resume().catch(() => { /* user gesture exhausted; nothing we can do */ });
        }
        return ctx;
    }

    // Single tone burst. Sharp attack + short release so it reads as
    // "warning" rather than "musical". gainPeak caps loudness so we don't
    // make the operator jump out of their seat.
    function beep(frequency, durationMs, startOffsetSec = 0, gainPeak = 0.25) {
        const c = ensureContext();
        if (!c) return;

        const start = c.currentTime + startOffsetSec;
        const stop  = start + durationMs / 1000;

        const osc = c.createOscillator();
        // "square" gives the harsher "industrial alarm" timbre; "sine" would
        // sound too gentle for a NO-GO alert, "sawtooth" is closer but a bit
        // buzzy. Square strikes the right balance.
        osc.type = "square";
        osc.frequency.setValueAtTime(frequency, start);

        const gain = c.createGain();
        // Quick attack (5ms ramp from 0 → peak), full hold, quick release.
        // The envelope is what stops it sounding like a ringtone.
        gain.gain.setValueAtTime(0, start);
        gain.gain.linearRampToValueAtTime(gainPeak, start + 0.005);
        gain.gain.setValueAtTime(gainPeak, stop - 0.02);
        gain.gain.linearRampToValueAtTime(0, stop);

        osc.connect(gain);
        gain.connect(c.destination);
        osc.start(start);
        osc.stop(stop + 0.05);
    }

    return {
        // ── NO-GO alarm ─────────────────────────────────────────────────
        // Three rising-then-falling beeps over ~1.4 seconds. The pattern
        // (880Hz → 660Hz → 880Hz) is the same alternation used in
        // industrial fire panels and is widely recognised as "danger".
        playNoGo: function () {
            try {
                beep(880, 200, 0.00);   // beep 1 — high
                beep(660, 200, 0.30);   // beep 2 — low
                beep(880, 200, 0.60);   // beep 3 — high
                beep(660, 400, 0.95);   // longer low tail — finality
            } catch (e) {
                // Audio failures must never bubble — they'd mask the
                // important NO-GO toast & navigation. Log and move on.
                console.warn("MineAlerts.playNoGo:", e);
            }
        },

        // ── GO-BUT chime ────────────────────────────────────────────────
        // Two brief mid-pitched beeps. Reads as "heads up, attention
        // required" without the dread of the NO-GO pattern. Played when a
        // supervisor sign-off is required.
        playGoBut: function () {
            try {
                beep(660, 150, 0.00, 0.18);
                beep(880, 200, 0.22, 0.18);
            } catch (e) {
                console.warn("MineAlerts.playGoBut:", e);
            }
        },

        // Diagnostic helper for the debug page — confirms audio is wired
        // up correctly on this device.
        test: function () {
            try { beep(880, 250, 0, 0.20); }
            catch (e) { console.warn("MineAlerts.test:", e); }
        },

        // ── Phase 8.1 — URGENT siren loop ────────────────────────────────
        // Two-tone rising-falling siren that loops for up to durationSec
        // seconds (default 30). Fires from C# when an operator raises a
        // NO-GO AND the device has no server connectivity — anyone within
        // audio range knows immediately without needing the app.
        //
        // Returns a handle the C# layer can use to stopUrgent() early if
        // the operator dismisses (e.g. taps a "I've notified somebody"
        // button). Without a stop call the siren self-cancels at duration.
        _urgentTimer: null,
        startUrgent: function (durationSec) {
            durationSec = durationSec || 30;
            this.stopUrgent();   // never stack two sirens
            const self = this;
            // Each cycle = high (900 Hz) for 500ms then low (650 Hz) for
            // 500ms. Classic European emergency-vehicle siren cadence
            // chosen because it's loud, recognisable, and not easily
            // confused with the existing playNoGo trill.
            const fire = function () {
                try {
                    beep(900, 480, 0.00, 0.55);
                    beep(650, 480, 0.50, 0.55);
                } catch (e) {
                    console.warn("MineAlerts.startUrgent:", e);
                }
            };
            fire();
            this._urgentTimer = setInterval(fire, 1000);
            // Auto-stop after the configured duration so a forgotten
            // alert doesn't drain the battery + irritate the team.
            setTimeout(function () { self.stopUrgent(); }, durationSec * 1000);
        },
        stopUrgent: function () {
            if (this._urgentTimer != null) {
                clearInterval(this._urgentTimer);
                this._urgentTimer = null;
            }
        }
    };
})();
