// Custom Marker Manager Module
// Handles user-placed custom marker management

import { TileSize, BaseTileSize, HnHMaxZoom, HnHMinZoom } from './leaflet-config.js';

// Custom marker storage
const customMarkers = {};
let currentMapId = 0;
let customMarkerLayer = null;
let customMarkersInitialized = false;
let pendingCustomMarkers = [];

// Safely invoke .NET methods from JS
let invokeDotNetSafe = null;

/**
 * Initialize custom marker manager
 * @param {object} layer - Custom marker layer group
 * @param {function} invokeFunc - Function to invoke .NET methods
 */
export function initializeCustomMarkerManager(layer, invokeFunc) {
    console.log('[CustomMarker] Initializing with invokeFunc:', typeof invokeFunc, invokeFunc);
    customMarkerLayer = layer;
    invokeDotNetSafe = invokeFunc;
    console.log('[CustomMarker] invokeDotNetSafe set to:', typeof invokeDotNetSafe, invokeDotNetSafe);
    customMarkersInitialized = true;
    flushCustomMarkerQueue();
}

/**
 * Set the current map ID for marker filtering
 */
export function setCurrentMapId(mapId) {
    currentMapId = mapId;
}

/**
 * Check if custom marker layer is ready
 * @returns {boolean} - True if initialized
 */
export function isCustomMarkerLayerReady() {
    return customMarkersInitialized;
}

/**
 * Add a custom marker to the map with distinct styling
 * @param {object} marker - Custom marker object with Id, MapId, CoordX, CoordY, X, Y, Title, Icon, PlacedAt, etc.
 * @param {object} mapInstance - Leaflet map instance
 */
export function addCustomMarker(marker, mapInstance) {
    if (!customMarkerLayer || !mapInstance) return;

    const normalized = normalizeCustomMarker(marker);

    if (!normalized) {
        console.warn('[CustomMarker] Unable to normalize marker payload', marker);
        return;
    }

    if (!customMarkersInitialized) {
        pendingCustomMarkers.push(normalized);
        return;
    }

    if (normalized.mapId && normalized.mapId !== currentMapId) {
        pendingCustomMarkers.push(normalized);
        return;
    }

    // Calculate absolute position from grid + local coords (use BaseTileSize for 100x100 game grid)
    const absX = normalized.coordX * BaseTileSize + normalized.x;
    const absY = normalized.coordY * BaseTileSize + normalized.y;

    // Defensive check: mapInstance might be destroyed or circuit not ready
    try {
        if (!mapInstance.unproject) {
            console.warn('[CustomMarker] mapInstance.unproject missing, marker skipped:', normalized.id);
            return;
        }
    } catch (e) {
        console.warn('[CustomMarker] Error checking mapInstance, marker skipped:', e);
        return;
    }

    try {
        const latlng = mapInstance.unproject([absX, absY], HnHMaxZoom);

        const iconSrc = resolveIconPath(normalized.icon);
        // Check for timerText in both camelCase and PascalCase (from C#)
        const timerText = marker.timerText || marker.TimerText || null;

        // Create custom marker icon with wrapper for distinct styling
        let iconHtml, iconSize, iconAnchor;

        if (timerText) {
            // Custom marker with timer - add timer border and text
            iconHtml = `
                <div class="marker-with-timer">
                    <img src="${iconSrc}"
                         class="marker-icon-img"
                         alt="${normalized.title}"
                         style="width: 32px; height: 32px; object-fit: contain; display: block;"
                         onerror="this.onerror=null;this.src='/gfx/terobjs/mm/custom.png';" />
                    <div class="marker-timer-text">${timerText}</div>
                </div>
            `;
            iconSize = [32, 44]; // Add 12px for timer text
            iconAnchor = [16, 28]; // Adjust anchor for timer text
        } else {
            // Custom marker without timer - standard styling
            iconHtml = `
                <div class="custom-marker" data-marker-id="${normalized.id}">
                    <img src="${iconSrc}"
                         alt="${normalized.title}"
                         style="width: 32px; height: 32px; object-fit: contain; display: block;"
                         onerror="this.onerror=null;this.src='/gfx/terobjs/mm/custom.png';" />
                </div>
            `;
            iconSize = [32, 32];
            iconAnchor = [16, 16];
        }

        const customIcon = L.divIcon({
            html: iconHtml,
            className: timerText ? 'marker-timer-container' : '', // Empty to avoid default leaflet-div-icon class
            iconSize: iconSize,
            iconAnchor: iconAnchor
        });

        // Build popup HTML with full marker details
        const descriptionHtml = normalized.description
            ? `<p style="margin: 4px 0; white-space: pre-wrap; color: #333;">${normalized.description}</p>`
            : '';

        const popupHtml = `
            <div style="min-width: 200px; background: white; padding: 12px; border-radius: 4px;">
                <h4 style="margin: 0 0 8px 0; font-size: 1.1em; color: #333;">${normalized.title}</h4>
                ${descriptionHtml}
                <div style="font-size: 0.85em; color: #666; margin-top: 8px; border-top: 1px solid #ddd; padding-top: 8px;">
                    <div style="margin: 2px 0;"><strong>Location:</strong> Grid (${normalized.coordX}, ${normalized.coordY}) + (${normalized.x}, ${normalized.y})</div>
                    <div style="margin: 2px 0;"><strong>Created by:</strong> ${normalized.createdBy}</div>
                    <div style="margin: 2px 0;"><strong>Placed:</strong> ${normalized.relativeTime}</div>
                </div>
            </div>
        `;

        // Create marker with both tooltip (hover) and popup (click)
        const leafletMarker = L.marker(latlng, { icon: customIcon })
            .bindTooltip(`<strong>${normalized.title}</strong><br/>${normalized.relativeTime}`, {
                direction: 'top',
                offset: [0, -16]
            })
            .bindPopup(popupHtml, {
                maxWidth: 300,
                minWidth: 200,
                className: 'custom-marker-popup'
            });

        // Add click handlers
        leafletMarker.on('click', () => {
            // Popup will open automatically due to bindPopup, but we can ensure it here
            leafletMarker.openPopup();
        });

        leafletMarker.on('contextmenu', (e) => {
            L.DomEvent.stopPropagation(e);
            // Pass screen coordinates for proper context menu positioning
            const screenX = Math.floor(e.containerPoint.x);
            const screenY = Math.floor(e.containerPoint.y);

            // Guard check: ensure invokeDotNetSafe is available
            if (typeof invokeDotNetSafe === 'function') {
                invokeDotNetSafe('JsOnCustomMarkerContextMenu', normalized.id, screenX, screenY);
            } else {
                console.warn('[CustomMarker] invokeDotNetSafe not available for context menu on marker', normalized.id);
            }
        });

        // Store and add to layer
        customMarkers[normalized.id] = leafletMarker;
        customMarkerLayer.addLayer(leafletMarker);
    } catch (err) {
        console.error('[CustomMarker] Error adding custom marker:', err, normalized);
        // Don't crash the circuit - just skip this marker
    }
}

/**
 * Flush queued custom markers (add markers that were waiting for map switch)
 * @param {object} mapInstance - Leaflet map instance
 */
export function flushCustomMarkerQueue(mapInstance) {
    if (!customMarkersInitialized || !mapInstance || pendingCustomMarkers.length === 0) {
        return;
    }

    const remaining = [];
    for (const marker of pendingCustomMarkers) {
        if (marker.mapId && marker.mapId !== currentMapId) {
            remaining.push(marker);
            continue;
        }

        addCustomMarker(marker, mapInstance);
    }

    pendingCustomMarkers = remaining;
}

/**
 * Update an existing custom marker
 * @param {number} markerId - Marker ID
 * @param {object} marker - Updated marker object
 * @param {object} mapInstance - Leaflet map instance
 */
export function updateCustomMarker(markerId, marker, mapInstance) {
    // Remove old marker and add updated one
    removeCustomMarker(markerId);
    addCustomMarker(marker, mapInstance);
}

/**
 * Remove a custom marker from the map
 * @param {number} markerId - Marker ID to remove
 */
export function removeCustomMarker(markerId) {
    if (!customMarkerLayer) return;

    const marker = customMarkers[markerId];
    if (marker) {
        customMarkerLayer.removeLayer(marker);
        delete customMarkers[markerId];
    }
}

/**
 * Clear all custom markers from the map
 */
export function clearAllCustomMarkers() {
    if (!customMarkerLayer) return;

    customMarkerLayer.clearLayers();
    Object.keys(customMarkers).forEach(id => delete customMarkers[id]);
}

/**
 * Toggle custom marker layer visibility
 * @param {boolean} visible - Whether to show custom markers
 * @param {object} mapInstance - Leaflet map instance
 */
export function toggleCustomMarkers(visible, mapInstance) {
    if (!customMarkerLayer || !mapInstance) return;

    if (visible) {
        if (!mapInstance.hasLayer(customMarkerLayer)) {
            customMarkerLayer.addTo(mapInstance);
        }
    } else {
        if (mapInstance.hasLayer(customMarkerLayer)) {
            mapInstance.removeLayer(customMarkerLayer);
        }
    }
}

/**
 * Jump map view to a custom marker
 * @param {number} markerId - Marker ID to jump to
 * @param {number} zoomLevel - Zoom level to use
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if marker was found
 */
export function jumpToCustomMarker(markerId, zoomLevel, mapInstance) {
    const marker = customMarkers[markerId];
    if (!marker) {
        console.warn('[CustomMarker] jumpToCustomMarker failed; marker not found', markerId, {
            knownMarkers: Object.keys(customMarkers)
        });
        return false;
    }

    const latlng = marker.getLatLng();
    let targetZoom = mapInstance.getZoom();
    if (typeof zoomLevel === 'number' && Number.isFinite(zoomLevel)) {
        targetZoom = Math.min(Math.max(zoomLevel, HnHMinZoom), HnHMaxZoom);
    } else {
        targetZoom = Math.max(targetZoom, 5);
    }

    mapInstance.setView(latlng, targetZoom);

    if (typeof marker.openPopup === 'function') {
        marker.openPopup();
    }

    if (typeof marker.openTooltip === 'function') {
        marker.openTooltip();
    }

    return true;
}

// Helper functions

function resolveIconPath(icon) {
    if (!icon || typeof icon !== 'string') {
        return '/gfx/terobjs/mm/custom.png';
    }

    const trimmed = icon.trim();
    if (trimmed.length === 0) {
        return '/gfx/terobjs/mm/custom.png';
    }

    return trimmed.startsWith('/') ? trimmed : `/${trimmed}`;
}

function normalizeCustomMarker(marker) {
    if (!marker || typeof marker !== 'object') {
        return null;
    }

    const coerceNumber = (value, fallback = 0) => {
        if (typeof value === 'number') {
            return Number.isFinite(value) ? value : fallback;
        }
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : fallback;
    };

    const coerceBool = (value, fallback = false) => {
        if (typeof value === 'boolean') return value;
        if (typeof value === 'string') {
            if (value.toLowerCase() === 'true') return true;
            if (value.toLowerCase() === 'false') return false;
        }
        return fallback;
    };

    const resolve = (lower, upper, fallback = undefined) => {
        if (lower !== undefined) return lower;
        if (upper !== undefined) return upper;
        return fallback;
    };

    const id = coerceNumber(resolve(marker.id, marker.Id), 0);
    const mapId = coerceNumber(resolve(marker.mapId, marker.MapId), 0);
    const coordX = coerceNumber(resolve(marker.coordX, marker.CoordX), 0);
    const coordY = coerceNumber(resolve(marker.coordY, marker.CoordY), 0);
    const x = coerceNumber(resolve(marker.x, marker.X), 0);
    const y = coerceNumber(resolve(marker.y, marker.Y), 0);

    const title = (resolve(marker.title, marker.Title, '') ?? '').toString();
    const description = resolve(marker.description, marker.Description, null);
    const icon = (resolve(marker.icon, marker.Icon, '') ?? '').toString();
    const createdBy = (resolve(marker.createdBy, marker.CreatedBy, '') ?? '').toString();
    const relativeTime = (resolve(marker.relativeTime, marker.RelativeTime, '') ?? '').toString();
    const hidden = coerceBool(resolve(marker.hidden, marker.Hidden), false);

    const placedAt = resolve(marker.placedAt, marker.PlacedAt, null);
    const updatedAt = resolve(marker.updatedAt, marker.UpdatedAt, null);

    return {
        id,
        mapId,
        coordX,
        coordY,
        x,
        y,
        title,
        description,
        icon,
        createdBy,
        relativeTime,
        hidden,
        placedAt,
        updatedAt
    };
}
