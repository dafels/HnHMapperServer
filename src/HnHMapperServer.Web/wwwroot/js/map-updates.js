// Browser-based SSE (Server-Sent Events) client for map updates
// Uses native EventSource with automatic reconnection and cookie-based auth

let eventSource = null;
let dotnetRef = null;
let isConnecting = false;
let reconnectAttempts = 0;
const MAX_RECONNECT_DELAY = 5000; // 5 seconds max backoff

// Polling fallback configuration (for VPN users where SSE fails)
let isPollingMode = false;
let pollInterval = null;
let sseFailureCount = 0;
const SSE_FAILURE_THRESHOLD = 3;  // Switch to polling after 3 consecutive SSE failures
const POLL_INTERVAL_MS = 3000;    // Poll every 3 seconds

// Connection health monitoring
let lastEventTime = 0;
let healthCheckInterval = null;
const HEALTH_CHECK_INTERVAL = 8000;  // Check every 8 seconds
const CONNECTION_TIMEOUT = 12000;    // 12 seconds without data = unhealthy

// Track the highest tile cache token (T) we've seen.
//
// Why:
// - The server may (re)send a large "initial tile cache" payload on SSE connect.
// - If the browser tab is backgrounded, the SSE connection may drop and reconnect when the tab returns.
// - Re-sending the full tile cache (~300k entries) on each reconnect can freeze the browser.
// - We therefore reconnect with `?since=<lastTileCache>` to request only tiles updated since we last synced.
//
// Note:
// - This assumes tile cache tokens (T) are monotonically increasing in practice (timestamp-like).
let lastTileCache = 0;

// Track if initial data has been loaded via HTTP
let initialDataLoaded = false;

/**
 * Initialize map updates - fetches initial data via HTTP first, then tries SSE for real-time updates
 * @param {object} dotnetReference - DotNet object reference for callbacks
 * @returns {Promise<boolean>} - true if initialization successful
 */
export async function initializeSseUpdates(dotnetReference) {
    console.warn('[SSE] ===== initializeSseUpdates called =====');
    console.warn('[SSE] dotnetReference:', dotnetReference);
    dotnetRef = dotnetReference;

    // Reuse existing connection if it's still working (prevents closing on Blazor reconnect)
    if (eventSource) {
        if (eventSource.readyState === EventSource.OPEN) {
            console.warn('[SSE] EventSource already connected and open, reusing existing connection');
            return true;  // Reuse existing connection
        } else {
            console.warn('[SSE] Closing broken EventSource (readyState: ' + eventSource.readyState + ' - 0=CONNECTING, 1=OPEN, 2=CLOSED)');
            eventSource.close();
            eventSource = null;
        }
    }

    // If already in polling mode, just ensure polling is running
    if (isPollingMode) {
        console.warn('[SSE] Already in polling mode, ensuring polling is active');
        if (!pollInterval) startPolling();
        return true;
    }

    // STEP 1: Fetch initial data via HTTP immediately (doesn't depend on SSE)
    // This ensures the map loads even if SSE is blocked by VPN/proxy
    if (!initialDataLoaded) {
        console.warn('[HTTP] Fetching initial map data via HTTP...');
        try {
            await fetchInitialData();
            initialDataLoaded = true;
            console.warn('[HTTP] Initial data loaded successfully');
        } catch (error) {
            console.error('[HTTP] Failed to fetch initial data:', error);
            // Continue anyway - SSE might still work
        }
    }

    // STEP 2: Try to establish SSE connection for real-time updates
    console.warn('[SSE] Calling connectSse...');
    const result = connectSse();
    console.warn('[SSE] connectSse returned:', result);
    return result;
}

/**
 * Connect to SSE endpoint - browser automatically sends cookies
 */
function connectSse() {
    console.warn('[SSE] connectSse called - isConnecting:', isConnecting, 'eventSource:', eventSource);

    if (isConnecting || eventSource) {
        console.warn('[SSE] Already connecting or connected, returning false');
        return false;
    }

    isConnecting = true;

    try {
        // Build SSE URL. If we have previously processed any tile cache tokens, request only the
        // delta since that token. This prevents huge re-sync payloads (and browser freezes) on reconnect.
        let url = '/map/updates';
        if (typeof lastTileCache === 'number' && Number.isFinite(lastTileCache) && lastTileCache > 0) {
            url += `?since=${encodeURIComponent(String(lastTileCache))}`;
        }

        console.warn('[SSE] Creating new EventSource for', url);

        // EventSource automatically includes cookies for same-origin requests
        eventSource = new EventSource(url, {
            withCredentials: true
        });

        // Expose globally for notification-center.js to access
        window.mapUpdates = window.mapUpdates || {};
        window.mapUpdates.eventSource = eventSource;
        console.warn('[SSE] EventSource exposed as window.mapUpdates.eventSource');

        console.warn('[SSE] EventSource created successfully');
        console.warn('[SSE] EventSource.url:', eventSource.url);
        console.warn('[SSE] EventSource.readyState:', eventSource.readyState, '(0=CONNECTING, 1=OPEN, 2=CLOSED)');
        console.warn('[SSE] EventSource.withCredentials:', eventSource.withCredentials);

        // Wait a bit to see if readyState changes
        setTimeout(() => {
            console.warn('[SSE] EventSource readyState after 100ms:', eventSource?.readyState);
        }, 100);

        setTimeout(() => {
            console.warn('[SSE] EventSource readyState after 500ms:', eventSource?.readyState);
        }, 500);

        // Connection opened successfully
        eventSource.onopen = function () {
            console.warn('[SSE] ===== CONNECTED to /map/updates =====');
            isConnecting = false;
            reconnectAttempts = 0;
            sseFailureCount = 0;  // Reset failure count on successful connection
            lastEventTime = Date.now();
            startHealthCheck();

            // Notify Blazor of connection mode
            invokeDotNetSafe('OnConnectionModeChanged', 'sse');
        };

        // Default message handler (tile updates - batched every 5s)
        eventSource.onmessage = function (event) {
            lastEventTime = Date.now();  // Track last event for health monitoring
            try {
                // NOTE:
                // The server may stream a large initial tile cache as many smaller SSE messages.
                // Each message still contains a JSON array, so the logic here remains the same.
                const tiles = JSON.parse(event.data);
                if (tiles && Array.isArray(tiles) && tiles.length > 0) {
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
                        window.mapUpdates = window.mapUpdates || {};
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
                }
            } catch (e) {
                console.error('[SSE] Error parsing tile updates:', e);
            }
        };

        // Map merge event
        eventSource.addEventListener('merge', function (event) {
            try {
                const merge = JSON.parse(event.data);
                invokeDotNetSafe('OnSseMapMerge', merge);
            } catch (e) {
                console.error('[SSE] Error parsing merge event:', e);
            }
        });

        // Map metadata update event (name, hidden, priority)
        eventSource.addEventListener('mapUpdate', function (event) {
            try {
                const mapInfo = JSON.parse(event.data);
                invokeDotNetSafe('OnSseMapUpdated', mapInfo);
            } catch (e) {
                console.error('[SSE] Error parsing mapUpdate event:', e);
            }
        });

        // Map deletion event
        eventSource.addEventListener('mapDelete', function (event) {
            try {
                const deleteInfo = JSON.parse(event.data);
                invokeDotNetSafe('OnSseMapDeleted', deleteInfo.Id);
            } catch (e) {
                console.error('[SSE] Error parsing mapDelete event:', e);
            }
        });

        // Map revision event (cache busting)
        // Note: Server sends camelCase JSON (mapId, revision)
        eventSource.addEventListener('mapRevision', function (event) {
            try {
                const revision = JSON.parse(event.data);
                // Use Number.isFinite() to ensure values are valid finite numbers
                // This catches null, undefined, NaN, Infinity which would fail .NET deserialization
                if (revision &&
                    typeof revision.mapId === 'number' && Number.isFinite(revision.mapId) &&
                    typeof revision.revision === 'number' && Number.isFinite(revision.revision)) {
                    invokeDotNetSafe('OnSseMapRevision', revision.mapId, revision.revision);
                } else {
                    console.warn('[SSE] Ignoring mapRevision with invalid values:', revision);
                }
            } catch (e) {
                console.error('[SSE] Error parsing mapRevision event:', e);
            }
        });

        // Custom marker created event
        eventSource.addEventListener('customMarkerCreated', function (event) {
            try {
                const marker = JSON.parse(event.data);
                invokeDotNetSafe('OnCustomMarkerCreated', marker);
            } catch (e) {
                console.error('[SSE] Error parsing customMarkerCreated event:', e);
            }
        });

        // Custom marker updated event
        eventSource.addEventListener('customMarkerUpdated', function (event) {
            try {
                const marker = JSON.parse(event.data);
                invokeDotNetSafe('OnCustomMarkerUpdated', marker);
            } catch (e) {
                console.error('[SSE] Error parsing customMarkerUpdated event:', e);
            }
        });

        // Custom marker deleted event
        eventSource.addEventListener('customMarkerDeleted', function (event) {
            try {
                const deleteInfo = JSON.parse(event.data);
                invokeDotNetSafe('OnCustomMarkerDeleted', deleteInfo);
            } catch (e) {
                console.error('[SSE] Error parsing customMarkerDeleted event:', e);
            }
        });

        // Game marker created event
        eventSource.addEventListener('markerCreated', function (event) {
            try {
                const marker = JSON.parse(event.data);
                invokeDotNetSafe('OnMarkerCreated', marker);
            } catch (e) {
                console.error('[SSE] Error parsing markerCreated event:', e);
            }
        });

        // Game marker updated event
        eventSource.addEventListener('markerUpdated', function (event) {
            try {
                const marker = JSON.parse(event.data);
                invokeDotNetSafe('OnMarkerUpdated', marker);
            } catch (e) {
                console.error('[SSE] Error parsing markerUpdated event:', e);
            }
        });

        // Game marker deleted event
        eventSource.addEventListener('markerDeleted', function (event) {
            try {
                const deleteInfo = JSON.parse(event.data);
                invokeDotNetSafe('OnMarkerDeleted', deleteInfo);
            } catch (e) {
                console.error('[SSE] Error parsing markerDeleted event:', e);
            }
        });

        // Ping created event
        eventSource.addEventListener('pingCreated', function (event) {
            try {
                const ping = JSON.parse(event.data);
                invokeDotNetSafe('OnPingCreated', ping);
            } catch (e) {
                console.error('[SSE] Error parsing pingCreated event:', e);
            }
        });

        // Ping deleted event
        eventSource.addEventListener('pingDeleted', function (event) {
            try {
                const deleteInfo = JSON.parse(event.data);
                invokeDotNetSafe('OnPingDeleted', deleteInfo);
            } catch (e) {
                console.error('[SSE] Error parsing pingDeleted event:', e);
            }
        });

        // Timer created event
        eventSource.addEventListener('timerCreated', function (event) {
            try {
                const timer = JSON.parse(event.data);
                invokeDotNetSafe('OnTimerCreated', timer);
            } catch (e) {
                console.error('[SSE] Error parsing timerCreated event:', e);
            }
        });

        // Timer updated event
        eventSource.addEventListener('timerUpdated', function (event) {
            try {
                const timer = JSON.parse(event.data);
                invokeDotNetSafe('OnTimerUpdated', timer);
            } catch (e) {
                console.error('[SSE] Error parsing timerUpdated event:', e);
            }
        });

        // Timer completed event
        eventSource.addEventListener('timerCompleted', function (event) {
            try {
                const timer = JSON.parse(event.data);
                invokeDotNetSafe('OnTimerCompleted', timer);
            } catch (e) {
                console.error('[SSE] Error parsing timerCompleted event:', e);
            }
        });

        // Timer deleted event
        eventSource.addEventListener('timerDeleted', function (event) {
            try {
                const deleteInfo = JSON.parse(event.data);
                invokeDotNetSafe('OnTimerDeleted', deleteInfo.Id);
            } catch (e) {
                console.error('[SSE] Error parsing timerDeleted event:', e);
            }
        });

        // Road created event
        eventSource.addEventListener('roadCreated', function (event) {
            try {
                const road = JSON.parse(event.data);
                invokeDotNetSafe('OnRoadCreated', road);
            } catch (e) {
                console.error('[SSE] Error parsing roadCreated event:', e);
            }
        });

        // Road updated event
        eventSource.addEventListener('roadUpdated', function (event) {
            try {
                const road = JSON.parse(event.data);
                invokeDotNetSafe('OnRoadUpdated', road);
            } catch (e) {
                console.error('[SSE] Error parsing roadUpdated event:', e);
            }
        });

        // Road deleted event
        eventSource.addEventListener('roadDeleted', function (event) {
            try {
                const deleteInfo = JSON.parse(event.data);
                invokeDotNetSafe('OnRoadDeleted', deleteInfo);
            } catch (e) {
                console.error('[SSE] Error parsing roadDeleted event:', e);
            }
        });

        // Overlay updated event
        eventSource.addEventListener('overlayUpdated', function (event) {
            try {
                const overlay = JSON.parse(event.data);
                invokeDotNetSafe('OnOverlayUpdated', overlay);
            } catch (e) {
                console.error('[SSE] Error parsing overlayUpdated event:', e);
            }
        });

        // Characters snapshot event (initial full state)
        eventSource.addEventListener('charactersSnapshot', function (event) {
            try {
                const characters = JSON.parse(event.data);
                invokeDotNetSafe('OnSseCharactersSnapshot', characters);
            } catch (e) {
                console.error('[SSE] Error parsing charactersSnapshot event:', e);
            }
        });

        // Character delta event (incremental updates)
        eventSource.addEventListener('characterDelta', function (event) {
            try {
                const delta = JSON.parse(event.data);
                invokeDotNetSafe('OnSseCharacterDelta', delta);
            } catch (e) {
                console.error('[SSE] Error parsing characterDelta event:', e);
            }
        });

        // Error handler - EventSource auto-reconnects, but we track attempts
        eventSource.onerror = function (error) {
            console.error('[SSE] ===== CONNECTION ERROR =====');
            console.error('[SSE] Error event:', error);
            console.error('[SSE] EventSource readyState:', eventSource?.readyState);
            console.error('[SSE] ReadyState meanings: 0=CONNECTING, 1=OPEN, 2=CLOSED');
            isConnecting = false;

            // Track consecutive failures
            reconnectAttempts++;
            sseFailureCount++;
            console.error('[SSE] Reconnect attempt:', reconnectAttempts, 'SSE failure count:', sseFailureCount);

            // Switch to polling mode if SSE consistently fails (VPN/proxy blocking)
            if (sseFailureCount >= SSE_FAILURE_THRESHOLD) {
                console.warn('[SSE] Too many failures (' + sseFailureCount + '), switching to polling mode');
                switchToPollingMode();
                return;
            }

            // If auth fails (401), EventSource will keep retrying
            // The server-side fix ensures cookies work, so this should resolve
        };

        return true;
    } catch (e) {
        console.error('[SSE] Failed to create EventSource:', e);
        isConnecting = false;
        scheduleManualReconnect();
        return false;
    }
}

/**
 * Manual reconnect with exponential backoff (fallback if EventSource fails to auto-reconnect)
 */
function scheduleManualReconnect() {
    const baseDelay = reconnectAttempts === 0 ? 1000 : Math.min(reconnectAttempts * 1000, MAX_RECONNECT_DELAY);
    const jitter = Math.random() * 200 - 100; // ±100ms jitter
    const delay = Math.max(500, baseDelay + jitter);

    console.debug(`[SSE] Scheduling manual reconnect in ${delay}ms`);

    setTimeout(() => {
        // Clean up closed EventSource before reconnecting
        if (eventSource && eventSource.readyState === EventSource.CLOSED) {
            eventSource.close(); // Ensure cleanup
            eventSource = null;
        }

        if (!eventSource) {
            connectSse();
        }
    }, delay);
}

/**
 * Disconnect SSE stream
 */
export function disconnectSseUpdates() {
    if (eventSource) {
        console.debug('[SSE] Disconnecting');
        eventSource.close();
        eventSource = null;
    }
    isConnecting = false;
    reconnectAttempts = 0;
}

/**
 * Safely invoke .NET methods from JS; silently drop calls during Blazor disconnects.
 * Does NOT retry - SSE will send fresh data when circuit reconnects.
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
        // Silently drop, don't retry (fresh SSE data will arrive)
        return null;
    }
}

/**
 * Check if SSE is currently connected
 * @returns {boolean}
 */
export function isSseConnected() {
    return eventSource !== null && eventSource.readyState === EventSource.OPEN;
}

/**
 * Get current connection mode
 * @returns {string} 'sse', 'polling', 'connecting', or 'disconnected'
 */
export function getConnectionMode() {
    if (isPollingMode) return 'polling';
    if (eventSource && eventSource.readyState === EventSource.OPEN) return 'sse';
    if (isConnecting) return 'connecting';
    return 'disconnected';
}

/**
 * Start connection health monitoring
 * Detects stale connections and forces reconnect
 */
function startHealthCheck() {
    stopHealthCheck();
    healthCheckInterval = setInterval(() => {
        const timeSinceLastEvent = Date.now() - lastEventTime;
        if (timeSinceLastEvent > CONNECTION_TIMEOUT && eventSource) {
            console.warn('[SSE] Connection appears stale (no events for ' + Math.round(timeSinceLastEvent / 1000) + 's), forcing reconnect');
            sseFailureCount++;

            if (sseFailureCount >= SSE_FAILURE_THRESHOLD) {
                console.warn('[SSE] Too many stale connections, switching to polling mode');
                switchToPollingMode();
            } else {
                // Force reconnect
                if (eventSource) {
                    eventSource.close();
                    eventSource = null;
                }
                scheduleManualReconnect();
            }
        }
    }, HEALTH_CHECK_INTERVAL);
}

/**
 * Stop health check interval
 */
function stopHealthCheck() {
    if (healthCheckInterval) {
        clearInterval(healthCheckInterval);
        healthCheckInterval = null;
    }
}

/**
 * Switch to polling mode when SSE consistently fails
 */
function switchToPollingMode() {
    if (isPollingMode) return;

    console.log('[POLL] Switching to polling mode');
    isPollingMode = true;

    // Clean up SSE connection
    stopHealthCheck();
    if (eventSource) {
        eventSource.close();
        eventSource = null;
    }
    isConnecting = false;

    // Notify Blazor of mode change
    invokeDotNetSafe('OnConnectionModeChanged', 'polling');

    // Start polling
    startPolling();
}

/**
 * Start polling interval
 */
function startPolling() {
    stopPolling();

    // Initial poll immediately
    poll();

    // Regular polling
    pollInterval = setInterval(poll, POLL_INTERVAL_MS);
}

/**
 * Stop polling interval
 */
function stopPolling() {
    if (pollInterval) {
        clearInterval(pollInterval);
        pollInterval = null;
    }
}

/**
 * Fetch initial map data via HTTP (called before SSE connection attempt)
 * This ensures the map loads immediately even if SSE is blocked
 */
async function fetchInitialData() {
    const response = await fetch('/map/api/v1/poll', {
        credentials: 'include'
    });

    if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
    }

    const data = await response.json();

    // Process tiles (initial full load)
    if (data.tiles && data.tiles.length > 0) {
        console.log('[HTTP] Received', data.tiles.length, 'tiles');

        // Update lastTileCache
        for (const t of data.tiles) {
            const cache = t.T ?? t.t;
            if (typeof cache === 'number' && Number.isFinite(cache) && cache > lastTileCache) {
                lastTileCache = cache;
            }
        }

        // Apply tile updates
        try {
            const fastApply = window?.hnhMapper?.applyTileUpdates;
            if (typeof fastApply === 'function') {
                fastApply(data.tiles);
            } else {
                invokeDotNetSafe('OnSseTileUpdates', data.tiles);
            }
        } catch (e) {
            console.error('[HTTP] Error applying tile updates:', e);
        }
    }

    // Process characters
    if (data.characters) {
        console.log('[HTTP] Received', data.characters.length, 'characters');
        invokeDotNetSafe('OnSseCharactersSnapshot', data.characters);
    }

    // Process map revisions
    if (data.mapRevisions) {
        for (const [mapId, revision] of Object.entries(data.mapRevisions)) {
            const mapIdNum = parseInt(mapId, 10);
            if (Number.isFinite(mapIdNum) && Number.isFinite(revision)) {
                invokeDotNetSafe('OnSseMapRevision', mapIdNum, revision);
            }
        }
    }
}

/**
 * Poll the server for updates (fallback when SSE fails)
 */
async function poll() {
    try {
        const params = new URLSearchParams();
        if (typeof lastTileCache === 'number' && Number.isFinite(lastTileCache) && lastTileCache > 0) {
            params.set('since', String(lastTileCache));
        }

        const response = await fetch(`/map/api/v1/poll?${params.toString()}`, {
            credentials: 'include'
        });

        if (!response.ok) {
            console.error('[POLL] Request failed:', response.status);
            return;
        }

        const data = await response.json();

        // Process tiles
        if (data.tiles && data.tiles.length > 0) {
            // Update lastTileCache
            for (const t of data.tiles) {
                const cache = t.T ?? t.t;
                if (typeof cache === 'number' && Number.isFinite(cache) && cache > lastTileCache) {
                    lastTileCache = cache;
                }
            }

            // Apply tile updates
            try {
                const fastApply = window?.hnhMapper?.applyTileUpdates;
                if (typeof fastApply === 'function') {
                    fastApply(data.tiles);
                } else {
                    invokeDotNetSafe('OnSseTileUpdates', data.tiles);
                }
            } catch (e) {
                console.error('[POLL] Error applying tile updates:', e);
            }
        }

        // Process characters as snapshot
        if (data.characters) {
            invokeDotNetSafe('OnSseCharactersSnapshot', data.characters);
        }

        // Process map revisions
        if (data.mapRevisions) {
            for (const [mapId, revision] of Object.entries(data.mapRevisions)) {
                const mapIdNum = parseInt(mapId, 10);
                if (Number.isFinite(mapIdNum) && Number.isFinite(revision)) {
                    invokeDotNetSafe('OnSseMapRevision', mapIdNum, revision);
                }
            }
        }

    } catch (error) {
        console.error('[POLL] Error:', error);
    }
}

/**
 * Retry SSE connection from polling mode
 * @returns {boolean} true if retry initiated
 */
export function retrySseConnection() {
    if (!isPollingMode) return false;

    console.log('[SSE] Retrying SSE connection from polling mode');
    stopPolling();
    isPollingMode = false;
    sseFailureCount = 0;
    reconnectAttempts = 0;

    return connectSse();
}
