// Generic input shim for BlazorMapView.
// Owns the --pan-x / --pan-y CSS variables on the viewport. C# never writes them
// via inline style — only via setPan() below. This makes pan visually decoupled
// from SignalR latency: pointermove updates --pan-x at native refresh rate while
// C# only needs to be notified when the tile range changes (every ~100 px of pan).

const instances = new WeakMap();

export function init(viewportEl, dotnetRef) {
    if (!viewportEl || instances.has(viewportEl)) return;

    const state = {
        viewportEl,
        dotnetRef,
        dragging: false,
        pointerId: -1,
        panX: 0, panY: 0,                    // absolute current pan offset (CSS px)
        sinceLastPostDx: 0, sinceLastPostDy: 0, // accumulated since last post to C#
        wheelSign: 0, wheelAnchorX: 0, wheelAnchorY: 0,
        rafQueued: false,
        ro: null,
    };

    const applyPan = () => {
        viewportEl.style.setProperty('--pan-x', state.panX + 'px');
        viewportEl.style.setProperty('--pan-y', state.panY + 'px');
    };

    const flush = () => {
        state.rafQueued = false;
        if (state.sinceLastPostDx !== 0 || state.sinceLastPostDy !== 0) {
            const dx = state.sinceLastPostDx, dy = state.sinceLastPostDy;
            state.sinceLastPostDx = 0; state.sinceLastPostDy = 0;
            dotnetRef.invokeMethodAsync('OnPanDelta', dx, dy);
        }
        if (state.wheelSign !== 0) {
            const sign = state.wheelSign, ax = state.wheelAnchorX, ay = state.wheelAnchorY;
            state.wheelSign = 0;
            dotnetRef.invokeMethodAsync('OnZoom', sign, ax, ay);
        }
    };
    const schedule = () => { if (!state.rafQueued) { state.rafQueued = true; requestAnimationFrame(flush); } };

    state.onPointerDown = (e) => {
        if (e.button !== 0) return;
        state.dragging = true;
        state.pointerId = e.pointerId;
        viewportEl.setPointerCapture(e.pointerId);
    };
    state.onPointerMove = (e) => {
        if (!state.dragging || e.pointerId !== state.pointerId) return;
        state.panX += e.movementX;
        state.panY += e.movementY;
        state.sinceLastPostDx += e.movementX;
        state.sinceLastPostDy += e.movementY;
        applyPan();
        schedule();
    };
    state.onPointerUp = (e) => {
        if (e.pointerId !== state.pointerId) return;
        state.dragging = false;
        try { viewportEl.releasePointerCapture(state.pointerId); } catch { }
        // flush any pending pan delta
        if (state.sinceLastPostDx !== 0 || state.sinceLastPostDy !== 0) {
            const dx = state.sinceLastPostDx, dy = state.sinceLastPostDy;
            state.sinceLastPostDx = 0; state.sinceLastPostDy = 0;
            dotnetRef.invokeMethodAsync('OnPanDelta', dx, dy);
        }
        dotnetRef.invokeMethodAsync('OnPanEnd');
    };
    state.onWheel = (e) => {
        e.preventDefault();
        const r = viewportEl.getBoundingClientRect();
        state.wheelSign = e.deltaY > 0 ? 1 : -1;
        state.wheelAnchorX = e.clientX - r.left;
        state.wheelAnchorY = e.clientY - r.top;
        schedule();
    };
    state.onContextMenu = (e) => { e.preventDefault(); };
    state.onTransitionEnd = (e) => {
        if (e.target === viewportEl || (e.target && e.target.dataset && e.target.dataset.zoomLayer)) {
            dotnetRef.invokeMethodAsync('OnZoomTransitionEnd');
        }
    };

    // Capture phase so wheel/pointer events fire before they reach child elements
    // (markers, roads, etc.) — otherwise a target like a marker <img> can absorb
    // the wheel and the bubble-phase listener on the viewport never runs.
    viewportEl.addEventListener('pointerdown', state.onPointerDown, true);
    viewportEl.addEventListener('pointermove', state.onPointerMove, true);
    viewportEl.addEventListener('pointerup', state.onPointerUp, true);
    viewportEl.addEventListener('pointercancel', state.onPointerUp, true);
    viewportEl.addEventListener('wheel', state.onWheel, { passive: false, capture: true });
    viewportEl.addEventListener('contextmenu', state.onContextMenu);
    viewportEl.addEventListener('transitionend', state.onTransitionEnd);

    if (typeof ResizeObserver !== 'undefined') {
        state.ro = new ResizeObserver(() => {
            const r = viewportEl.getBoundingClientRect();
            dotnetRef.invokeMethodAsync('OnViewportResize', Math.round(r.width), Math.round(r.height), Math.round(r.left), Math.round(r.top));
        });
        state.ro.observe(viewportEl);
    }

    // Page scroll can change the viewport's page-relative offset without resizing.
    // Re-send rect on scroll so screen-to-world conversion stays accurate.
    state.onScroll = () => {
        const r = viewportEl.getBoundingClientRect();
        dotnetRef.invokeMethodAsync('OnViewportResize', Math.round(r.width), Math.round(r.height), Math.round(r.left), Math.round(r.top));
    };
    window.addEventListener('scroll', state.onScroll, { passive: true, capture: true });

    instances.set(viewportEl, state);
    applyPan();
    const r = viewportEl.getBoundingClientRect();
    dotnetRef.invokeMethodAsync('OnViewportResize', Math.round(r.width), Math.round(r.height), Math.round(r.left), Math.round(r.top));
}

// C# tells us the absolute pan offset (in CSS px). Used on initial layout,
// jump-to, zoom-to-cursor, and set-view. Does NOT post a delta back to C#.
export function setPan(viewportEl, x, y) {
    const s = instances.get(viewportEl);
    if (!s) return;
    s.panX = x;
    s.panY = y;
    s.sinceLastPostDx = 0;
    s.sinceLastPostDy = 0;
    s.viewportEl.style.setProperty('--pan-x', x + 'px');
    s.viewportEl.style.setProperty('--pan-y', y + 'px');
}

export function getBoundingRect(el) {
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return { x: r.left, y: r.top, w: r.width, h: r.height };
}

export function dispose(viewportEl) {
    const s = instances.get(viewportEl);
    if (!s) return;
    viewportEl.removeEventListener('pointerdown', s.onPointerDown, true);
    viewportEl.removeEventListener('pointermove', s.onPointerMove, true);
    viewportEl.removeEventListener('pointerup', s.onPointerUp, true);
    viewportEl.removeEventListener('pointercancel', s.onPointerUp, true);
    viewportEl.removeEventListener('wheel', s.onWheel, { capture: true });
    viewportEl.removeEventListener('contextmenu', s.onContextMenu);
    viewportEl.removeEventListener('transitionend', s.onTransitionEnd);
    if (s.ro) s.ro.disconnect();
    if (s.onScroll) window.removeEventListener('scroll', s.onScroll, { capture: true });
    instances.delete(viewportEl);
}
