// Leaflet Interop Module for Blazor
// Main orchestrator that coordinates specialized managers

// Import modules
import { TileSize, HnHMaxZoom, HnHMinZoom, HnHCRS } from './map/leaflet-config.js';
import { SmartTileLayer, setParentPlaceholder, clearPlaceholder } from './map/smart-tile-layer.js';
import * as CharacterManager from './map/character-manager.js';
import * as MarkerManager from './map/marker-manager.js';
import * as CustomMarkerManager from './map/custom-marker-manager.js';
import * as PingManager from './map/ping-manager.js';
import * as OverlayLayer from './map/overlay-layer.js';
import * as RoadManager from './map/road-manager.js';

// Grid Coordinate Layer
L.GridLayer.GridCoord = L.GridLayer.extend({
    createTile: function (coords) {
        const element = document.createElement("div");
        element.style.width = `${TileSize}px`;
        element.style.height = `${TileSize}px`;
        element.className = "map-tile";

        const scaleFactor = Math.pow(2, HnHMaxZoom - coords.z);
        const topLeft = { x: coords.x * scaleFactor, y: coords.y * scaleFactor };
        const bottomRight = { x: topLeft.x + scaleFactor - 1, y: topLeft.y + scaleFactor - 1 };

        let text = `(${topLeft.x};${topLeft.y})`;
        if (scaleFactor !== 1) {
            text += `<br>(${bottomRight.x};${bottomRight.y})`;
        }

        const textElement = document.createElement("div");
        textElement.className = "map-tile-text";
        textElement.innerHTML = text;
        element.appendChild(textElement);

        return element;
    }
});

// Global state
let mapInstance = null;
let dotnetRef = null;
let mainLayer = null;
let overlayLayer = null;
let coordLayer = null;
let markerLayer = null;
let detailedMarkerLayer = null;
let customMarkerLayer = null;
let roadLayer = null;
let currentMapId = 0;

// Debounce timer for zoom operations to reduce excessive tile requests during rapid zoom
let zoomDebounceTimer = null;
const ZOOM_DEBOUNCE_MS = 1500; // 1.5 seconds

// Safely invoke .NET methods from JS; ignore calls during Blazor reconnects
// Handles "No interop methods registered" error that occurs during circuit initialization
function invokeDotNetSafe(method, ...args) {
    try {
        if (!dotnetRef) {
            console.debug('[Leaflet] dotnetRef is null, dropping call:', method);
            return null;
        }

        if (typeof dotnetRef.invokeMethodAsync !== 'function') {
            console.debug('[Leaflet] invokeMethodAsync not available yet, dropping call:', method);
            return null;
        }

        // Additional check: Try to detect if we're too early in initialization
        // This prevents "Cannot send data if the connection is not in the 'Connected' State" errors
        try {
            // Call the method and handle Promise rejections
            const promise = dotnetRef.invokeMethodAsync(method, ...args);

            if (!promise || typeof promise.catch !== 'function') {
                console.debug('[Leaflet] invokeMethodAsync did not return a Promise, dropping:', method);
                return null;
            }

            return promise
                .then(result => {
                    console.log('[Leaflet] JS->.NET call succeeded:', method);
                    return result;
                })
                .catch(err => {
                // Specifically catch "No interop methods are registered for renderer X" error
                // This happens when map events fire before Blazor circuit finishes initialization
                if (err && err.message) {
                    if (err.message.includes('No interop methods')) {
                        console.warn('[Leaflet] Circuit not ready, dropping call:', method);
                        return null;
                    }
                    if (err.message.includes('Cannot send data') || err.message.includes('not in the') || err.message.includes('Connected')) {
                        console.warn('[Leaflet] SignalR not connected yet, dropping call:', method);
                        return null;
                    }
                }
                // For other errors, log as error so we see them
                console.error('[Leaflet] Error during JS->.NET call:', method, err.message || err, err);
                return null;
            });
        } catch (invokeError) {
            // Catch synchronous errors from invokeMethodAsync itself
            // This handles "Cannot send data" errors that throw before returning a Promise
            if (invokeError && invokeError.message &&
                (invokeError.message.includes('Cannot send data') ||
                 invokeError.message.includes('not in the') ||
                 invokeError.message.includes('Connected'))) {
                console.debug('[Leaflet] SignalR not connected (sync error), dropping call:', method);
                return null;
            }
            throw invokeError; // Re-throw unexpected errors
        }
    } catch (e) {
        // Connection may be down or circuit initializing; drop the call quietly
        console.debug('[Leaflet] JS->.NET call dropped:', method, e.message || e);
        return null;
    }
}

export async function initializeMap(mapElementId, dotnetReference) {
    console.log('[Leaflet] initializeMap called, element:', mapElementId);
    dotnetRef = dotnetReference;

    // Ensure DOM element is ready before Leaflet touches it
    // Return a promise that resolves after requestAnimationFrame
    await new Promise(resolve => {
        requestAnimationFrame(() => {
            try {
                console.log('[Leaflet] Creating Leaflet map instance...');

                // Create map with smooth animations enabled for better visual experience
                mapInstance = L.map(mapElementId, {
                    minZoom: HnHMinZoom,
                    maxZoom: HnHMaxZoom,
                    crs: HnHCRS,
                    attributionControl: false,
                    inertia: false,
                    zoomAnimation: true,        // Enable smooth zoom transitions
                    fadeAnimation: true,         // Re-enable tile fade (we'll avoid black with placeholders)
                    markerZoomAnimation: true    // Enable marker zoom animations
                });

                console.log('[Leaflet] Map instance created successfully');
                resolve();
            } catch (error) {
                console.error('[Leaflet] CRITICAL ERROR creating map instance:', error);
                throw error;
            }
        });
    });

    // Main tile layer (with cache and revision parameters for cache busting)
    // Performance optimizations for smooth zoom/pan: updateWhenZooming, keepBuffer, updateInterval, noWrap
    mainLayer = new SmartTileLayer('/map/grids/{map}/{z}/{x}_{y}.png?v={v}&{cache}', {
        minZoom: HnHMinZoom,
        maxZoom: HnHMaxZoom,
        zoomOffset: 0,
        zoomReverse: true,
        tileSize: TileSize,
        updateWhenIdle: false,     // Load tiles during zoom animation for smoother transitions (changed from true)
        updateWhenZooming: true,   // Continue updating tiles during zoom animation (NEW)
        keepBuffer: 3,             // Keep 3 tile buffer around viewport for smoother zoom/pan (increased from 2)
        updateInterval: 100,       // Throttle tile updates during continuous pan (ms) - faster for snappier response
        noWrap: true              // Don't wrap tiles at world edges (Haven map is finite)
    });
    mainLayer.invalidTile = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=';
    mainLayer.mapId = 0; // Explicitly initialize to ensure it's always a number (not undefined)
    mainLayer.addTo(mapInstance);

    // Mark missing tiles as temporarily invalid (TTL-based) to avoid repeated network requests
    // After TTL expires, tiles will be retried automatically
    mainLayer.on('tileerror', (e) => {
        try {
            const z = e?.coords?.z ?? mapInstance.getZoom();
            const x = e?.coords?.x;
            const y = e?.coords?.y;
            if (typeof x === 'number' && typeof y === 'number' && typeof z === 'number') {
                const cacheKey = `${mainLayer.mapId}:${x}:${y}:${z}`;
                // Set expiry timestamp: current time + TTL (2 minutes to align with server cache)
                mainLayer.negativeCache[cacheKey] = Date.now() + mainLayer.negativeCacheTTL;
            }
        } catch { /* ignore */ }
    });

    // Use parent tile as placeholder during load to avoid black background
    mainLayer.on('tileloadstart', (e) => setParentPlaceholder(mainLayer, e, mapInstance));
    mainLayer.on('tileload', clearPlaceholder);

    // Overlay layer is created lazily when first used (via setOverlayMap with valid mapId > 0)
    // This cuts tile traffic in half by default, as overlay is only needed when comparing maps
    overlayLayer = null;

    // Grid coordinate layer
    coordLayer = new L.GridLayer.GridCoord({ tileSize: TileSize, opacity: 0 });
    coordLayer.addTo(mapInstance);

    // Marker layers
    markerLayer = L.layerGroup().addTo(mapInstance);
    detailedMarkerLayer = L.layerGroup().addTo(mapInstance);
    customMarkerLayer = L.layerGroup().addTo(mapInstance);
    roadLayer = L.layerGroup().addTo(mapInstance);

    // Initialize managers
    MarkerManager.setMarkerLayers(markerLayer, detailedMarkerLayer);
    MarkerManager.initializeMarkerManager(invokeDotNetSafe);
    CustomMarkerManager.initializeCustomMarkerManager(customMarkerLayer, invokeDotNetSafe);
    PingManager.initialize(mapInstance);
    RoadManager.initializeRoadManager(roadLayer, invokeDotNetSafe);
    RoadManager.setMapInstance(mapInstance);

    // Initialize overlay layer (claims, villages, provinces)
    // Layer is visible by default with pclaim enabled (controlled by floating buttons)
    OverlayLayer.initializeOverlayLayer(mapInstance);

    // Set up overlay data request callback to route through Blazor
    OverlayLayer.setRequestOverlaysCallback((mapId, coords) => {
        invokeDotNetSafe('JsRequestOverlays', mapId, coords);
    });

    // Keyboard event handler for Alt+M ping shortcut
    let lastMouseLatLng = null;
    mapInstance.on('mousemove', (e) => {
        lastMouseLatLng = e.latlng;
    });

    document.addEventListener('keydown', (e) => {
        // Alt+M creates a ping at mouse position (or map center if no mouse position)
        if (e.altKey && e.key.toLowerCase() === 'm') {
            e.preventDefault();

            // Use last mouse position or map center
            const latlng = lastMouseLatLng || mapInstance.getCenter();

            // Convert LatLng to game coordinates
            const point = mapInstance.project(latlng, HnHMaxZoom);
            const coordX = Math.floor(point.x / TileSize);
            const coordY = Math.floor(point.y / TileSize);
            const localX = Math.floor(((point.x % TileSize) + TileSize) % TileSize);
            const localY = Math.floor(((point.y % TileSize) + TileSize) % TileSize);

            // Call Blazor to create ping
            invokeDotNetSafe('JsCreatePing', currentMapId, coordX, coordY, localX, localY);
        }
    });

    // Event handlers
    mapInstance.on('drag', () => {
        const point = mapInstance.project(mapInstance.getCenter(), mapInstance.getZoom());
        const coords = {
            x: Math.floor(point.x / TileSize),
            y: Math.floor(point.y / TileSize),
            z: mapInstance.getZoom()
        };
        invokeDotNetSafe('JsOnMapDragged', coords.x, coords.y, coords.z);
    });

    // Use 'zoomend' instead of 'zoom' to avoid triggering Blazor render cycles during zoom animation
    // Debounce expensive operations (prefetch, cache clearing, callbacks) to reduce excessive requests during rapid zoom
    mapInstance.on('zoomend', () => {
        const zoom = mapInstance.getZoom();

        // IMMEDIATE: Show/hide detailed markers (zoom-dependent UI - no debounce needed)
        if (zoom >= 6) {
            if (!mapInstance.hasLayer(detailedMarkerLayer)) {
                detailedMarkerLayer.addTo(mapInstance);
            }
        } else {
            if (mapInstance.hasLayer(detailedMarkerLayer)) {
                mapInstance.removeLayer(detailedMarkerLayer);
            }
        }

        // DEBOUNCED: Heavy operations only after zoom settles (1.5s delay)
        // This prevents 75+ tile requests per zoom step during rapid zooming
        clearTimeout(zoomDebounceTimer);
        zoomDebounceTimer = setTimeout(() => {
            // Calculate coords once zoom settles
            const point = mapInstance.project(mapInstance.getCenter(), mapInstance.getZoom());
            const coords = {
                x: Math.floor(point.x / TileSize),
                y: Math.floor(point.y / TileSize),
                z: mapInstance.getZoom()
            };

            // Notify C# of final zoom position (triggers URL update)
            invokeDotNetSafe('JsOnMapZoomed', coords.x, coords.y, coords.z);

            // Clear negative cache for visible tiles (allows retry of failed tiles)
            if (mainLayer) {
                mainLayer.clearVisibleNegativeCache();
            }
            if (overlayLayer && overlayLayer.mapId > 0) {
                overlayLayer.clearVisibleNegativeCache();
            }

            // Prefetch tiles for next zoom level (only after zoom settles)
            prefetchNextZoomTiles();
        }, ZOOM_DEBOUNCE_MS);
    });

    // Right-click handler for both tile context menu and custom marker placement
    mapInstance.on('contextmenu', (e) => {
        if (!mainLayer) {
            console.warn('[MapClick] Ignored right-click because mainLayer is not ready');
            return;
        }

        const zoom = mapInstance.getZoom();
        const point = mapInstance.project(e.latlng, HnHMaxZoom);

        const rawMapId = mainLayer.mapId;
        const mapId = Number(rawMapId);

        const rawCoordX = Number.isFinite(point.x) ? point.x : NaN;
        const rawCoordY = Number.isFinite(point.y) ? point.y : NaN;

        const coordX = Math.floor(rawCoordX / TileSize);
        const coordY = Math.floor(rawCoordY / TileSize);

        const localX = Math.floor(((rawCoordX % TileSize) + TileSize) % TileSize);
        const localY = Math.floor(((rawCoordY % TileSize) + TileSize) % TileSize);

        console.debug('[MapClick] types', {
            mapId: typeof rawMapId,
            coordX: typeof rawCoordX,
            coordY: typeof rawCoordY,
            localX: typeof localX,
            localY: typeof localY
        });

        if (!Number.isInteger(mapId) || mapId <= 0) {
            console.warn('[MapClick] Ignored right-click with invalid mapId', { rawMapId, mapId });
            return;
        }

        if (!Number.isInteger(coordX) || !Number.isInteger(coordY) || !Number.isInteger(localX) || !Number.isInteger(localY)) {
            console.warn('[MapClick] Ignored right-click due to invalid coordinates', { coordX, coordY, localX, localY });
            return;
        }

        const x = localX;
        const y = localY;

        console.log('[MapClick] Right-click at', { mapId, coordX, coordY, x, y });

        // Ctrl+Right-click: Show tile context menu (admin/writer tools)
        // Regular Right-click: Show map action menu (create marker/ping)
        const screenX = Math.floor(e.containerPoint.x);
        const screenY = Math.floor(e.containerPoint.y);

        if (e.originalEvent && e.originalEvent.ctrlKey) {
            // Ctrl held - show tile context menu for admin/writer actions
            invokeDotNetSafe('JsOnContextMenu', coordX, coordY, screenX, screenY);
        } else {
            // No modifier - show map action menu (create marker/ping)
            invokeDotNetSafe('JsOnMapRightClick', mapId, coordX, coordY, x, y, screenX, screenY);
        }
    });

    // NOTE: zoomend handler with cache clearing and prefetch has been moved to lines 165-205
    // and is now debounced with 1.5s delay to prevent excessive requests during rapid zoom

    // Clear negative cache for visible tiles after move completes
    // This allows tiles that were temporarily unavailable during pan to be retried
    // Note: We don't call redraw() - Leaflet will naturally load new tiles and keep old ones visible until ready
    mapInstance.on('moveend', () => {
        if (mainLayer) {
            mainLayer.clearVisibleNegativeCache();
        }
        if (overlayLayer && overlayLayer.mapId > 0) {
            overlayLayer.clearVisibleNegativeCache();
        }
    });

    // Listen for map 'load' event to notify C# when map is truly ready
    // IMPORTANT: Register this BEFORE setView() because 'load' fires immediately on first setView
    mapInstance.on('load', () => {
        console.log('[Leaflet] Map load event fired, notifying C# that map is ready');
        invokeDotNetSafe('OnMapReady');
    });

    // Listen for custom 'mapchanged' event fired by changeMap() function
    // This notifies C# when map switching is complete (replaces arbitrary delays)
    mapInstance.on('mapchanged', (e) => {
        console.log('[Leaflet] mapchanged event fired for map', e.mapId);
        invokeDotNetSafe('JsOnMapChanged', e.mapId);
    });

    // Set initial view to zoom level 7 (max zoom, shows base tiles)
    // This will trigger the 'load' event immediately
    mapInstance.setView([0, 0], HnHMaxZoom);

    return true;
}

/**
 * Prefetch tiles at next AND previous zoom levels to warm browser cache
 * This makes subsequent zoom-in AND zoom-out feel instant
 */
function prefetchNextZoomTiles() {
    if (!mapInstance || !mainLayer || !mainLayer.mapId || mainLayer.mapId <= 0) {
        return;
    }

    const currentZoom = mapInstance.getZoom();
    const pixelBounds = mapInstance.getPixelBounds();
    const tileRange = mainLayer._pxBoundsToTileRange(pixelBounds);

    // Prefetch a small ring around the viewport (±1 tile)
    const prefetchRange = {
        minX: Math.max(0, tileRange.min.x - 1),
        maxX: tileRange.max.x + 1,
        minY: Math.max(0, tileRange.min.y - 1),
        maxY: tileRange.max.y + 1
    };

    // Get revision for cache busting
    const revision = mainLayer.mapRevisions[mainLayer.mapId] || 1;
    let totalPrefetched = 0;

    // Prefetch next zoom level (zoom in) if not at max zoom
    if (currentZoom < HnHMaxZoom) {
        const nextZoom = currentZoom + 1;

        // Convert current zoom coordinates to next zoom coordinates
        // At higher zoom, we need 4x the tiles (2x2 grid per current tile)
        const nextZoomMinX = prefetchRange.minX * 2;
        const nextZoomMaxX = prefetchRange.maxX * 2 + 1;
        const nextZoomMinY = prefetchRange.minY * 2;
        const nextZoomMaxY = prefetchRange.maxY * 2 + 1;

        // Limit prefetch to avoid overwhelming the browser (max 50 tiles per zoom level)
        let prefetchCount = 0;
        const maxPrefetch = 50;

        for (let x = nextZoomMinX; x <= nextZoomMaxX && prefetchCount < maxPrefetch; x++) {
            for (let y = nextZoomMinY; y <= nextZoomMaxY && prefetchCount < maxPrefetch; y++) {
                // Convert Leaflet zoom to HnH zoom (reverse)
                let hnhZoom = nextZoom;
                if (mainLayer.options.zoomReverse) {
                    hnhZoom = mainLayer.options.maxZoom - nextZoom;
                }

                const cacheKey = `${mainLayer.mapId}:${x}:${y}:${hnhZoom}`;
                const tileKey = `${x}:${y}:${hnhZoom}`;

                // Skip if tile is in negative cache (known to be missing)
                if (mainLayer.negativeCache[cacheKey]) {
                    continue;
                }

                // Skip if already in per-map tile cache (already loaded)
                if (mainLayer.cache[mainLayer.mapId] && mainLayer.cache[mainLayer.mapId][tileKey]) {
                    continue;
                }

                // Build URL for prefetch
                const tileUrl = `/map/grids/${mainLayer.mapId}/${hnhZoom}/${x}_${y}.png?v=${revision}`;

                // Prefetch using Image() - this warms the browser cache
                const prefetchImg = new Image();
                prefetchImg.src = tileUrl;

                prefetchCount++;
            }
        }

        totalPrefetched += prefetchCount;
        if (prefetchCount > 0) {
            console.debug(`[Prefetch] Warmed cache for ${prefetchCount} tiles at zoom ${nextZoom} (zoom in)`);
        }
    }

    // NEW: Prefetch previous zoom level (zoom out) if not at min zoom
    if (currentZoom > HnHMinZoom) {
        const prevZoom = currentZoom - 1;

        // Convert current zoom coordinates to previous zoom coordinates
        // At lower zoom, we need 1/4 the tiles (each prev tile covers 2x2 current tiles)
        const prevZoomMinX = Math.floor(prefetchRange.minX / 2);
        const prevZoomMaxX = Math.floor(prefetchRange.maxX / 2);
        const prevZoomMinY = Math.floor(prefetchRange.minY / 2);
        const prevZoomMaxY = Math.floor(prefetchRange.maxY / 2);

        let prefetchCount = 0;
        const maxPrefetch = 25; // Fewer tiles needed for zoom out

        for (let x = prevZoomMinX; x <= prevZoomMaxX && prefetchCount < maxPrefetch; x++) {
            for (let y = prevZoomMinY; y <= prevZoomMaxY && prefetchCount < maxPrefetch; y++) {
                // Convert Leaflet zoom to HnH zoom (reverse)
                let hnhZoom = prevZoom;
                if (mainLayer.options.zoomReverse) {
                    hnhZoom = mainLayer.options.maxZoom - prevZoom;
                }

                const cacheKey = `${mainLayer.mapId}:${x}:${y}:${hnhZoom}`;
                const tileKey = `${x}:${y}:${hnhZoom}`;

                // Skip if tile is in negative cache
                if (mainLayer.negativeCache[cacheKey]) {
                    continue;
                }

                // Skip if already in per-map tile cache
                if (mainLayer.cache[mainLayer.mapId] && mainLayer.cache[mainLayer.mapId][tileKey]) {
                    continue;
                }

                // Build URL for prefetch
                const tileUrl = `/map/grids/${mainLayer.mapId}/${hnhZoom}/${x}_${y}.png?v=${revision}`;

                // Prefetch using Image()
                const prefetchImg = new Image();
                prefetchImg.src = tileUrl;

                prefetchCount++;
            }
        }

        totalPrefetched += prefetchCount;
        if (prefetchCount > 0) {
            console.debug(`[Prefetch] Warmed cache for ${prefetchCount} tiles at zoom ${prevZoom} (zoom out)`);
        }
    }
}

export function changeMap(mapId) {
    // Guard: Skip expensive operations if already on this map
    // This prevents redundant marker clearing and tile redraws when Blazor re-renders
    if (currentMapId === mapId && mainLayer && mainLayer.mapId === mapId) {
        console.debug('[changeMap] Already on map', mapId, '- skipping redundant call');
        return true;
    }

    console.log('[changeMap] Changing from map', currentMapId, 'to map', mapId);

    currentMapId = mapId;
    mainLayer.mapId = mapId;

    // Update manager states
    CharacterManager.setCurrentMapId(mapId);
    MarkerManager.setCurrentMapId(mapId);
    CustomMarkerManager.setCurrentMapId(mapId);
    OverlayLayer.setOverlayMapId(mapId);
    RoadManager.setCurrentMapId(mapId);

    // Clear all markers to avoid showing markers from previous map
    CharacterManager.clearAllCharacters(mapInstance);
    MarkerManager.clearAllMarkers(mapInstance);

    // NEW: Use per-map cache structure instead of clearing everything
    // Initialize cache for new map if needed (but keep other maps' caches)
    if (!mainLayer.cache[mapId]) {
        mainLayer.cache[mapId] = {};
    }

    // Clear negative cache entries for the NEW map only (allow retries for new map)
    // Keep negative cache for other maps to prevent unnecessary refetches
    const keysToDelete = [];
    for (const key in mainLayer.negativeCache) {
        if (key.startsWith(`${mapId}:`)) {
            keysToDelete.push(key);
        }
    }
    keysToDelete.forEach(key => delete mainLayer.negativeCache[key]);

    // Force tile reload by invalidating the layer
    mainLayer.redraw();

    // Also invalidate size to trigger full reload
    if (mapInstance) {
        mapInstance.invalidateSize(false);
    }

    CustomMarkerManager.flushCustomMarkerQueue(mapInstance);
    RoadManager.clearAllRoads();
    RoadManager.flushRoadQueue(mapInstance);

    // Fire mapchanged event to notify C# that map change is complete
    // This replaces arbitrary 50ms delays with proper event-driven architecture
    console.log('[changeMap] Map change complete, firing mapchanged event for map', mapId);
    if (mapInstance) {
        mapInstance.fire('mapchanged', { mapId: mapId });
    }

    return true;
}

export function setOverlayMap(mapId) {
    // Lazy-initialize overlay layer only when actually needed (mapId > 0)
    if (!overlayLayer && mapId > 0) {
        // Create overlay layer on-demand (same config as main layer but with opacity)
        overlayLayer = new SmartTileLayer('/map/grids/{map}/{z}/{x}_{y}.png?v={v}&{cache}', {
            minZoom: HnHMinZoom,
            maxZoom: HnHMaxZoom,
            zoomOffset: 0,
            zoomReverse: true,
            tileSize: TileSize,
            opacity: 0.6,
            updateWhenIdle: false,     // Load tiles during zoom animation for smoother transitions (changed from true)
            updateWhenZooming: true,   // Continue updating tiles during zoom animation (NEW)
            keepBuffer: 3,             // Keep 3 tile buffer around viewport for smoother zoom/pan (increased from 2)
            updateInterval: 100,       // Throttle tile updates during continuous pan (ms) - faster for snappier response
            noWrap: true              // Don't wrap tiles at world edges (Haven map is finite)
        });
        overlayLayer.invalidTile = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=';
        overlayLayer.mapId = -1;

        // Mark missing overlay tiles as temporarily invalid (TTL-based)
        overlayLayer.on('tileerror', (e) => {
            try {
                const z = e?.coords?.z ?? mapInstance.getZoom();
                const x = e?.coords?.x;
                const y = e?.coords?.y;
                if (typeof x === 'number' && typeof y === 'number' && typeof z === 'number') {
                    const cacheKey = `${overlayLayer.mapId}:${x}:${y}:${z}`;
                    // Set expiry timestamp: current time + TTL (2 minutes to align with server cache)
                    overlayLayer.negativeCache[cacheKey] = Date.now() + overlayLayer.negativeCacheTTL;
                }
            } catch { /* ignore */ }
        });
        // Use parent tile as placeholder during load to avoid black background
        overlayLayer.on('tileloadstart', (e) => setParentPlaceholder(overlayLayer, e, mapInstance));
        overlayLayer.on('tileload', clearPlaceholder);

        overlayLayer.addTo(mapInstance);
    }

    if (overlayLayer) {
        overlayLayer.mapId = mapId || -1;
        overlayLayer.redraw();
    }

    return true;
}

export function setView(gridX, gridY, zoom) {
    const x = gridX * 100;
    const y = gridY * 100;
    const latlng = mapInstance.unproject([x, y], HnHMaxZoom);
    mapInstance.setView(latlng, zoom);
    return true;
}

export function zoomOut() {
    mapInstance.setView([0, 0], HnHMinZoom);
    return true;
}

export function toggleGridCoordinates(visible) {
    coordLayer.setOpacity(visible ? 1 : 0);
    return true;
}

export function refreshTile(mapId, x, y, z, timestamp) {
    const tileKey = `${x}:${y}:${z}`;

    // Initialize per-map cache if needed
    if (!mainLayer.cache[mapId]) {
        mainLayer.cache[mapId] = {};
    }

    // Store cache entry with etag (timestamp)
    mainLayer.cache[mapId][tileKey] = { etag: timestamp };

    let refreshedAny = false;

    // Helper: check if a tile is currently within (slightly expanded) visible range for a given layer
    function isTileVisible(layer, tileX, tileY, tileZ) {
        if (!mapInstance || !layer || !layer._map) return false;
        // Convert Leaflet zoom to HnH zoom (reverse handling)
        let hnhZ = mapInstance.getZoom();
        if (layer.options.zoomReverse) {
            hnhZ = layer.options.maxZoom - hnhZ;
        }
        if (tileZ !== hnhZ) return false;
        // Compute visible tile range and expand by 1 tile as a buffer
        const range = layer._pxBoundsToTileRange(mapInstance.getPixelBounds());
        const minX = range.min.x - 1;
        const maxX = range.max.x + 1;
        const minY = range.min.y - 1;
        const maxY = range.max.y + 1;
        return tileX >= minX && tileX <= maxX && tileY >= minY && tileY <= maxY;
    }

    // Refresh only if the tile is visible for the corresponding layer; otherwise skip
    if (mainLayer && mainLayer.mapId === mapId) {
        if (isTileVisible(mainLayer, x, y, z)) {
            refreshedAny = mainLayer.refresh(x, y, z) || refreshedAny;
        }
    }

    if (overlayLayer && overlayLayer.mapId === mapId) {
        if (isTileVisible(overlayLayer, x, y, z)) {
            refreshedAny = overlayLayer.refresh(x, y, z) || refreshedAny;
        }
    }

    // Do not force redraw for offscreen updates; let visible tiles update smoothly
    return true;
}

// Update map revision for cache busting
export function setMapRevision(mapId, revision) {
    if (mainLayer) {
        mainLayer.setMapRevision(mapId, revision);
    }
    if (overlayLayer) {
        overlayLayer.setMapRevision(mapId, revision);
    }
}

// Character Management - Delegate to CharacterManager
export function addCharacter(characterData) {
    return CharacterManager.addCharacter(characterData, mapInstance);
}

export function updateCharacter(characterId, characterData) {
    return CharacterManager.updateCharacter(characterId, characterData, mapInstance);
}

export function removeCharacter(characterId) {
    return CharacterManager.removeCharacter(characterId, mapInstance);
}

export function clearAllCharacters() {
    return CharacterManager.clearAllCharacters(mapInstance);
}

export function setCharactersSnapshot(charactersData) {
    console.log('[leaflet-interop] setCharactersSnapshot called with', Array.isArray(charactersData) ? charactersData.length : 'non-array', 'items, delegating to CharacterManager');
    return CharacterManager.setCharactersSnapshot(charactersData, mapInstance);
}

export function applyCharacterDelta(delta) {
    return CharacterManager.applyCharacterDelta(delta, mapInstance);
}

export function toggleCharacterTooltips(visible) {
    return CharacterManager.toggleCharacterTooltips(visible);
}

export function setUpdateInterval(intervalMs) {
    CharacterManager.setUpdateInterval(intervalMs);
    return true;
}

export function jumpToCharacter(characterId) {
    return CharacterManager.jumpToCharacter(characterId, mapInstance);
}

// Marker Management - Delegate to MarkerManager
export function addMarker(markerData) {
    return MarkerManager.addMarker(markerData, mapInstance);
}

export function addMarkersBatch(markersData) {
    return MarkerManager.addMarkersBatch(markersData, mapInstance);
}

export function updateMarker(markerId, markerData) {
    return MarkerManager.updateMarker(markerId, markerData, mapInstance);
}

export function removeMarker(markerId) {
    return MarkerManager.removeMarker(markerId, mapInstance);
}

export function clearAllMarkers() {
    return MarkerManager.clearAllMarkers(mapInstance);
}

export function toggleMarkerTooltips(type, visible) {
    return MarkerManager.toggleMarkerTooltips(type, visible);
}

export function setThingwallHighlightEnabled(enabled) {
    if (!mapInstance) {
        console.warn('[LeafletInterop] Cannot toggle thingwall highlight - map not initialized');
        return false;
    }
    return MarkerManager.setThingwallHighlightEnabled(enabled, mapInstance);
}

export function setQuestGiverHighlightEnabled(enabled) {
    if (!mapInstance) {
        console.warn('[LeafletInterop] Cannot toggle quest giver highlight - map not initialized');
        return false;
    }
    return MarkerManager.setQuestGiverHighlightEnabled(enabled, mapInstance);
}

export function setMarkerFilterModeEnabled(enabled) {
    if (!mapInstance) {
        console.warn('[LeafletInterop] Cannot toggle marker filter mode - map not initialized');
        return false;
    }
    return MarkerManager.setMarkerFilterModeEnabled(enabled, mapInstance);
}

export function setJumpConnectionsEnabled(enabled) {
    if (!mapInstance) {
        console.warn('[LeafletInterop] Cannot toggle jump connections - map not initialized');
        return false;
    }
    return MarkerManager.setJumpConnectionsEnabled(enabled, mapInstance);
}

export function jumpToMarker(markerId) {
    return MarkerManager.jumpToMarker(markerId, mapInstance);
}

export function setHiddenMarkerTypes(types) {
    return MarkerManager.setHiddenMarkerTypes(types, mapInstance);
}

// Custom Marker Management - Delegate to CustomMarkerManager
export function addCustomMarker(marker) {
    return CustomMarkerManager.addCustomMarker(marker, mapInstance);
}

export function updateCustomMarker(markerId, marker) {
    return CustomMarkerManager.updateCustomMarker(markerId, marker, mapInstance);
}

export function removeCustomMarker(markerId) {
    return CustomMarkerManager.removeCustomMarker(markerId);
}

export function clearAllCustomMarkers() {
    return CustomMarkerManager.clearAllCustomMarkers();
}

export function toggleCustomMarkers(visible) {
    return CustomMarkerManager.toggleCustomMarkers(visible, mapInstance);
}

export function jumpToCustomMarker(markerId, zoomLevel) {
    return CustomMarkerManager.jumpToCustomMarker(markerId, zoomLevel, mapInstance);
}

export function isCustomMarkerLayerReady() {
    return CustomMarkerManager.isCustomMarkerLayerReady();
}

export function flushCustomMarkerQueue() {
    return CustomMarkerManager.flushCustomMarkerQueue(mapInstance);
}

// Ping functions
export function onPingCreated(pingData) {
    console.log('[leaflet-interop] onPingCreated called with:', pingData);
    const ping = typeof pingData === 'string' ? JSON.parse(pingData) : pingData;
    console.log('[leaflet-interop] Parsed ping:', ping);
    console.log('[leaflet-interop] Calling PingManager.addPing...');
    const result = PingManager.addPing(ping, true);
    console.log('[leaflet-interop] PingManager.addPing result:', result);
    return result;
}

export function onPingDeleted(pingId) {
    console.log('[leaflet-interop] onPingDeleted called with:', pingId);
    return PingManager.removePing(pingId, false);
}

export function jumpToLatestPing() {
    const latestPing = PingManager.getLatestPing();

    if (!latestPing || !latestPing.ping) {
        console.log('[leaflet-interop] No ping available to jump to');
        return false;
    }

    const ping = latestPing.ping;
    console.log('[leaflet-interop] Jumping to latest ping:', ping);

    // Calculate absolute position from grid coordinates
    const TileSize = 100;
    const HnHMaxZoom = 7;
    const absX = ping.CoordX * TileSize + ping.X;
    const absY = ping.CoordY * TileSize + ping.Y;

    // Convert to map coordinates
    const latLng = mapInstance.unproject([absX, absY], HnHMaxZoom);

    // Animate to the ping location
    mapInstance.setView(latLng, HnHMaxZoom, {
        animate: true,
        duration: 0.5
    });

    return true;
}

export function getActivePingCount() {
    return PingManager.getActivePingCount();
}

// Overlay Layer Management - Control claims, villages, provinces display
export function setClaimOverlayVisible(visible) {
    OverlayLayer.setOverlayLayerVisible(visible, mapInstance);
    return true;
}

export function setOverlayTypeEnabled(overlayType, enabled) {
    OverlayLayer.setOverlayTypeEnabled(overlayType, enabled);
    return true;
}

export function setEnabledOverlayTypes(types) {
    OverlayLayer.setEnabledOverlayTypes(types);
    return true;
}

export function getEnabledOverlayTypes() {
    return OverlayLayer.getEnabledOverlayTypes();
}

export function isClaimOverlayVisible() {
    return OverlayLayer.isOverlayLayerVisible(mapInstance);
}

export function clearOverlayCache() {
    OverlayLayer.clearOverlayCache();
    return true;
}

export function redrawOverlays() {
    OverlayLayer.redrawOverlays();
    return true;
}

// Receive overlay data from Blazor and pass to overlay layer
export function setOverlayData(mapId, overlays) {
    OverlayLayer.setOverlayData(mapId, overlays);
    return true;
}

// Road Management - Delegate to RoadManager
export function addRoad(road) {
    return RoadManager.addRoad(road, mapInstance);
}

export function updateRoad(roadId, road) {
    return RoadManager.updateRoad(roadId, road, mapInstance);
}

export function removeRoad(roadId) {
    return RoadManager.removeRoad(roadId);
}

export function clearAllRoads() {
    return RoadManager.clearAllRoads();
}

export function toggleRoads(visible) {
    return RoadManager.toggleRoads(visible, mapInstance);
}

export function jumpToRoad(roadId) {
    return RoadManager.jumpToRoad(roadId, mapInstance);
}

export function startDrawingRoad() {
    return RoadManager.startDrawingRoad(mapInstance);
}

export function cancelDrawingRoad() {
    return RoadManager.cancelDrawingRoad(mapInstance);
}

export function isInDrawingMode() {
    return RoadManager.isInDrawingMode();
}

export function getDrawingPointsCount() {
    return RoadManager.getDrawingPointsCount();
}

export function finishDrawingRoadFromMenu() {
    return RoadManager.finishDrawingRoad(mapInstance);
}
