// Minimal pointer-driven signature pad.
//   SigPad.init("opSig")        — wire pointer events on a <canvas id="opSig">
//   SigPad.clear("opSig")       — wipe
//   SigPad.isEmpty("opSig")     — true if nothing drawn yet
//   SigPad.toDataUrl("opSig")   — "data:image/png;base64,..." or null if empty
//
// Designed for Blazor Hybrid JS interop. No npm deps, no external script.
window.SigPad = (function () {
    'use strict';

    function getCanvas(id) {
        const c = document.getElementById(id);
        if (!c) return null;
        return c;
    }

    function resize(canvas, ctx) {
        const rect = canvas.getBoundingClientRect();
        const dpr  = window.devicePixelRatio || 1;
        // Preserve drawn content across DPR resizes by snapshotting first
        const snap = canvas._dirty ? canvas.toDataURL('image/png') : null;

        canvas.width  = Math.max(1, Math.round(rect.width  * dpr));
        canvas.height = Math.max(1, Math.round(rect.height * dpr));
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.scale(dpr, dpr);
        ctx.strokeStyle = '#0f172a';
        ctx.lineWidth   = 2;
        ctx.lineCap     = 'round';
        ctx.lineJoin    = 'round';

        if (snap) {
            const img = new Image();
            img.onload = function () { ctx.drawImage(img, 0, 0, rect.width, rect.height); };
            img.src = snap;
        }
    }

    function init(id) {
        const canvas = getCanvas(id);
        if (!canvas || canvas._sigpadInited) return;
        canvas._sigpadInited = true;
        canvas._dirty        = false;

        const ctx = canvas.getContext('2d');
        resize(canvas, ctx);

        let drawing = false;

        function ptFromEvent(e) {
            const rect = canvas.getBoundingClientRect();
            return {
                x: (e.clientX ?? (e.touches && e.touches[0]?.clientX) ?? 0) - rect.left,
                y: (e.clientY ?? (e.touches && e.touches[0]?.clientY) ?? 0) - rect.top
            };
        }

        function start(e) {
            if (e.button !== undefined && e.button !== 0) return;
            e.preventDefault();
            drawing = true;
            canvas._dirty = true;
            const p = ptFromEvent(e);
            ctx.beginPath();
            ctx.moveTo(p.x, p.y);
            canvas.setPointerCapture && e.pointerId !== undefined && canvas.setPointerCapture(e.pointerId);
        }
        function move(e) {
            if (!drawing) return;
            e.preventDefault();
            const p = ptFromEvent(e);
            ctx.lineTo(p.x, p.y);
            ctx.stroke();
        }
        function end() { drawing = false; }

        canvas.addEventListener('pointerdown', start);
        canvas.addEventListener('pointermove', move);
        canvas.addEventListener('pointerup',   end);
        canvas.addEventListener('pointercancel', end);
        canvas.addEventListener('pointerleave',  end);

        // Debounced resize handler
        const ro = new ResizeObserver(function () { resize(canvas, ctx); });
        ro.observe(canvas);
    }

    function clear(id) {
        const canvas = getCanvas(id);
        if (!canvas) return;
        const ctx = canvas.getContext('2d');
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, canvas.width, canvas.height);
        const dpr = window.devicePixelRatio || 1;
        ctx.scale(dpr, dpr);
        canvas._dirty = false;
    }

    function isEmpty(id) {
        const canvas = getCanvas(id);
        return !canvas || !canvas._dirty;
    }

    function toDataUrl(id) {
        const canvas = getCanvas(id);
        if (!canvas || !canvas._dirty) return null;
        return canvas.toDataURL('image/png');
    }

    return { init, clear, isEmpty, toDataUrl };
})();
