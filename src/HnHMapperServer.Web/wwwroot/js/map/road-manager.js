// Road Manager Module
// Handles user-drawn road/path management

import { TileSize, BaseTileSize, HnHMaxZoom, HnHMinZoom } from './leaflet-config.js';

// Road storage
const roads = {};
let currentMapId = 0;
let roadLayer = null;
let roadsInitialized = false;
let pendingRoads = [];

// Drawing state
let isDrawingRoad = false;
let currentDrawingPoints = [];
let tempPolyline = null;
let tempMarkers = [];

// Selection state
let selectedRoadId = null;
let selectionTimeout = null;
let selectionLabelMarker = null;
const SELECTION_FLASH_DURATION = 1500; // 3 flashes at 0.5s each
const SELECTION_DISPLAY_DURATION = 5000; // How long to keep selection visible

// Safely invoke .NET methods from JS
let invokeDotNetSafe = null;
let mapInstanceRef = null;

// Color palette for unique road colors (16 distinct colors)
const ROAD_COLOR_PALETTE = [
    '#FF6B6B',  // Coral red
    '#4ECDC4',  // Teal
    '#FFE66D',  // Yellow
    '#95E1D3',  // Mint
    '#F38181',  // Salmon
    '#AA96DA',  // Lavender
    '#FCBAD3',  // Pink
    '#A8D8EA',  // Light blue
    '#FF9F43',  // Orange
    '#5CD85A',  // Green
    '#DDA0DD',  // Plum
    '#87CEEB',  // Sky blue
    '#F0E68C',  // Khaki
    '#98D8C8',  // Seafoam
    '#C9B1FF',  // Periwinkle
    '#FFB6C1'   // Light pink
];

/**
 * Get deterministic color for a road based on its ID
 * @param {number} roadId - Road ID
 * @returns {string} - Hex color code
 */
function getRoadColor(roadId) {
    return ROAD_COLOR_PALETTE[roadId % ROAD_COLOR_PALETTE.length];
}

// Visual styles (hover only - base style uses per-road color)
const ROAD_HOVER_STYLE = {
    weight: 6,
    opacity: 1
};

const WAYPOINT_COLORS = {
    start: '#22C55E',      // Green
    intermediate: '#3B82F6', // Blue
    end: '#EF4444'          // Red
};

/**
 * Initialize road manager
 * @param {object} layer - Road layer group
 * @param {function} invokeFunc - Function to invoke .NET methods
 */
export function initializeRoadManager(layer, invokeFunc) {
    console.log('[Road] Initializing with invokeFunc:', typeof invokeFunc);
    roadLayer = layer;
    invokeDotNetSafe = invokeFunc;
    roadsInitialized = true;
    flushRoadQueue();
}

/**
 * Set the current map ID for road filtering
 */
export function setCurrentMapId(mapId) {
    currentMapId = mapId;
}

/**
 * Set map instance reference
 */
export function setMapInstance(instance) {
    mapInstanceRef = instance;
}

/**
 * Convert waypoint to Leaflet LatLng
 */
function waypointToLatLng(wp, mapInstance) {
    // Use BaseTileSize for 100x100 game grid coordinates
    const absX = wp.coordX * BaseTileSize + wp.x;
    const absY = wp.coordY * BaseTileSize + wp.y;
    return mapInstance.unproject([absX, absY], HnHMaxZoom);
}

/**
 * Convert waypoints array to LatLng array
 */
function waypointsToLatLngs(waypoints, mapInstance) {
    return waypoints.map(wp => waypointToLatLng(wp, mapInstance));
}

/**
 * Create waypoint markers for display (start=green, end=red, intermediate=blue)
 * @param {array} waypoints - Array of waypoint DTOs
 * @param {object} mapInstance - Leaflet map instance
 * @returns {array} - Array of circle markers
 */
function createWaypointMarkers(waypoints, mapInstance) {
    if (!waypoints || waypoints.length < 2) return [];

    const markers = [];
    waypoints.forEach((wp, index) => {
        const latlng = waypointToLatLng(wp, mapInstance);
        let color, radius;

        if (index === 0) {
            color = WAYPOINT_COLORS.start;
            radius = 8;
        } else if (index === waypoints.length - 1) {
            color = WAYPOINT_COLORS.end;
            radius = 8;
        } else {
            color = WAYPOINT_COLORS.intermediate;
            radius = 5;
        }

        const marker = L.circleMarker(latlng, {
            radius: radius,
            color: color,
            fillColor: color,
            fillOpacity: 0.8,
            weight: 2
        });
        markers.push(marker);
    });

    return markers;
}

/**
 * Add a road to the map
 * @param {object} road - Road object with id, mapId, name, waypoints, createdBy, etc.
 * @param {object} mapInstance - Leaflet map instance
 */
export function addRoad(road, mapInstance) {
    if (!roadLayer || !mapInstance) return;

    const normalized = normalizeRoad(road);

    if (!normalized) {
        console.warn('[Road] Unable to normalize road payload', road);
        return;
    }

    if (!roadsInitialized) {
        pendingRoads.push(road);
        return;
    }

    if (normalized.mapId && normalized.mapId !== currentMapId) {
        pendingRoads.push(road);
        return;
    }

    if (!normalized.waypoints || normalized.waypoints.length < 2) {
        console.warn('[Road] Road has insufficient waypoints', normalized.id);
        return;
    }

    try {
        const latlngs = waypointsToLatLngs(normalized.waypoints, mapInstance);

        // Get unique color for this road based on ID
        const roadColor = getRoadColor(normalized.id);

        // Create polyline for the road with unique color
        const polyline = L.polyline(latlngs, {
            color: roadColor,
            weight: 4,
            opacity: 0.85,
            interactive: true
        });

        // Create road label (shown along the line)
        polyline.bindTooltip(normalized.name, {
            permanent: false,
            sticky: true,
            direction: 'top',
            offset: [0, -10],
            className: 'road-tooltip'
        });

        // Create waypoint markers (hidden by default, shown on hover or selection)
        const waypointMarkers = createWaypointMarkers(normalized.waypoints, mapInstance);
        const waypointLayer = L.layerGroup(waypointMarkers);

        // Hover effect - highlight road and show waypoints (unless selected)
        polyline.on('mouseover', () => {
            polyline.setStyle(ROAD_HOVER_STYLE);
            // Only show waypoints on hover if this road is not currently selected
            if (selectedRoadId !== normalized.id && !mapInstance.hasLayer(waypointLayer)) {
                waypointLayer.addTo(mapInstance);
            }
        });

        polyline.on('mouseout', () => {
            // Restore original style (unless selected)
            if (selectedRoadId !== normalized.id) {
                polyline.setStyle({
                    color: roadColor,
                    weight: 4,
                    opacity: 0.85
                });
                if (mapInstance.hasLayer(waypointLayer)) {
                    mapInstance.removeLayer(waypointLayer);
                }
            }
        });

        // Context menu for editing/deleting
        polyline.on('contextmenu', (e) => {
            L.DomEvent.stopPropagation(e);
            const screenX = Math.floor(e.containerPoint.x);
            const screenY = Math.floor(e.containerPoint.y);

            if (typeof invokeDotNetSafe === 'function') {
                invokeDotNetSafe('JsOnRoadContextMenu', normalized.id, screenX, screenY);
            }
        });

        // Store road data with color for restoration after hover/selection
        roads[normalized.id] = {
            polyline,
            waypointLayer,
            data: normalized,
            color: roadColor
        };

        roadLayer.addLayer(polyline);
    } catch (err) {
        console.error('[Road] Error adding road:', err, normalized);
    }
}

/**
 * Flush queued roads (add roads that were waiting for map switch)
 * @param {object} mapInstance - Leaflet map instance
 */
export function flushRoadQueue(mapInstance) {
    if (!roadsInitialized || !mapInstance || pendingRoads.length === 0) {
        return;
    }

    const remaining = [];
    for (const road of pendingRoads) {
        const normalized = normalizeRoad(road);
        if (normalized && normalized.mapId && normalized.mapId !== currentMapId) {
            remaining.push(road);
            continue;
        }
        addRoad(road, mapInstance);
    }

    pendingRoads = remaining;
}

/**
 * Update an existing road
 * @param {number} roadId - Road ID
 * @param {object} road - Updated road object
 * @param {object} mapInstance - Leaflet map instance
 */
export function updateRoad(roadId, road, mapInstance) {
    removeRoad(roadId);
    addRoad(road, mapInstance);
}

/**
 * Remove a road from the map
 * @param {number} roadId - Road ID to remove
 */
export function removeRoad(roadId) {
    if (!roadLayer) return;

    const roadData = roads[roadId];
    if (roadData) {
        roadLayer.removeLayer(roadData.polyline);
        if (roadData.waypointLayer && mapInstanceRef) {
            mapInstanceRef.removeLayer(roadData.waypointLayer);
        }
        delete roads[roadId];
    }
}

/**
 * Clear all roads from the map
 */
export function clearAllRoads() {
    if (!roadLayer) return;

    roadLayer.clearLayers();
    Object.keys(roads).forEach(id => {
        if (roads[id].waypointLayer && mapInstanceRef) {
            mapInstanceRef.removeLayer(roads[id].waypointLayer);
        }
        delete roads[id];
    });
}

/**
 * Toggle road layer visibility
 * @param {boolean} visible - Whether to show roads
 * @param {object} mapInstance - Leaflet map instance
 */
export function toggleRoads(visible, mapInstance) {
    if (!roadLayer || !mapInstance) return;

    if (visible) {
        if (!mapInstance.hasLayer(roadLayer)) {
            roadLayer.addTo(mapInstance);
        }
    } else {
        if (mapInstance.hasLayer(roadLayer)) {
            mapInstance.removeLayer(roadLayer);
        }
        // Also hide any visible waypoint layers
        Object.values(roads).forEach(roadData => {
            if (roadData.waypointLayer && mapInstance.hasLayer(roadData.waypointLayer)) {
                mapInstance.removeLayer(roadData.waypointLayer);
            }
        });
    }
}

/**
 * Jump map view to a road (legacy - prefer selectRoad for visual feedback)
 * @param {number} roadId - Road ID to jump to
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if road was found
 */
export function jumpToRoad(roadId, mapInstance) {
    const roadData = roads[roadId];
    if (!roadData) {
        console.warn('[Road] jumpToRoad failed; road not found', roadId);
        return false;
    }

    const bounds = roadData.polyline.getBounds();
    mapInstance.fitBounds(bounds, { padding: [50, 50] });
    return true;
}

/**
 * Select a road - flash highlight, show label and waypoints
 * @param {number} roadId - Road ID to select
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if road was found and selected
 */
export function selectRoad(roadId, mapInstance) {
    const roadData = roads[roadId];
    if (!roadData) {
        console.warn('[Road] selectRoad failed; road not found', roadId);
        return false;
    }

    // Clear previous selection if exists
    clearRoadSelection();

    // Store selection state
    selectedRoadId = roadId;

    // Apply selected style (brighter, thicker)
    roadData.polyline.setStyle({
        weight: 6,
        opacity: 1
    });

    // Get the polyline SVG element and add flash class
    const polylineElement = roadData.polyline.getElement();
    if (polylineElement) {
        polylineElement.classList.add('road-selected-flash');
    }

    // Show waypoint markers (without animation - just show them)
    if (!mapInstance.hasLayer(roadData.waypointLayer)) {
        roadData.waypointLayer.addTo(mapInstance);
    }

    // Create a prominent label at the center of the road
    const latlngs = roadData.polyline.getLatLngs();
    if (latlngs && latlngs.length > 0) {
        // Find the middle point of the road
        const midIndex = Math.floor(latlngs.length / 2);
        const midPoint = latlngs[midIndex];

        // Create a label marker with the road name
        selectionLabelMarker = L.marker(midPoint, {
            icon: L.divIcon({
                className: 'road-selection-label-container',
                html: `<div class="road-selected-label" style="border-color: ${roadData.color}; box-shadow: 0 0 12px ${roadData.color}, 0 4px 12px rgba(0,0,0,0.5);">${roadData.data.name}</div>`,
                iconSize: null,
                iconAnchor: [0, 0]
            }),
            interactive: false,
            zIndexOffset: 1000
        });
        selectionLabelMarker.addTo(mapInstance);
    }

    // Clear selection after timeout
    selectionTimeout = setTimeout(() => {
        if (selectedRoadId === roadId) {
            // Remove flash class
            if (polylineElement) {
                polylineElement.classList.remove('road-selected-flash');
            }

            // Hide waypoints
            if (mapInstance.hasLayer(roadData.waypointLayer)) {
                mapInstance.removeLayer(roadData.waypointLayer);
            }

            // Remove label
            if (selectionLabelMarker && mapInstance.hasLayer(selectionLabelMarker)) {
                mapInstance.removeLayer(selectionLabelMarker);
                selectionLabelMarker = null;
            }

            // Reset style
            roadData.polyline.setStyle({
                color: roadData.color,
                weight: 4,
                opacity: 0.85
            });

            selectedRoadId = null;
        }
    }, SELECTION_DISPLAY_DURATION);

    // Fit bounds to road
    const bounds = roadData.polyline.getBounds();
    mapInstance.fitBounds(bounds, { padding: [50, 50] });

    console.log('[Road] Selected road:', roadId, 'color:', roadData.color);
    return true;
}

/**
 * Clear current road selection
 */
export function clearRoadSelection() {
    // Clear timeout
    if (selectionTimeout) {
        clearTimeout(selectionTimeout);
        selectionTimeout = null;
    }

    // Remove label marker
    if (selectionLabelMarker && mapInstanceRef && mapInstanceRef.hasLayer(selectionLabelMarker)) {
        mapInstanceRef.removeLayer(selectionLabelMarker);
        selectionLabelMarker = null;
    }

    // Reset previous selection if exists
    if (selectedRoadId !== null && roads[selectedRoadId]) {
        const roadData = roads[selectedRoadId];

        // Remove flash class
        const polylineElement = roadData.polyline.getElement();
        if (polylineElement) {
            polylineElement.classList.remove('road-selected-flash');
        }

        // Reset style to original color
        roadData.polyline.setStyle({
            color: roadData.color,
            weight: 4,
            opacity: 0.85
        });

        // Remove waypoint layer if visible
        if (mapInstanceRef && mapInstanceRef.hasLayer(roadData.waypointLayer)) {
            mapInstanceRef.removeLayer(roadData.waypointLayer);
        }
    }

    selectedRoadId = null;
}

// ============ Route Navigation Functions ============

// Route highlighting state
let routeHighlightedRoads = [];
let routeMarkers = [];
let startMarker = null;
let endMarker = null;

/**
 * Highlight a route (multiple roads) on the map
 * @param {array} roadIds - Array of road IDs in order
 * @param {object} startPoint - Start point {x, y} in absolute pixels
 * @param {object} endPoint - End point {x, y} in absolute pixels
 * @param {object} mapInstance - Leaflet map instance
 */
export function highlightRoute(roadIds, startPoint, endPoint, mapInstance) {
    if (!mapInstance || !roadLayer) return;

    console.log('[Road] highlightRoute called with', roadIds.length, 'road IDs:', roadIds);
    console.log('[Road] Available road IDs in roads object:', Object.keys(roads));

    // Clear any existing route highlight
    clearRouteHighlight(mapInstance);

    // Highlight each road in the route
    let highlightedCount = 0;
    roadIds.forEach((roadId, index) => {
        // Ensure roadId is the correct type (convert to number if needed)
        const normalizedId = typeof roadId === 'string' ? parseInt(roadId, 10) : roadId;

        let roadData = roads[normalizedId];

        // If not found, try string key as fallback
        if (!roadData) {
            roadData = roads[String(roadId)];
        }

        if (!roadData) {
            console.warn(`[Road] Road ${roadId} (normalized: ${normalizedId}) not found in roads object for highlighting`);
            return;
        }

        highlightedCount++;

        // Store original style
        const originalColor = roadData.color;

        // Apply route highlight style
        roadData.polyline.setStyle({
            weight: 8,
            opacity: 1
        });

        // Add route highlight class for animation
        const polylineElement = roadData.polyline.getElement();
        if (polylineElement) {
            polylineElement.classList.add('route-highlighted');
        }

        // Add numbered marker at the start of each road
        const endpoints = getRoadEndpointsForRoute(roadData.data);
        if (endpoints) {
            const markerPoint = endpoints.start;
            const latlng = mapInstance.unproject([markerPoint.x, markerPoint.y], HnHMaxZoom);

            const numberMarker = L.marker(latlng, {
                icon: L.divIcon({
                    className: 'route-number-marker',
                    html: `<div class="route-number">${index + 1}</div>`,
                    iconSize: [28, 28],
                    iconAnchor: [14, 14]
                }),
                interactive: false,
                zIndexOffset: 2000
            });
            numberMarker.addTo(mapInstance);
            routeMarkers.push(numberMarker);
        }

        routeHighlightedRoads.push({
            roadId,
            originalColor,
            polyline: roadData.polyline
        });
    });

    // Add start marker (green)
    if (startPoint) {
        const startLatLng = mapInstance.unproject([startPoint.x, startPoint.y], HnHMaxZoom);
        startMarker = L.marker(startLatLng, {
            icon: L.divIcon({
                className: 'route-start-marker',
                html: '<div class="route-endpoint-icon start">S</div>',
                iconSize: [32, 32],
                iconAnchor: [16, 16]
            }),
            interactive: false,
            zIndexOffset: 2500
        });
        startMarker.addTo(mapInstance);
    }

    // Add end marker (red)
    if (endPoint) {
        const endLatLng = mapInstance.unproject([endPoint.x, endPoint.y], HnHMaxZoom);
        endMarker = L.marker(endLatLng, {
            icon: L.divIcon({
                className: 'route-end-marker',
                html: '<div class="route-endpoint-icon end">D</div>',
                iconSize: [32, 32],
                iconAnchor: [16, 16]
            }),
            interactive: false,
            zIndexOffset: 2500
        });
        endMarker.addTo(mapInstance);
    }

    console.log('[Road] Route highlighted:', highlightedCount, 'of', roadIds.length, 'roads');
}

/**
 * Get road endpoints for route calculation
 * @param {object} roadData - Road data object
 * @returns {object} - {start: {x, y}, end: {x, y}}
 */
function getRoadEndpointsForRoute(roadData) {
    let waypoints = roadData.waypoints;
    if (typeof waypoints === 'string') {
        try {
            waypoints = JSON.parse(waypoints);
        } catch {
            return null;
        }
    }

    if (!waypoints || waypoints.length < 2) return null;

    const firstWp = waypoints[0];
    const lastWp = waypoints[waypoints.length - 1];

    // Use BaseTileSize for 100x100 game grid coordinates
    return {
        start: {
            x: (firstWp.coordX ?? firstWp.CoordX ?? 0) * BaseTileSize + (firstWp.x ?? firstWp.X ?? 0),
            y: (firstWp.coordY ?? firstWp.CoordY ?? 0) * BaseTileSize + (firstWp.y ?? firstWp.Y ?? 0)
        },
        end: {
            x: (lastWp.coordX ?? lastWp.CoordX ?? 0) * BaseTileSize + (lastWp.x ?? lastWp.X ?? 0),
            y: (lastWp.coordY ?? lastWp.CoordY ?? 0) * BaseTileSize + (lastWp.y ?? lastWp.Y ?? 0)
        }
    };
}

/**
 * Clear route highlighting
 * @param {object} mapInstance - Leaflet map instance
 */
export function clearRouteHighlight(mapInstance) {
    // Remove route highlight from roads
    routeHighlightedRoads.forEach(({ roadId, originalColor, polyline }) => {
        const polylineElement = polyline.getElement();
        if (polylineElement) {
            polylineElement.classList.remove('route-highlighted');
        }

        // Restore original style
        polyline.setStyle({
            color: originalColor,
            weight: 4,
            opacity: 0.85
        });
    });
    routeHighlightedRoads = [];

    // Remove number markers
    routeMarkers.forEach(marker => {
        if (mapInstance && mapInstance.hasLayer(marker)) {
            mapInstance.removeLayer(marker);
        }
    });
    routeMarkers = [];

    // Remove start/end markers
    if (startMarker && mapInstance && mapInstance.hasLayer(startMarker)) {
        mapInstance.removeLayer(startMarker);
        startMarker = null;
    }
    if (endMarker && mapInstance && mapInstance.hasLayer(endMarker)) {
        mapInstance.removeLayer(endMarker);
        endMarker = null;
    }

    console.log('[Road] Route highlight cleared');
}

/**
 * Check if a route is currently highlighted
 * @returns {boolean}
 */
export function hasRouteHighlight() {
    return routeHighlightedRoads.length > 0;
}

/**
 * Get all roads data for pathfinding
 * @returns {array} - Array of road objects
 */
export function getAllRoadsData() {
    return Object.values(roads).map(r => r.data);
}

// ============ Drawing Mode Functions ============

/**
 * Start drawing a new road
 * @param {object} mapInstance - Leaflet map instance
 */
export function startDrawingRoad(mapInstance) {
    if (!mapInstance) return;

    isDrawingRoad = true;
    currentDrawingPoints = [];
    mapInstanceRef = mapInstance;

    // Change cursor
    mapInstance.getContainer().style.cursor = 'crosshair';

    // Add click handler for adding points
    mapInstance.on('click', onDrawingClick);
    mapInstance.on('dblclick', onDrawingDoubleClick);

    // Prevent map zoom on double-click while drawing
    mapInstance.doubleClickZoom.disable();

    console.log('[Road] Drawing mode started');
}

/**
 * Handle click during drawing mode
 */
function onDrawingClick(e) {
    if (!isDrawingRoad) return;

    const mapInstance = e.target;
    const latlng = e.latlng;
    currentDrawingPoints.push(latlng);

    // Determine marker color based on position
    let color;
    if (currentDrawingPoints.length === 1) {
        color = WAYPOINT_COLORS.start;
    } else {
        // All subsequent points are intermediate (will update last to red on finish)
        color = WAYPOINT_COLORS.intermediate;
    }

    // Add temporary marker at click point
    const marker = L.circleMarker(latlng, {
        radius: 6,
        color: color,
        fillColor: color,
        fillOpacity: 0.8,
        weight: 2
    });
    tempMarkers.push(marker);
    marker.addTo(mapInstance);

    // Update temporary polyline
    if (tempPolyline) {
        tempPolyline.setLatLngs(currentDrawingPoints);
    } else if (currentDrawingPoints.length >= 2) {
        tempPolyline = L.polyline(currentDrawingPoints, {
            color: '#FFFFFF',
            weight: 3,
            dashArray: '8, 8',
            opacity: 0.7
        }).addTo(mapInstance);
    }

    console.log('[Road] Point added, total:', currentDrawingPoints.length);
}

/**
 * Handle double-click to finish drawing
 */
function onDrawingDoubleClick(e) {
    if (!isDrawingRoad || currentDrawingPoints.length < 2) return;
    L.DomEvent.stopPropagation(e);
    L.DomEvent.preventDefault(e);
    finishDrawingRoad(e.target);
}

/**
 * Finish drawing and return waypoints
 * @param {object} mapInstance - Leaflet map instance
 */
export function finishDrawingRoad(mapInstance) {
    if (!mapInstance || currentDrawingPoints.length < 2) {
        cancelDrawingRoad(mapInstance);
        return;
    }

    isDrawingRoad = false;
    mapInstance.getContainer().style.cursor = '';
    mapInstance.off('click', onDrawingClick);
    mapInstance.off('dblclick', onDrawingDoubleClick);
    mapInstance.doubleClickZoom.enable();

    // Convert latlngs to waypoint format (use BaseTileSize for 100x100 game grid)
    const waypoints = currentDrawingPoints.map(latlng => {
        const point = mapInstance.project(latlng, HnHMaxZoom);
        const coordX = Math.floor(point.x / BaseTileSize);
        const coordY = Math.floor(point.y / BaseTileSize);
        const x = Math.floor(((point.x % BaseTileSize) + BaseTileSize) % BaseTileSize);
        const y = Math.floor(((point.y % BaseTileSize) + BaseTileSize) % BaseTileSize);
        return { coordX, coordY, x, y };
    });

    // Remove temporary markers and polyline
    cleanupDrawingTemp(mapInstance);

    // Notify Blazor with waypoints
    console.log('[Road] Finishing drawing with', waypoints.length, 'waypoints, mapId:', currentMapId);
    console.log('[Road] invokeDotNetSafe type:', typeof invokeDotNetSafe);
    console.log('[Road] Waypoints:', JSON.stringify(waypoints));

    if (typeof invokeDotNetSafe === 'function') {
        try {
            invokeDotNetSafe('JsOnRoadDrawingComplete', currentMapId, waypoints);
            console.log('[Road] invokeDotNetSafe called successfully');
        } catch (err) {
            console.error('[Road] Error calling invokeDotNetSafe:', err);
        }
    } else {
        console.error('[Road] invokeDotNetSafe is NOT a function! Type:', typeof invokeDotNetSafe);
    }

    currentDrawingPoints = [];
}

/**
 * Cancel drawing mode
 * @param {object} mapInstance - Leaflet map instance
 */
export function cancelDrawingRoad(mapInstance) {
    if (!mapInstance) return;

    isDrawingRoad = false;
    mapInstance.getContainer().style.cursor = '';
    mapInstance.off('click', onDrawingClick);
    mapInstance.off('dblclick', onDrawingDoubleClick);
    mapInstance.doubleClickZoom.enable();

    cleanupDrawingTemp(mapInstance);
    currentDrawingPoints = [];

    console.log('[Road] Drawing cancelled');
}

/**
 * Check if currently in drawing mode
 */
export function isInDrawingMode() {
    return isDrawingRoad;
}

/**
 * Get current drawing points count
 */
export function getDrawingPointsCount() {
    return currentDrawingPoints.length;
}

/**
 * Clean up temporary drawing elements
 */
function cleanupDrawingTemp(mapInstance) {
    tempMarkers.forEach(m => {
        if (mapInstance.hasLayer(m)) {
            mapInstance.removeLayer(m);
        }
    });
    tempMarkers = [];

    if (tempPolyline) {
        if (mapInstance.hasLayer(tempPolyline)) {
            mapInstance.removeLayer(tempPolyline);
        }
        tempPolyline = null;
    }
}

// ============ Helper Functions ============

/**
 * Normalize road object from API response
 */
function normalizeRoad(road) {
    if (!road || typeof road !== 'object') {
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

    const id = coerceNumber(resolve(road.id, road.Id), 0);
    const mapId = coerceNumber(resolve(road.mapId, road.MapId), 0);
    const name = resolve(road.name, road.Name) || 'Unnamed Road';
    const hidden = coerceBool(resolve(road.hidden, road.Hidden), false);
    const createdBy = resolve(road.createdBy, road.CreatedBy) || 'Unknown';

    // Parse waypoints - handle both camelCase and PascalCase
    let waypoints = resolve(road.waypoints, road.Waypoints) || [];
    if (typeof waypoints === 'string') {
        try {
            waypoints = JSON.parse(waypoints);
        } catch {
            waypoints = [];
        }
    }

    // Normalize each waypoint
    waypoints = waypoints.map(wp => ({
        coordX: coerceNumber(resolve(wp.coordX, wp.CoordX), 0),
        coordY: coerceNumber(resolve(wp.coordY, wp.CoordY), 0),
        x: coerceNumber(resolve(wp.x, wp.X), 0),
        y: coerceNumber(resolve(wp.y, wp.Y), 0)
    }));

    return {
        id,
        mapId,
        name,
        waypoints,
        createdBy,
        hidden
    };
}
