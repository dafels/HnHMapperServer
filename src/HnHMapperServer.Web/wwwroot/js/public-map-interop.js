// Public Map Interop Module
// Simplified Leaflet map for public (unauthenticated) map viewing
// Displays tiles and thingwall markers with highlighted styling

// Map constants (same as main map)
const TileSize = 100;
const HnHMaxZoom = 7;
const HnHMinZoom = 1;

// Coordinate normalization factors
const latNormalization = 90.0 * TileSize / 2500000.0;
const lngNormalization = 180.0 * TileSize / 2500000.0;

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

        // Build URL for public tiles
        return `/public/${this.slug}/tiles/${hnhZoom}/${x}_${y}.png`;
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
                // Create map
                mapInstance = L.map(mapElement, {
                    minZoom: HnHMinZoom,
                    maxZoom: HnHMaxZoom,
                    crs: HnHCRS,
                    attributionControl: false,
                    inertia: false,
                    zoomAnimation: true,
                    fadeAnimation: false
                });

                // Create tile layer
                tileLayer = new PublicTileLayer('', {
                    tileSize: TileSize,
                    maxZoom: HnHMaxZoom,
                    minZoom: HnHMinZoom,
                    zoomReverse: true,
                    updateWhenZooming: false,
                    updateWhenIdle: true,
                    keepBuffer: 2
                });

                tileLayer.slug = slug;
                tileLayer.addTo(mapInstance);

                // Create marker layer for thingwalls
                markerLayer = L.layerGroup();
                markerLayer.addTo(mapInstance);

                // Calculate center position in pixels and convert to LatLng
                // centerX/centerY are tile coordinates, convert to pixel position (center of tile)
                const centerPixelX = (centerX + 0.5) * TileSize;
                const centerPixelY = (centerY + 0.5) * TileSize;
                const centerLatLng = mapInstance.unproject([centerPixelX, centerPixelY], HnHMaxZoom);

                // Set view to calculated center with appropriate zoom
                mapInstance.setView(centerLatLng, initialZoom);

                console.log('[PublicMap] Initialized at center:', centerLatLng, 'zoom:', initialZoom);
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
