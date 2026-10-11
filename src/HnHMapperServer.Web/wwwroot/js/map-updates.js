// Browser-side SSE client for /map/updates (live tiles, characters, markers, map events).
//
// Connection contract (server side: MapEndpoints.WatchGridUpdates + Core/Constants/SseFrames.cs):
// - The server sends a NAMED `heartbeat` event when its live loop starts and then every ~5 s,
//   unconditionally. It must stay a named event: EventSource never surfaces `: comment` lines to
//   JavaScript, which is why the old `: keep-alive` comment let the watchdog below kill every quiet
//   stream about every 17 s.
// - Every delivered event (heartbeat, tile batch, any named event) proves the stream is alive.
// - Reconnects are always manual, never the browser's native retry: the URL carries `?since=`, which
//   has to be rebuilt from the newest tile token on every attempt (native retry replays the old URL).
// - The server ends every stream after ~15 min (±20%) so authorization is re-evaluated; a healthy
//   stream's end reconnects after ~1 s with a fresh snapshot.
// - There is no polling fallback. A stream that keeps failing is retried forever
//   with backoff, and after a few unhealthy connections Map.razor shows "Live updates reconnecting…".

// SseFrames.HeartbeatEvent
const HEARTBEAT_EVENT = 'heartbeat';

// Watchdog: one tick every 5 s; a stream with no event for 20 s (four missed heartbeats) is dead.
const HEALTH_CHECK_INTERVAL = 5000;
const CONNECTION_TIMEOUT = 20000;

// Reconnect backoff: 1 s doubling to 30 s, ±20% jitter, retried forever.
const RECONNECT_BASE_DELAY = 1000;
const MAX_RECONNECT_DELAY = 30000;

// Consecutive unhealthy connections before Map.razor shows the "reconnecting" indicator.
const UNHEALTHY_NOTIFY_THRESHOLD = 3;

let eventSource = null;
let dotnetRef = null;

let reconnectTimer = null;
let reconnectAttempts = 0;      // backoff exponent; reset by a healthy connection
let unhealthyStreak = 0;        // consecutive connections that ended before turning healthy
let reconnectingShown = false;  // .NET was told to show the indicator

let healthCheckInterval = null;
let lastEventTime = 0;
let lastTick = 0;
let connectedAt = 0;
let sawHeartbeat = false;
let connectionHealthy = false;

// Track the highest tile cache token (T) we've seen.
//
// Why:
// - The server (re)sends the tile cache on every connect: the whole list without `since`.
// - Re-sending the full tile cache (~300k entries) on each reconnect can freeze the browser.
// - We therefore reconnect with `?since=<lastTileCache>` to request only tiles updated since we last synced.
// - Kept across disconnectSseUpdates (SPA exit, circuit pause) on purpose, so re-entry asks for the delta.
//
// Note:
// - This assumes tile cache tokens (T) are monotonically increasing in practice (timestamp-like).
let lastTileCache = 0;

// Fallback-path dedupe for mapRevision (see applyMapRevision): mapId -> last revision sent to .NET.
const forwardedRevisions = new Map();

// Named events forwarded to .NET as parsed JSON, unchanged.
const FORWARDED_EVENTS = {
    merge: 'OnSseMapMerge',
    mapUpdate: 'OnSseMapUpdated',            // upsert: full map item, incl. newly created maps
    customMarkerCreated: 'OnCustomMarkerCreated',
    customMarkerUpdated: 'OnCustomMarkerUpdated',
    customMarkerDeleted: 'OnCustomMarkerDeleted',
    markerCreated: 'OnMarkerCreated',
    markerUpdated: 'OnMarkerUpdated',
    markerDeleted: 'OnMarkerDeleted',
    pingCreated: 'OnPingCreated',
    pingDeleted: 'OnPingDeleted',
    timerCreated: 'OnTimerCreated',
    timerUpdated: 'OnTimerUpdated',
    timerCompleted: 'OnTimerCompleted',
    roadCreated: 'OnRoadCreated',
    roadUpdated: 'OnRoadUpdated',
    roadDeleted: 'OnRoadDeleted',
    overlayUpdated: 'OnOverlayUpdated',
    charactersSnapshot: 'OnSseCharactersSnapshot',   // full state, sent only at connect
    characterDelta: 'OnSseCharacterDelta'
};

// circuit-pause.js (a classic script, it cannot import this module) calls this after a successful
// pause: the paused circuit's dotnetRef is dead, so the stream would only feed calls nobody receives.
// The resumed Map component calls initializeSseUpdates again.
try {
    window.mapUpdates = window.mapUpdates || {};
    window.mapUpdates.suspend = disconnectSseUpdates;
} catch { /* ignore - non-browser environment */ }

/**
 * Start (or restart) live map updates.
 *
 * Always opens a fresh stream, even if one is open: the server sends charactersSnapshot and the
 * mapRevision replay only at connect, and a re-initialised component (circuit reconnect, resume after
 * a pause, SPA re-entry) starts with empty character state.
 *
 * @param {object} dotnetReference - DotNet object reference for callbacks
 * @returns {boolean} - true if the EventSource was created
 */
export function initializeSseUpdates(dotnetReference) {
    dotnetRef = dotnetReference;
    forwardedRevisions.clear();
    // The .NET side starts without the indicator (fresh component, or Map.razor cleared it on
    // circuit-up); a stream that is still failing raises it again on its next unhealthy loss.
    // reconnectAttempts/unhealthyStreak deliberately carry over (disconnectSseUpdates resets them):
    // a re-init during an outage must not restart the backoff from 1 s.
    reconnectingShown = false;

    clearReconnectTimer();
    closeEventSource();
    return connectSse();
}

/**
 * Stop everything this module started: the stream, the watchdog and any pending reconnect, and drop
 * the .NET reference (the caller disposes it). Called from Map.razor's DisposeAsync (leaving /map) and
 * by circuit-pause.js. lastTileCache is kept so the next initializeSseUpdates asks only for the delta.
 */
export function disconnectSseUpdates() {
    clearReconnectTimer();
    closeEventSource();
    dotnetRef = null;
    reconnectAttempts = 0;
    unhealthyStreak = 0;
    reconnectingShown = false;
    forwardedRevisions.clear();
}

/**
 * Check if SSE is currently connected
 * @returns {boolean}
 */
export function isSseConnected() {
    return eventSource !== null && eventSource.readyState === EventSource.OPEN;
}

/**
 * Open the stream. The browser sends the auth cookie itself (same origin).
 */
function connectSse() {
    if (eventSource) return true;

    // Rebuilt on every attempt: ask only for tiles newer than the newest token we have.
    let url = '/map/updates';
    if (Number.isFinite(lastTileCache) && lastTileCache > 0) {
        url += `?since=${encodeURIComponent(String(lastTileCache))}`;
    }

    let es;
    try {
        es = new EventSource(url, { withCredentials: true });
    } catch (e) {
        console.error('[SSE] Failed to create EventSource:', e);
        onConnectionEnded(false, 'create failed');
        return false;
    }

    eventSource = es;
    try {
        window.mapUpdates.eventSource = es; // debugging aid (DevTools)
    } catch { /* ignore */ }

    connectedAt = Date.now();
    lastEventTime = connectedAt;
    sawHeartbeat = false;
    connectionHealthy = false;
    // Armed at connect, not at onopen, so a request stuck in CONNECTING is caught too.
    startHealthCheck();

    // Every handler ignores events of a stream that has since been replaced or closed.
    const listen = (name, handler) => es.addEventListener(name, (event) => {
        if (es !== eventSource) return;
        markAlive();
        handler(event);
    });

    es.onopen = () => {
        if (es !== eventSource) return;
        console.debug('[SSE] Connected to', url);
        markAlive();
    };

    // Set before markAlive so the heartbeat that completes the health window is counted at once.
    es.addEventListener(HEARTBEAT_EVENT, () => {
        if (es !== eventSource) return;
        sawHeartbeat = true;
        markAlive();
    });

    // Default (unnamed) messages are tile batches: the initial tile cache in chunks, then a batch every 10 s.
    es.onmessage = (event) => {
        if (es !== eventSource) return;
        markAlive();
        applyTileBatch(event.data);
    };

    for (const [name, method] of Object.entries(FORWARDED_EVENTS)) {
        listen(name, (event) => {
            const payload = parseEventData(name, event);
            if (payload !== undefined) {
                invokeDotNetSafe(method, payload);
            }
        });
    }

    // Map deletion event. Server sends camelCase JSON ({ id: ... })
    listen('mapDelete', (event) => {
        const deleteInfo = parseEventData('mapDelete', event);
        if (deleteInfo === undefined) return;
        const id = deleteInfo?.id ?? deleteInfo?.Id;
        if (typeof id === 'number' && Number.isFinite(id)) {
            invokeDotNetSafe('OnSseMapDeleted', id);
        } else {
            console.warn('[SSE] Ignoring mapDelete with invalid id:', deleteInfo);
        }
    });

    // Timer deletion event. Server sends camelCase JSON ({ id: ... })
    listen('timerDeleted', (event) => {
        const deleteInfo = parseEventData('timerDeleted', event);
        if (deleteInfo === undefined) return;
        const id = deleteInfo?.id ?? deleteInfo?.Id;
        if (typeof id === 'number' && Number.isFinite(id)) {
            invokeDotNetSafe('OnTimerDeleted', id);
        } else {
            console.warn('[SSE] Ignoring timerDeleted with invalid id:', deleteInfo);
        }
    });

    // Map revision event (tile cache busting). Server sends camelCase JSON ({ mapId, revision }, plus
    // `cells` - the regenerated zoom-0 cells - on ZoomQ bumps). Every connect replays all of the
    // tenant's revisions (without cells), so most of these are duplicates.
    listen('mapRevision', (event) => {
        const revision = parseEventData('mapRevision', event);
        if (revision === undefined) return;
        if (revision && Number.isInteger(revision.mapId) && Number.isInteger(revision.revision)) {
            applyMapRevision(revision.mapId, revision.revision, Array.isArray(revision.cells) ? revision.cells : null);
        } else {
            console.warn('[SSE] Ignoring mapRevision with invalid values:', revision);
        }
    });

    // Any error, in any readyState: close and reconnect ourselves. Left alone, EventSource would retry
    // its original URL (stale ?since=) or give up for good (CLOSED after a non-200 answer).
    es.onerror = () => {
        if (es !== eventSource) return;
        handleConnectionLost('error');
    };

    return true;
}

/**
 * Parse an event's JSON payload; logs and returns undefined when it is not valid JSON.
 */
function parseEventData(name, event) {
    try {
        return JSON.parse(event.data);
    } catch (e) {
        console.error(`[SSE] Error parsing ${name} event:`, e);
        return undefined;
    }
}

/**
 * Apply one default-message tile batch.
 */
function applyTileBatch(data) {
    try {
        const tiles = JSON.parse(data);
        if (!Array.isArray(tiles) || tiles.length === 0) return;

        // Update last seen cache token BEFORE applying (so reconnect uses a fresh `since` marker).
        // We intentionally do this in a tight loop (no allocations) for speed.
        // Accept both `T` and `t` for robustness.
        for (let i = 0; i < tiles.length; i++) {
            const t = tiles[i];
            const cache = t?.T ?? t?.t;
            if (typeof cache === 'number' && Number.isFinite(cache) && cache > lastTileCache) {
                lastTileCache = cache;
            }
        }

        // Expose for debugging (helps confirm reconnect delta behavior in DevTools).
        try {
            window.mapUpdates.lastTileCache = lastTileCache;
        } catch { /* ignore */ }

        // FAST PATH:
        // Apply tile updates directly in JS (Leaflet) to avoid JS -> .NET -> JS per-tile roundtrips.
        // This is critical to prevent UI freezes when the tab returns to foreground and a burst of
        // updates arrives at once.
        try {
            const fastApply = window?.hnhMapper?.applyTileUpdates;
            if (typeof fastApply === 'function') {
                fastApply(tiles);
                return;
            }
        } catch {
            // If anything goes wrong, fall back to the legacy .NET path.
        }

        // Fallback: legacy path (JS -> .NET). Kept for backwards compatibility / early init cases.
        invokeDotNetSafe('OnSseTileUpdates', tiles);
    } catch (e) {
        console.error('[SSE] Error parsing tile updates:', e);
    }
}

/**
 * Revisions stay in the browser: leaflet-interop.js owns the store (window.hnhMapper.setMapRevision,
 * registered by initializeMap) and drops exact duplicates itself; it also decides whether `cells` may
 * narrow the refresh. The .NET round trip is only a fallback for when that bridge is missing (it
 * drops the cells: a full refresh); it is deduped here and again in Map.razor.cs.
 */
function applyMapRevision(mapId, revision, cells) {
    try {
        const setRevision = window?.hnhMapper?.setMapRevision;
        if (typeof setRevision === 'function') {
            setRevision(mapId, revision, cells);
            return;
        }
    } catch {
        // Fall through to the .NET path.
    }

    if (forwardedRevisions.get(mapId) === revision) return;
    forwardedRevisions.set(mapId, revision);
    invokeDotNetSafe('OnSseMapRevision', mapId, revision);
}

/**
 * Any event proves the stream is alive.
 */
function markAlive() {
    lastEventTime = Date.now();
    checkHealthy(lastEventTime);
}

/**
 * A connection counts as healthy only once it delivered a heartbeat (the server's live loop runs and
 * the proxy chain streams) AND stayed up for CONNECTION_TIMEOUT. Only then are the backoff and the
 * unhealthy streak reset - never at onopen or on the first byte, so a stream that dies in its snapshot
 * phase, or sits behind a buffering proxy, still backs off and raises the indicator.
 */
function checkHealthy(now) {
    if (connectionHealthy || !sawHeartbeat || now - connectedAt < CONNECTION_TIMEOUT) return;

    connectionHealthy = true;
    reconnectAttempts = 0;
    unhealthyStreak = 0;
    if (reconnectingShown) {
        reconnectingShown = false;
        console.info('[SSE] Live updates restored');
        invokeDotNetSafe('OnLiveUpdatesReconnecting', false);
    }
}

/**
 * The current stream failed (error event or watchdog): close it and schedule the next attempt.
 */
function handleConnectionLost(reason) {
    const wasHealthy = connectionHealthy;
    closeEventSource();
    onConnectionEnded(wasHealthy, reason);
}

function onConnectionEnded(wasHealthy, reason) {
    if (wasHealthy) {
        // A long-lived stream dropped (lifetime bound, deploy, proxy reload, network blip): come straight back.
        reconnectAttempts = 0;
        unhealthyStreak = 0;
    } else {
        unhealthyStreak++;
        if (unhealthyStreak >= UNHEALTHY_NOTIFY_THRESHOLD && !reconnectingShown) {
            reconnectingShown = true;
            invokeDotNetSafe('OnLiveUpdatesReconnecting', true);
        }
    }
    scheduleReconnect(reason);
}

/**
 * Exponential backoff with jitter (1 s, 2 s, 4 s ... 30 s), forever. Jitter spreads the reconnects of
 * every open tab after a deploy instead of landing them on the API in the same instant.
 */
function scheduleReconnect(reason) {
    clearReconnectTimer();
    if (!dotnetRef) return; // disconnected or suspended: nobody to deliver to

    const base = Math.min(RECONNECT_BASE_DELAY * Math.pow(2, reconnectAttempts), MAX_RECONNECT_DELAY);
    reconnectAttempts = Math.min(reconnectAttempts + 1, 10);
    const delay = Math.round(base * (0.8 + Math.random() * 0.4));

    const message = `[SSE] Stream lost (${reason}), reconnecting in ${delay} ms (unhealthy streak ${unhealthyStreak})`;
    if (unhealthyStreak > 0) {
        console.warn(message);
    } else {
        console.debug(message);
    }

    reconnectTimer = setTimeout(() => {
        reconnectTimer = null;
        if (dotnetRef && !eventSource) {
            connectSse();
        }
    }, delay);
}

function clearReconnectTimer() {
    if (reconnectTimer !== null) {
        clearTimeout(reconnectTimer);
        reconnectTimer = null;
    }
}

function closeEventSource() {
    stopHealthCheck();
    if (eventSource) {
        try {
            eventSource.close();
        } catch { /* ignore */ }
        eventSource = null;
    }
    connectionHealthy = false;
    sawHeartbeat = false;
    try {
        window.mapUpdates.eventSource = null;
    } catch { /* ignore */ }
}

/**
 * Watchdog. Runs from connect until the stream is closed.
 */
function startHealthCheck() {
    stopHealthCheck();
    lastTick = Date.now();
    healthCheckInterval = setInterval(healthTick, HEALTH_CHECK_INTERVAL);
}

function healthTick() {
    const now = Date.now();
    // Time this timer itself did not run is not the stream's fault: hidden tabs clamp timers to about
    // once a minute and frozen tabs run nothing, while events queued meanwhile are dispatched after
    // this tick. So the allowance grows with the lateness and the watchdog still works, just slower.
    const late = Math.max(0, now - lastTick - HEALTH_CHECK_INTERVAL);
    lastTick = now;
    if (!eventSource) return;

    const silentFor = now - lastEventTime;
    if (silentFor > CONNECTION_TIMEOUT + late) {
        console.warn(`[SSE] No event for ${Math.round(silentFor / 1000)} s, reconnecting`);
        handleConnectionLost('stale');
    }
}

function stopHealthCheck() {
    if (healthCheckInterval !== null) {
        clearInterval(healthCheckInterval);
        healthCheckInterval = null;
    }
}

/**
 * Safely invoke .NET methods from JS; silently drop calls during Blazor disconnects.
 * Does NOT retry - every reconnect of the stream re-sends the state that matters (snapshot, revisions).
 */
function invokeDotNetSafe(method, ...args) {
    // Quick bail-out checks
    if (!dotnetRef || typeof dotnetRef.invokeMethodAsync !== 'function') {
        return null;
    }

    try {
        const promise = dotnetRef.invokeMethodAsync(method, ...args);

        // Silently swallow all promise rejections - circuit may be disconnecting
        if (promise && typeof promise.catch === 'function') {
            promise.catch(() => {
                // Intentionally empty - drop errors silently during circuit transitions
            });
        }

        return promise;
    } catch (e) {
        // Synchronous throw from invokeMethodAsync - circuit not ready
        return null;
    }
}
