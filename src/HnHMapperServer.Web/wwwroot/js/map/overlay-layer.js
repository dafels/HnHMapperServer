// Overlay Layer Module
// Renders claim, village, and province overlays using Canvas
// Data is bitpacked (1250 bytes = 10000 bits for 100x100 grid) with LSB-first ordering
// Data is fetched via Blazor interop (server-to-server) not direct browser fetch

import { TileSize, BaseTileSize, HnHMaxZoom } from './leaflet-config.js';

// Overlay type colors (RGBA)
const OVERLAY_COLORS = {
    // Player Claims - red transparent
    'ClaimFloor': [255, 60, 60, 80],    // Semi-transparent red fill
    'ClaimOutline': [200, 0, 0, 180],   // Semi-transparent red border

    // Village Claims - orange transparent
    'VillageFloor': [255, 165, 0, 80],  // Semi-transparent orange fill
    'VillageOutline': [220, 120, 0, 180], // Semi-transparent orange border
    'VillageSAR': [200, 200, 0, 60],    // Semi-transparent yellow (Safe Area Radius)

    // Provinces - various colors for different levels
    'Province0': [255, 0, 0, 100],      // Red
    'Province1': [255, 128, 0, 100],    // Orange
    'Province2': [255, 255, 0, 100],    // Yellow
    'Province3': [0, 255, 0, 100],      // Green
    'Province4': [0, 128, 255, 100]     // Blue
};

// Which overlay types should be rendered as outlines (borders only)
// Note: VillageOutline is NOT in this set because hmap files often only contain outline data for villages,
// so we render VillageOutline as a fill to show the entire village area
const OUTLINE_TYPES = new Set(['ClaimOutline']);

// Overlay data cache: mapId -> coordKey -> overlayType -> Uint8Array
const overlayCache = {};

// Pending fetch requests to prevent duplicate Blazor calls
const pendingFetches = new Set();

// Request batching state for coalescing multiple tile requests
let pendingCoords = new Map(); // mapId -> Set<coordKey>
let batchTimer = null;
const BATCH_DELAY_MS = 50; // 50ms debounce window
const MAX_COORDS_PER_REQUEST = 100; // API limit - larger batches are truncated server-side

// Layer state
let currentMapId = 0;
let overlayCanvasLayer = null;
let enabledOverlayTypes = new Set(); // Empty by default, controlled by floating buttons

// Track active tiles for incremental updates (avoids flickering)
// key: "z_x_y" -> { canvas, coords, scaleFactor, startX, startY, endX, endY }
let activeTiles = new Map();

// Callback to request overlays from Blazor (set by leaflet-interop.js)
let requestOverlaysCallback = null;

/**
 * Decode base64 string to Uint8Array
 */
function base64ToUint8Array(base64) {
    const binaryString = atob(base64);
    const bytes = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
        bytes[i] = binaryString.charCodeAt(i);
    }
    return bytes;
}

/**
 * Check if a bit is set in the bitpacked overlay data
 * LSB-first ordering: bit at index i is at byte[i/8] & (1 << (i % 8))
 * Data is for 100x100 grid cells (BaseTileSize)
 */
function isBitSet(data, x, y) {
    const bitIndex = y * BaseTileSize + x;
    const byteIndex = Math.floor(bitIndex / 8);
    const bitOffset = bitIndex % 8;
    return (data[byteIndex] & (1 << bitOffset)) !== 0;
}

/**
 * Get overlay data for a grid coordinate
 * Returns cached data or null if not loaded yet
 */
function getOverlayData(mapId, x, y) {
    const mapCache = overlayCache[mapId];
    if (!mapCache) return null;

    const coordKey = `${x}_${y}`;
    return mapCache[coordKey] || null;
}

/**
 * Request overlay data for a set of grid coordinates via Blazor
 * Uses debounced batching to coalesce multiple tile requests into fewer API calls
 * Data will be returned asynchronously via setOverlayData()
 */
function requestOverlays(mapId, coords) {
    if (coords.length === 0) return;
    if (!requestOverlaysCallback) {
        console.warn('[Overlay] No callback registered - cannot request overlays');
        return;
    }

    // Initialize pending coords set for this mapId if needed
    if (!pendingCoords.has(mapId)) {
        pendingCoords.set(mapId, new Set());
    }

    // Mark coordinates as pending in cache and add to batch
    if (!overlayCache[mapId]) {
        overlayCache[mapId] = {};
    }

    const pendingSet = pendingCoords.get(mapId);
    for (const coord of coords) {
        const coordKey = `${coord.x}_${coord.y}`;

        // Skip if already in cache (pending or not) - prevents duplicate requests
        // Pending coords are already being fetched by another request
        if (overlayCache[mapId][coordKey]) {
            continue;
        }

        // Mark as pending and add to batch
        overlayCache[mapId][coordKey] = { _pending: true };
        pendingSet.add(coordKey);
    }

    // Debounce: wait for more requests, then send batched request
    clearTimeout(batchTimer);
    batchTimer = setTimeout(() => {
        flushBatchedRequests();
    }, BATCH_DELAY_MS);
}

/**
 * Flush all pending batched requests to Blazor
 * Called after debounce timer expires
 * Splits large batches into chunks of MAX_COORDS_PER_REQUEST to avoid API truncation
 */
function flushBatchedRequests() {
    for (const [mapId, coordSet] of pendingCoords) {
        if (coordSet.size === 0) continue;

        // Split into chunks to avoid API truncation (API limit is 100 coords)
        const coordArray = Array.from(coordSet);
        for (let i = 0; i < coordArray.length; i += MAX_COORDS_PER_REQUEST) {
            const chunk = coordArray.slice(i, i + MAX_COORDS_PER_REQUEST);
            const coordStr = chunk.join(',');
            const fetchKey = `${mapId}:${coordStr}`;

            // Skip if already requesting this exact batch
            if (pendingFetches.has(fetchKey)) continue;
            pendingFetches.add(fetchKey);

            console.debug(`[Overlay] Sending batch: ${chunk.length} coords for map ${mapId}`);

            // Request data from Blazor (fire-and-forget, response comes via setOverlayData)
            requestOverlaysCallback(mapId, coordStr);

            // Clear pending flag after a timeout (in case Blazor doesn't respond)
            setTimeout(() => {
                pendingFetches.delete(fetchKey);
            }, 10000);
        }
    }

    // Clear all pending coord batches
    pendingCoords.clear();
}

/**
 * Set the callback function to request overlays from Blazor
 */
export function setRequestOverlaysCallback(callback) {
    requestOverlaysCallback = callback;
}

/**
 * Receive overlay data from Blazor and cache it
 * Called by Blazor after fetching data from API
 *
 * Note: We don't try to match responses to requests because multiple batches
 * can be in flight and responses arrive out of order. Instead, we just process
 * the data we receive and update the cache accordingly.
 */
export function setOverlayData(mapId, overlays) {
    console.debug('[Overlay] Received', overlays.length, 'overlays from Blazor for map', mapId);

    // Initialize map cache if needed
    if (!overlayCache[mapId]) {
        overlayCache[mapId] = {};
    }

    // Build set of affected grid coordinates for efficient lookup
    const affectedGrids = new Set();

    // Process and cache each overlay
    for (const overlay of overlays) {
        const coordKey = `${overlay.x}_${overlay.y}`;
        affectedGrids.add(coordKey);

        // Initialize or replace cache entry (clears _pending flag if present)
        if (!overlayCache[mapId][coordKey] || overlayCache[mapId][coordKey]._pending) {
            overlayCache[mapId][coordKey] = {};
        }

        // Decode base64 data to Uint8Array
        overlayCache[mapId][coordKey][overlay.type] = base64ToUint8Array(overlay.data);
    }

    // Note: Coords with no overlays stay pending forever (won't be re-requested).
    // This is intentional - they just won't show overlays, which is correct behavior.

    // Find and update only tiles that cover affected grid coordinates (avoids flickering)
    if (overlayCanvasLayer) {
        for (const [tileKey, tileInfo] of activeTiles) {
            // Check if this tile covers any of the affected grids
            let needsUpdate = false;
            for (let gx = tileInfo.startX; gx < tileInfo.endX && !needsUpdate; gx++) {
                for (let gy = tileInfo.startY; gy < tileInfo.endY && !needsUpdate; gy++) {
                    if (affectedGrids.has(`${gx}_${gy}`)) {
                        needsUpdate = true;
                    }
                }
            }

            if (needsUpdate) {
                // Re-render this specific tile in place (no flicker)
                overlayCanvasLayer._renderOverlays(
                    tileInfo.canvas,
                    tileInfo.coords,
                    tileInfo.scaleFactor,
                    tileInfo.startX,
                    tileInfo.startY
                );
            }
        }
    }
}

/**
 * Create the overlay Canvas layer
 */
function createOverlayLayer() {
    return L.GridLayer.extend({
        createTile: function(coords, done) {
            const tile = document.createElement('canvas');
            tile.width = TileSize;
            tile.height = TileSize;

            // Calculate scale factor for zoom (each zoom level covers 2x2 tiles of next level)
            // Multiply by TileSize/BaseTileSize (4) because 400x400 tiles cover 4x4 base 100x100 grid cells
            const scaleFactor = Math.pow(2, HnHMaxZoom - coords.z) * (TileSize / BaseTileSize);

            // Calculate grid coordinates covered by this tile
            const startX = coords.x * scaleFactor;
            const startY = coords.y * scaleFactor;
            const endX = startX + scaleFactor;
            const endY = startY + scaleFactor;

            // Store tile reference for incremental updates (avoids flickering)
            const tileKey = `${coords.z}_${coords.x}_${coords.y}`;
            activeTiles.set(tileKey, {
                canvas: tile,
                coords,
                scaleFactor,
                startX,
                startY,
                endX,
                endY
            });

            // Collect coords to fetch
            const coordsToFetch = [];
            for (let gx = startX; gx < endX; gx++) {
                for (let gy = startY; gy < endY; gy++) {
                    const overlayData = getOverlayData(currentMapId, gx, gy);
                    if (overlayData === null) {
                        coordsToFetch.push({ x: gx, y: gy });
                    }
                }
            }

            // Request any missing overlay data via Blazor (only if overlays are enabled)
            if (coordsToFetch.length > 0 && enabledOverlayTypes.size > 0) {
                requestOverlays(currentMapId, coordsToFetch);
            }

            // Render overlays to canvas
            this._renderOverlays(tile, coords, scaleFactor, startX, startY);

            // Async completion
            setTimeout(() => done(null, tile), 0);

            return tile;
        },

        _renderOverlays: function(canvas, coords, scaleFactor, startGridX, startGridY) {
            const ctx = canvas.getContext('2d');

            // Use ImageData for batch pixel manipulation (50-100x faster than fillRect per pixel)
            const imageData = ctx.createImageData(TileSize, TileSize);
            const pixels = imageData.data;

            // Iterate over all grid tiles covered by this canvas tile
            for (let gx = startGridX; gx < startGridX + scaleFactor; gx++) {
                for (let gy = startGridY; gy < startGridY + scaleFactor; gy++) {
                    const overlayData = getOverlayData(currentMapId, gx, gy);
                    if (!overlayData) continue;

                    // Calculate offset within canvas for this grid
                    const canvasOffsetX = Math.floor((gx - startGridX) * TileSize / scaleFactor);
                    const canvasOffsetY = Math.floor((gy - startGridY) * TileSize / scaleFactor);
                    const gridSize = Math.floor(TileSize / scaleFactor);

                    // Render each enabled overlay type
                    for (const overlayType of enabledOverlayTypes) {
                        const data = overlayData[overlayType];
                        if (!data) continue;

                        const color = OVERLAY_COLORS[overlayType];
                        if (!color) continue;

                        const isOutline = OUTLINE_TYPES.has(overlayType);

                        // For zoomed-out views, sample the overlay data
                        if (scaleFactor > 1) {
                            this._renderScaledToImageData(pixels, data, canvasOffsetX, canvasOffsetY,
                                gridSize, color, isOutline, scaleFactor);
                        } else {
                            // 1:1 zoom - render pixel by pixel to ImageData
                            this._renderFullToImageData(pixels, data, canvasOffsetX, canvasOffsetY, color, isOutline);
                        }
                    }
                }
            }

            // Single putImageData call instead of 10,000+ fillRect calls
            ctx.putImageData(imageData, 0, 0);
        },

        // Blend a pixel into ImageData with alpha compositing
        _blendPixel: function(pixels, x, y, r, g, b, a) {
            if (x < 0 || x >= TileSize || y < 0 || y >= TileSize) return;
            const i = (y * TileSize + x) * 4;
            const srcAlpha = a / 255;
            const dstAlpha = pixels[i + 3] / 255;
            const outAlpha = srcAlpha + dstAlpha * (1 - srcAlpha);

            if (outAlpha > 0) {
                pixels[i] = (r * srcAlpha + pixels[i] * dstAlpha * (1 - srcAlpha)) / outAlpha;
                pixels[i + 1] = (g * srcAlpha + pixels[i + 1] * dstAlpha * (1 - srcAlpha)) / outAlpha;
                pixels[i + 2] = (b * srcAlpha + pixels[i + 2] * dstAlpha * (1 - srcAlpha)) / outAlpha;
                pixels[i + 3] = outAlpha * 255;
            }
        },

        _renderFullToImageData: function(pixels, data, offsetX, offsetY, color, isOutline) {
            const r = color[0], g = color[1], b = color[2], a = color[3];

            // Data is BaseTileSize x BaseTileSize (100x100 grid)
            if (isOutline) {
                // For outlines, only draw pixels that are on the border
                for (let y = 0; y < BaseTileSize; y++) {
                    for (let x = 0; x < BaseTileSize; x++) {
                        if (isBitSet(data, x, y)) {
                            // Check if this is a border pixel (has at least one neighbor that's not set)
                            const isBorder =
                                (x === 0 || !isBitSet(data, x - 1, y)) ||
                                (x === BaseTileSize - 1 || !isBitSet(data, x + 1, y)) ||
                                (y === 0 || !isBitSet(data, x, y - 1)) ||
                                (y === BaseTileSize - 1 || !isBitSet(data, x, y + 1));

                            if (isBorder) {
                                this._blendPixel(pixels, offsetX + x, offsetY + y, r, g, b, a);
                            }
                        }
                    }
                }
            } else {
                // For fills, draw all set pixels
                for (let y = 0; y < BaseTileSize; y++) {
                    for (let x = 0; x < BaseTileSize; x++) {
                        if (isBitSet(data, x, y)) {
                            this._blendPixel(pixels, offsetX + x, offsetY + y, r, g, b, a);
                        }
                    }
                }
            }
        },

        _renderScaledToImageData: function(pixels, data, offsetX, offsetY, size, color, isOutline, scaleFactor) {
            const r = color[0], g = color[1], b = color[2], a = color[3];
            const sampleStep = Math.max(1, Math.floor(scaleFactor));

            // Sample the overlay data at intervals (data is BaseTileSize x BaseTileSize)
            for (let sy = 0; sy < BaseTileSize; sy += sampleStep) {
                for (let sx = 0; sx < BaseTileSize; sx += sampleStep) {
                    // Check if any pixel in this sample area is set
                    let anySet = false;
                    let isBorder = false;

                    for (let dy = 0; dy < sampleStep && !anySet; dy++) {
                        for (let dx = 0; dx < sampleStep && !anySet; dx++) {
                            const x = Math.min(sx + dx, BaseTileSize - 1);
                            const y = Math.min(sy + dy, BaseTileSize - 1);
                            if (isBitSet(data, x, y)) {
                                anySet = true;

                                // For outlines, check if this is a border
                                if (isOutline) {
                                    isBorder =
                                        (x === 0 || !isBitSet(data, x - 1, y)) ||
                                        (x === BaseTileSize - 1 || !isBitSet(data, x + 1, y)) ||
                                        (y === 0 || !isBitSet(data, x, y - 1)) ||
                                        (y === BaseTileSize - 1 || !isBitSet(data, x, y + 1));
                                }
                            }
                        }
                    }

                    if (anySet && (!isOutline || isBorder)) {
                        // Calculate canvas position for this sample
                        const canvasX = Math.floor(offsetX + (sx / BaseTileSize) * size);
                        const canvasY = Math.floor(offsetY + (sy / BaseTileSize) * size);
                        const rectSize = Math.max(1, Math.floor(size / BaseTileSize * sampleStep));

                        // Fill the rect area in ImageData
                        for (let py = 0; py < rectSize; py++) {
                            for (let px = 0; px < rectSize; px++) {
                                this._blendPixel(pixels, canvasX + px, canvasY + py, r, g, b, a);
                            }
                        }
                    }
                }
            }
        }
    });
}

/**
 * Initialize the overlay layer and add to map
 */
export function initializeOverlayLayer(mapInstance) {
    if (overlayCanvasLayer) {
        mapInstance.removeLayer(overlayCanvasLayer);
    }

    const OverlayLayer = createOverlayLayer();
    overlayCanvasLayer = new OverlayLayer({
        tileSize: TileSize,
        opacity: 1,
        updateWhenIdle: false,
        updateWhenZooming: true,
        keepBuffer: 2,
        noWrap: true
    });

    overlayCanvasLayer.addTo(mapInstance);

    // Clean up tile references when tiles are unloaded
    overlayCanvasLayer.on('tileunload', function(e) {
        const tileKey = `${e.coords.z}_${e.coords.x}_${e.coords.y}`;
        activeTiles.delete(tileKey);
    });

    return overlayCanvasLayer;
}

/**
 * Set the current map ID and clear cache for map changes
 */
export function setOverlayMapId(mapId) {
    if (currentMapId !== mapId) {
        currentMapId = mapId;
        // Clear ALL overlay cache on map switch to prevent memory accumulation
        // Each map's overlay data can be tens of MB - keeping all maps cached leads to GB memory usage
        for (const key in overlayCache) {
            delete overlayCache[key];
        }
        // Also clear active tiles tracking
        activeTiles.clear();
        if (overlayCanvasLayer && overlayCanvasLayer._map) {
            overlayCanvasLayer.redraw();
        }
    }
}

/**
 * Enable or disable an overlay type
 */
export function setOverlayTypeEnabled(overlayType, enabled) {
    if (enabled) {
        enabledOverlayTypes.add(overlayType);
    } else {
        enabledOverlayTypes.delete(overlayType);
    }

    if (overlayCanvasLayer && overlayCanvasLayer._map) {
        overlayCanvasLayer.redraw();
    }
}

/**
 * Set all enabled overlay types at once
 */
export function setEnabledOverlayTypes(types) {
    enabledOverlayTypes = new Set(types);
    if (overlayCanvasLayer && overlayCanvasLayer._map) {
        overlayCanvasLayer.redraw();
    }
}

/**
 * Get currently enabled overlay types
 */
export function getEnabledOverlayTypes() {
    return Array.from(enabledOverlayTypes);
}

/**
 * Toggle visibility of the overlay layer
 */
export function setOverlayLayerVisible(visible, mapInstance) {
    if (!overlayCanvasLayer) return;

    if (visible && !mapInstance.hasLayer(overlayCanvasLayer)) {
        overlayCanvasLayer.addTo(mapInstance);
    } else if (!visible && mapInstance.hasLayer(overlayCanvasLayer)) {
        mapInstance.removeLayer(overlayCanvasLayer);
    }
}

/**
 * Check if overlay layer is visible
 */
export function isOverlayLayerVisible(mapInstance) {
    return overlayCanvasLayer && mapInstance && mapInstance.hasLayer(overlayCanvasLayer);
}

/**
 * Clear all cached overlay data
 */
export function clearOverlayCache() {
    for (const key in overlayCache) {
        delete overlayCache[key];
    }
}

/**
 * Force redraw of overlay layer
 */
export function redrawOverlays() {
    if (overlayCanvasLayer && overlayCanvasLayer._map) {
        overlayCanvasLayer.redraw();
    }
}

/**
 * Invalidate cached overlay data at a specific coordinate and trigger refetch
 * Called when receiving overlayUpdated SSE event
 * @param {number} mapId - Map ID
 * @param {number} x - Grid X coordinate
 * @param {number} y - Grid Y coordinate
 * @param {string} overlayType - The overlay type that was updated (optional, if null clears all types)
 */
export function invalidateOverlayAtCoord(mapId, x, y, overlayType) {
    console.debug(`[Overlay] Invalidating overlay at map ${mapId}, coord (${x}, ${y}), type: ${overlayType || 'all'}`);

    const mapCache = overlayCache[mapId];
    if (mapCache) {
        const coordKey = `${x}_${y}`;
        if (mapCache[coordKey]) {
            if (overlayType) {
                // Clear specific overlay type
                delete mapCache[coordKey][overlayType];
            } else {
                // Clear all overlay types at this coordinate
                delete mapCache[coordKey];
            }
        }
    }

    // Only refetch if this is the current map and overlays are enabled
    if (mapId === currentMapId && enabledOverlayTypes.size > 0) {
        // Request fresh data for this coordinate
        requestOverlays(mapId, [{ x, y }]);
    }
}
