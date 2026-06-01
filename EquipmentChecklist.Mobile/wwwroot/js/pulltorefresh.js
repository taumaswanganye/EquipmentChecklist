// Pull-to-refresh helper for Blazor pages.
//
// Why custom: Blazor doesn't ship a built-in gesture; native MAUI's
// RefreshView lives outside the WebView, so we'd lose the unified
// look-and-feel. This wires touchstart / touchmove / touchend on a
// container element, computes a drag distance, and calls back into
// .NET when the operator pulls far enough and releases.
//
// Usage from the Razor component:
//   PullToRefresh.attach(el, dotNetRef, { threshold: 65 });
//   ... later ...
//   PullToRefresh.detach(el);
//
// dotNetRef must expose:
//   * OnProgress(percent)   — 0..100 while dragging
//   * OnTrigger()           — fired once when released past the threshold
//   * OnReset()             — fired when released without triggering
window.PullToRefresh = (function () {

  const handlers = new WeakMap();

  function attach(el, dotNetRef, options) {
    if (!el || handlers.has(el)) return;

    const opts = Object.assign({ threshold: 65, maxPull: 140 }, options || {});

    const state = {
      startY:   0,
      pullY:    0,
      tracking: false,
      armed:    false,    // true once the user has pulled past 8px from the top
    };

    function onTouchStart(e) {
      // Only honour the gesture when the page is scrolled to the top.
      // Otherwise touchmove would steal scroll events from the operator
      // who's just trying to scroll the list.
      if (window.scrollY > 0 && el.scrollTop > 0) return;
      if (e.touches.length !== 1) return;

      state.startY   = e.touches[0].clientY;
      state.pullY    = 0;
      state.tracking = true;
      state.armed    = false;
    }

    function onTouchMove(e) {
      if (!state.tracking) return;
      const dy = e.touches[0].clientY - state.startY;

      // Only react to *downward* drags.
      if (dy <= 0) {
        state.pullY = 0;
        dotNetRef.invokeMethodAsync('OnProgress', 0);
        return;
      }

      if (!state.armed && dy > 8) state.armed = true;
      if (!state.armed) return;

      // Light rubber-banding: damp the drag after threshold so the user
      // gets a visual hint they've hit the trigger but can keep going.
      const clamped = Math.min(opts.maxPull, dy);
      state.pullY = clamped;

      const percent = Math.min(100, (clamped / opts.threshold) * 100);
      dotNetRef.invokeMethodAsync('OnProgress', Math.round(percent));

      // Prevent the WebView from also scrolling the page when we're
      // intercepting the gesture.
      if (e.cancelable) e.preventDefault();
    }

    function onTouchEnd() {
      if (!state.tracking) return;
      state.tracking = false;

      if (state.pullY >= opts.threshold) {
        dotNetRef.invokeMethodAsync('OnTrigger');
      } else {
        dotNetRef.invokeMethodAsync('OnReset');
      }
    }

    el.addEventListener('touchstart', onTouchStart, { passive: true });
    el.addEventListener('touchmove',  onTouchMove,  { passive: false });
    el.addEventListener('touchend',   onTouchEnd,   { passive: true });
    el.addEventListener('touchcancel', onTouchEnd,  { passive: true });

    handlers.set(el, { onTouchStart, onTouchMove, onTouchEnd });
  }

  function detach(el) {
    if (!el || !handlers.has(el)) return;
    const h = handlers.get(el);
    el.removeEventListener('touchstart', h.onTouchStart);
    el.removeEventListener('touchmove',  h.onTouchMove);
    el.removeEventListener('touchend',   h.onTouchEnd);
    el.removeEventListener('touchcancel', h.onTouchEnd);
    handlers.delete(el);
  }

  return { attach, detach };
})();
