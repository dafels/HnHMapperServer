// Pauses the Blazor Server circuit while the tab is hidden (new in .NET 10).
//
// Why: a circuit lives as long as the tab holds the WebSocket, whether or not anyone is
// looking at it. Players leave the map or the cookbook open in a background tab for hours,
// and each of those tabs keeps its component state - and its references into the shared
// flat-row cache - alive on the server. Pausing evicts the circuit and keeps only a small
// serialized snapshot; resuming rebuilds it when the tab comes back.
//
// .NET 11 ships this as a built-in AutoPause package with a HiddenDelay. Until then this is
// the documented manual pattern, plus three things the docs' sample omits:
//   1. A delay, so flicking between tabs doesn't churn circuits.
//   2. Handling the boolean both calls return. resumeCircuit() returning false means the
//      state was evicted or expired; without a fallback the page sits there silently dead
//      (dotnet/aspnetcore#64607). We reload instead, which is the same recovery the user
//      would do by hand.
//   3. Purging JS-side listeners that hold DotNetObjectReferences into the paused circuit.
//      Component disposal during a pause cannot reach the browser (the circuit is already
//      disconnected, so MudBlazor swallows the JSDisconnectedException and its JS-side
//      cancelListener call never happens). The orphaned listeners keep watching the same
//      DOM nodes and fire invokeMethodAsync against object ids the resumed circuit does
//      not track - which surfaces as "There was an exception invoking 'OnSizeChanged'"
//      right after the resume's loading overlay clears (the reflow is what triggers the
//      stale ResizeObserver). The resumed circuit re-renders from scratch (fresh
//      firstRender), so it re-creates every listener it needs; the old ones are garbage
//      by definition the moment the pause succeeds.
(function () {
    'use strict';

    // Pause only after the tab has been out of sight for a long stretch. A paused circuit
    // is rebuilt from scratch on resume, and this app annotates nothing with
    // [PersistentState] yet, so anything the user had set up in the UI - cookbook filters,
    // an open sidebar panel - comes back at its defaults. Five minutes keeps that away
    // from anyone actually switching between tabs, while still reclaiming the tabs people
    // leave open for hours, which is the case that costs memory.
    // Lower this (and annotate the page state) once the pages persist their own state.
    var HIDDEN_DELAY_MS = 300000;
    var pauseTimer = null;
    var paused = false;
    var resuming = false;

    function log(msg) {
        if (window.__circuitPauseDebug) {
            console.debug('[circuit-pause] ' + msg);
        }
    }

    function canPause() {
        return window.Blazor
            && typeof window.Blazor.pauseCircuit === 'function'
            && typeof window.Blazor.resumeCircuit === 'function';
    }

    // Drops MudBlazor's JS-side listeners whose DotNetObjectReferences died with the
    // paused circuit (see point 3 above). Only registries whose entries the resumed
    // circuit provably re-creates are purged: MudTabs re-runs Observe() on its fresh
    // firstRender, and the viewport service re-registers its window-resize listener the
    // same way. Everything is best-effort - these are MudBlazor globals (stable public
    // interop names, but not our code), and a purge failure must never break the pause.
    function purgeDeadDotNetListeners() {
        try {
            // window.mudResizeObserver: per-observer ResizeObservers (MudTabs' scroll
            // buttons / slider). cancelListener(id) disconnects the observer and voids
            // its dotNetRef, so a still-pending throttle timeout (cancelListener does
            // not clearTimeout - upstream flaw as of MudBlazor 9.8.0 and dev) lands in
            // resizeHandler's try/catch instead of a dead invokeMethodAsync.
            var ro = window.mudResizeObserver;
            if (ro && ro._maps) {
                Object.keys(ro._maps).forEach(function (id) {
                    try { ro.cancelListener(id); } catch (e) { /* best effort */ }
                });
                log('purged resize observers');
            }
        } catch (e) {
            log('resize observer purge threw: ' + e);
        }
        try {
            // window.mudResizeListenerFactory: window-resize listeners (breakpoint /
            // viewport service, RaiseOnResized) - same dead-reference class.
            var rlf = window.mudResizeListenerFactory;
            if (rlf && typeof rlf.dispose === 'function') {
                rlf.dispose();
                log('purged resize listeners');
            }
        } catch (e) {
            log('resize listener purge threw: ' + e);
        }
    }

    async function pauseNow() {
        pauseTimer = null;
        if (paused || resuming || !canPause() || document.visibilityState !== 'hidden') {
            return;
        }
        try {
            var ok = await window.Blazor.pauseCircuit();
            paused = ok === true;
            log(ok ? 'paused' : 'pause declined (not connected yet, or already paused)');
            if (paused) {
                purgeDeadDotNetListeners();
            }
        } catch (e) {
            log('pause threw: ' + e);
        }
    }

    async function resumeNow() {
        if (!paused || resuming || !canPause()) {
            return;
        }
        resuming = true;
        try {
            var ok = await window.Blazor.resumeCircuit();
            if (ok === true) {
                paused = false;
                log('resumed');
            } else {
                // Persisted state is gone (evicted or expired). The circuit cannot come
                // back; a reload is the only way to a working page.
                log('resume refused - reloading');
                window.location.reload();
            }
        } catch (e) {
            log('resume threw: ' + e + ' - reloading');
            window.location.reload();
        } finally {
            resuming = false;
        }
    }

    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'hidden') {
            if (pauseTimer === null) {
                pauseTimer = window.setTimeout(pauseNow, HIDDEN_DELAY_MS);
            }
        } else {
            if (pauseTimer !== null) {
                window.clearTimeout(pauseTimer);
                pauseTimer = null;
            }
            resumeNow();
        }
    });

    // A tab that is closing does not need its circuit kept warm for a minute.
    window.addEventListener('pagehide', function () {
        if (pauseTimer !== null) {
            window.clearTimeout(pauseTimer);
            pauseTimer = null;
        }
    });
})();
