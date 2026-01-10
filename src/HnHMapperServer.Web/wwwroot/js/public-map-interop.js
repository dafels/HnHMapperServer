// Public Map Interop Module
// Simplified Leaflet map for public (unauthenticated) map viewing
// Displays tiles and thingwall markers with highlighted styling

// Map constants - using 400x400 tiles for better performance (16x fewer HTTP requests)
// Each 400x400 tile contains 4x4 = 16 original 100x100 grid cells
const TileSize = 400;
const BaseTileSize = 100;  // Original grid cell size (for coordinate conversion)
const HnHMaxZoom = 7;
const HnHMinZoom = 1;

// Coordinate normalization factors (based on original 100px grid cells, not display tile size)
const latNormalization = 90.0 * BaseTileSize / 2500000.0;
const lngNormalization = 180.0 * BaseTileSize / 2500000.0;

// Pre-computed scale factors for each zoom level
const SCALE_FACTORS = {};
for (let z = HnHMinZoom; z <= HnHMaxZoom; z++) {
    SCALE_FACTORS[z] = 1 << (HnHMaxZoom - z);  // 2^(HnHMaxZoom - z)
}

// Custom HnH Projection
const HnHProjection = {
    project: function (latlng) {
        return L.point(latlng.lat / latNormalization, latlng.lng / lngNormalization);
    },
    unproject: function (point) {
        return L.latLng(point.x * latNormalization, point.y * lngNormalization);
    },
    bounds: L.bounds(
        [-latNormalization, -lngNormalization],
        [latNormalization, lngNormalization]
    )
};

// Custom HnH Coordinate Reference System
const HnHCRS = L.extend({}, L.CRS.Simple, {
    projection: HnHProjection
});

// Global state
let mapInstance = null;
let tileLayer = null;
let markerLayer = null;
let currentSlug = null;
let zoomDebounceTimer = null;
let preloadDebounceTimer = null;

// Preload configuration
const PRELOAD_CONFIG = {
    enabled: true,
    concurrency: 6,           // Parallel fetches (don't overwhelm browser)
    extendViewport: 3,        // Tiles beyond viewport in each direction
    preloadAdjacentZooms: true // Also preload zoom +/- 1
};

// Track preloaded tiles to avoid duplicates
const preloadedTiles = new Set();

// Update URL with current map position using history.replaceState (bypasses Blazor)
// Coordinates in URL are in original grid units (100x100) for consistency with API bounds
function updateUrlWithPosition() {
    if (!mapInstance || !currentSlug) return;

    const point = mapInstance.project(mapInstance.getCenter(), HnHMaxZoom);
    // Use BaseTileSize for URL coordinates (consistent with API bounds)
    const x = Math.floor(point.x / BaseTileSize);
    const y = Math.floor(point.y / BaseTileSize);
    const z = mapInstance.getZoom();

    const newUrl = `/public/${currentSlug}?x=${x}&y=${y}&z=${z}`;
    history.replaceState(null, '', newUrl);
}

/**
 * Calculate which tiles to preload based on viewport bounds
 */
function calculateTilesToPreload(bounds, zoom) {
    const tiles = [];
    const extend = PRELOAD_CONFIG.extendViewport;

    // Convert bounds to tile coordinates at this zoom
    const nw = mapInstance.project(bounds.getNorthWest(), HnHMaxZoom);
    const se = mapInstance.project(bounds.getSouthEast(), HnHMaxZoom);

    // Scale factor for this zoom level
    const scale = SCALE_FACTORS[zoom];

    const minTileX = Math.floor(nw.x / TileSize / scale) - extend;
    const maxTileX = Math.floor(se.x / TileSize / scale) + extend;
    const minTileY = Math.floor(nw.y / TileSize / scale) - extend;
    const maxTileY = Math.floor(se.y / TileSize / scale) + extend;

    for (let x = minTileX; x <= maxTileX; x++) {
        for (let y = minTileY; y <= maxTileY; y++) {
            const url = `/public/${currentSlug}/tiles/${zoom}/${x}_${y}.webp`;
            if (!preloadedTiles.has(url)) {
                tiles.push(url);
            }
        }
    }

    return tiles;
}

/**
 * Preload tiles with concurrency limit
 */
async function preloadTilesWithLimit(urls, limit) {
    const queue = [...urls];
    const active = [];

    while (queue.length > 0 || active.length > 0) {
        // Start new fetches up to limit
        while (active.length < limit && queue.length > 0) {
            const url = queue.shift();
            preloadedTiles.add(url);

            const promise = fetch(url, { priority: 'low' })
                .then(() => {
                    active.splice(active.indexOf(promise), 1);
                })
                .catch(() => {
                    active.splice(active.indexOf(promise), 1);
                });

            active.push(promise);
        }

        // Wait for at least one to complete
        if (active.length > 0) {
            await Promise.race(active);
        }
    }
}

/**
 * Preload tiles in background after map is ready
 */
async function startBackgroundPreload() {
    if (!PRELOAD_CONFIG.enabled || !mapInstance || !currentSlug) return;

    // Get current viewport bounds
    const bounds = mapInstance.getBounds();
    const zoom = mapInstance.getZoom();

    // Calculate tile coordinates for extended viewport
    const tilesToPreload = calculateTilesToPreload(bounds, zoom);

    if (tilesToPreload.length > 0) {
        console.log(`[PublicMap] Preloading ${tilesToPreload.length} tiles for zoom ${zoom}`);

        // Preload with concurrency limit
        await preloadTilesWithLimit(tilesToPreload, PRELOAD_CONFIG.concurrency);
    }

    // Optionally preload adjacent zoom levels
    if (PRELOAD_CONFIG.preloadAdjacentZooms) {
        const adjacentZooms = [zoom - 1, zoom + 1].filter(z => z >= HnHMinZoom && z <= HnHMaxZoom);
        for (const adjZoom of adjacentZooms) {
            const adjTiles = calculateTilesToPreload(bounds, adjZoom);
            if (adjTiles.length > 0) {
                // Limit adjacent zoom preloading to avoid excessive requests
                const limitedTiles = adjTiles.slice(0, 50);
                console.log(`[PublicMap] Preloading ${limitedTiles.length} tiles for adjacent zoom ${adjZoom}`);
                await preloadTilesWithLimit(limitedTiles, PRELOAD_CONFIG.concurrency);
            }
        }
    }
}

// Public Map Tile Layer - simple version that passes coordinates through directly
// like the main map layer does (no offset, Leaflet coords = HnH coords)
const PublicTileLayer = L.TileLayer.extend({
    slug: '',

    getTileUrl: function (coords) {
        if (!this.slug) {
            return L.Util.emptyImageUrl;
        }

        // Leaflet's _getZoomForUrl already handles zoomReverse
        const hnhZoom = this._getZoomForUrl();

        // Pass coordinates directly through (no offset, like main map layer)
        const x = coords.x;
        const y = coords.y;

        // Build URL for public tiles (WebP for better compression)
        return `/public/${this.slug}/tiles/${hnhZoom}/${x}_${y}.webp`;
    }
});

export async function initializePublicMap(mapElement, slug, centerX, centerY, initialZoom, minX, maxX, minY, maxY) {
    console.log('[PublicMap] Initializing for slug:', slug,
        'center:', { centerX, centerY },
        'initialZoom:', initialZoom,
        'bounds:', { minX, maxX, minY, maxY });
    currentSlug = slug;

    // Ensure DOM element is ready
    await new Promise(resolve => {
        requestAnimationFrame(() => {
            try {
                // Create map with smooth interactions enabled
                mapInstance = L.map(mapElement, {
                    minZoom: HnHMinZoom,
                    maxZoom: HnHMaxZoom,
                    crs: HnHCRS,
                    attributionControl: false,
                    zoomAnimation: true,
                    fadeAnimation: true,        // Smooth tile fade-in
                    zoomAnimationThreshold: 4   // Disable animation for large zoom jumps
                });

                // Create tile layer with optimized settings for smooth experience
                tileLayer = new PublicTileLayer('', {
                    tileSize: TileSize,
                    maxZoom: HnHMaxZoom,
                    minZoom: HnHMinZoom,
                    zoomReverse: true,
                    updateWhenZooming: true,    // Load tiles during zoom for smoother experience
                    updateWhenIdle: false,      // Don't wait for idle - load immediately
                    keepBuffer: 10,             // Keep many tiles in memory for smooth panning
                    updateInterval: 50,         // Fast tile updates for responsiveness
                    zoomAnimationThreshold: 4   // Disable animation for large zooms
                });

                tileLayer.slug = slug;
                tileLayer.addTo(mapInstance);

                // Create marker layer for thingwalls
                markerLayer = L.layerGroup();
                markerLayer.addTo(mapInstance);

                // Calculate center position in pixels and convert to LatLng
                // centerX/centerY are in original grid coordinates (100x100 units)
                // Convert to pixel position: grid coord * BaseTileSize (100) gives absolute pixels
                // The tile layer uses TileSize (400) for its grid, so we use absolute pixels directly
                const centerPixelX = (centerX + 0.5) * BaseTileSize;
                const centerPixelY = (centerY + 0.5) * BaseTileSize;
                const centerLatLng = mapInstance.unproject([centerPixelX, centerPixelY], HnHMaxZoom);

                // Set view to calculated center with appropriate zoom
                mapInstance.setView(centerLatLng, initialZoom);

                // Add dragend handler to update URL when user pans the map
                mapInstance.on('dragend', () => {
                    updateUrlWithPosition();
                });

                // Add zoomend handler with debounce to update URL when user zooms
                mapInstance.on('zoomend', () => {
                    clearTimeout(zoomDebounceTimer);
                    zoomDebounceTimer = setTimeout(() => {
                        updateUrlWithPosition();
                    }, 300);

                    // Also trigger preload for new zoom level
                    clearTimeout(preloadDebounceTimer);
                    preloadDebounceTimer = setTimeout(startBackgroundPreload, 1000);
                });

                // Add dragend handler to preload tiles after panning
                mapInstance.on('dragend', () => {
                    clearTimeout(preloadDebounceTimer);
                    preloadDebounceTimer = setTimeout(startBackgroundPreload, 500);
                });

                console.log('[PublicMap] Initialized at center:', centerLatLng, 'zoom:', initialZoom);

                // Start background preloading after a short delay to let the map render first
                setTimeout(startBackgroundPreload, 500);

                resolve();
            } catch (ex) {
                console.error('[PublicMap] Failed to initialize:', ex);
                resolve();
            }
        });
    });
}

/**
 * Load and display thingwall markers from data passed by Blazor
 * @param {Array} markersData - Array of marker objects with id, name, x, y, image
 */
export function loadMarkersData(markersData) {
    if (!mapInstance || !markerLayer) {
        console.warn('[PublicMap] Map not initialized, cannot load markers');
        return;
    }

    console.log('[PublicMap] Loading', markersData.length, 'thingwall markers');

    // Clear existing markers
    markerLayer.clearLayers();

    // Add each marker with highlighted thingwall styling
    markersData.forEach(markerData => {
        // Convert absolute pixel position to LatLng
        const position = mapInstance.unproject([markerData.x, markerData.y], HnHMaxZoom);

        // Create icon from marker image path
        const iconUrl = `/${markerData.image}.png`;
        const icon = L.icon({
            iconUrl: iconUrl,
            iconSize: [36, 36],
            iconAnchor: [18, 18]
        });

        const marker = L.marker(position, { icon: icon });

        // Bind tooltip with thingwall label styling (always visible with cyan color)
        marker.bindTooltip(markerData.name, {
            permanent: true,
            direction: 'top',
            offset: [0, -18],
            className: 'thingwall-label'
        });

        // Add highlight class when marker is added to map
        marker.on('add', () => {
            const el = marker.getElement();
            if (el) {
                el.classList.add('thingwall-highlighted');
            }
        });

        marker.addTo(markerLayer);
    });

    console.log('[PublicMap] Rendered', markersData.length, 'markers');
}

export function updateTileUrl(slug) {
    console.log('[PublicMap] Updating tile URL for slug:', slug);
    currentSlug = slug;

    if (tileLayer) {
        tileLayer.slug = slug;
        tileLayer.redraw();
    }
}

export function dispose() {
    console.log('[PublicMap] Disposing');

    // Clear debounce timers
    clearTimeout(zoomDebounceTimer);
    zoomDebounceTimer = null;
    clearTimeout(preloadDebounceTimer);
    preloadDebounceTimer = null;

    // Clear preloaded tiles tracking
    preloadedTiles.clear();

    if (markerLayer) {
        markerLayer.clearLayers();
        markerLayer.remove();
        markerLayer = null;
    }

    if (tileLayer) {
        tileLayer.remove();
        tileLayer = null;
    }

    if (mapInstance) {
        mapInstance.remove();
        mapInstance = null;
    }

    currentSlug = null;
}
