// Marker Manager Module
// Handles game marker (resource, quest, etc.) management

import { HnHMaxZoom } from './leaflet-config.js';
import * as VoronoiAdjacency from './voronoi-adjacency.js';

// Marker storage - visible markers with their Leaflet instances
const markers = {};
// All marker data storage - persists regardless of visibility for re-evaluation
const allMarkerData = {};
let currentMapId = 0;
let markerLayer = null;
let detailedMarkerLayer = null;

// Hidden marker types (by image path)
const hiddenMarkerTypes = new Set();

// Thingwall highlighting state
let thingwallHighlightEnabled = false;

// Quest giver highlighting state
let questGiverHighlightEnabled = false;

// Marker filter mode state (hide all markers except highlighted ones)
let markerFilterModeEnabled = false;

// Thingwall tracking for Voronoi adjacency computation
const thingwallMarkers = {}; // thingwallId -> {marker, data}
let jumpConnectionsEnabled = false;

// Safely invoke .NET methods from JS
let invokeDotNetSafe = null;

/**
 * Initialize marker manager
 * @param {function} invokeFunc - Function to invoke .NET methods
 */
export function initializeMarkerManager(invokeFunc) {
    invokeDotNetSafe = invokeFunc;
}

/**
 * Set marker layers
 * @param {object} mainLayer - Main marker layer
 * @param {object} detailLayer - Detailed marker layer (for high zoom)
 */
export function setMarkerLayers(mainLayer, detailLayer) {
    markerLayer = mainLayer;
    detailedMarkerLayer = detailLayer;
}

/**
 * Set the current map ID for marker filtering
 */
export function setCurrentMapId(mapId) {
    currentMapId = mapId;
}

/**
 * Set hidden marker types (by image path)
 * @param {Array<string>} types - Array of image paths to hide
 * @param {object} mapInstance - Leaflet map instance (optional, for immediate refresh)
 */
export function setHiddenMarkerTypes(types, mapInstance) {
    hiddenMarkerTypes.clear();
    if (types && Array.isArray(types)) {
        types.forEach(t => hiddenMarkerTypes.add(t));
    }

    // If map instance provided, refresh marker visibility
    if (mapInstance) {
        refreshMarkerVisibility(mapInstance);
    }
}

/**
 * Get current hidden marker types
 * @returns {Array<string>} - Array of hidden image paths
 */
export function getHiddenMarkerTypes() {
    return Array.from(hiddenMarkerTypes);
}

/**
 * Refresh marker visibility based on current hidden types
 * Uses CSS display toggle for existing markers, adds markers that weren't loaded due to filter
 * @param {object} mapInstance - Leaflet map instance
 */
export function refreshMarkerVisibility(mapInstance) {
    // 1. Toggle visibility of existing markers via CSS (fast, reversible)
    Object.values(markers).forEach(mark => {
        const isHidden = hiddenMarkerTypes.has(mark.data.image);
        const el = mark.marker.getElement();
        if (el) {
            el.style.display = isHidden ? 'none' : '';
        }
    });

    // 2. Add markers from allMarkerData that are now visible but weren't loaded
    // (because they were hidden when initially added)
    Object.values(allMarkerData).forEach(markerData => {
        if (!markers[markerData.id] && !hiddenMarkerTypes.has(markerData.image)) {
            addMarker(markerData, mapInstance, true); // skipStorage=true
        }
    });
}

/**
 * Add a marker to the map
 * @param {object} markerData - Marker data with id, name, image, position, type, ready, minReady, maxReady, hidden
 * @param {object} mapInstance - Leaflet map instance
 * @param {boolean} skipStorage - If true, don't store in allMarkerData (used during rebuilds)
 * @returns {boolean} - True if marker was added
 */
export function addMarker(markerData, mapInstance, skipStorage = false) {
    // Only show markers on their own map
    if (markerData.map !== currentMapId) {
        return false;
    }

    // Store marker data for later re-evaluation (unless this is a rebuild)
    if (!skipStorage) {
        allMarkerData[markerData.id] = markerData;
    }

    if (markers[markerData.id]) {
        return false;
    }

    if (markerData.hidden) {
        return false;
    }

    // Skip markers whose type is hidden by user preference
    if (hiddenMarkerTypes.has(markerData.image)) {
        return false;
    }

    const iconUrl = `/${markerData.image}.png`;
    const isCustom = markerData.image === "gfx/terobjs/mm/custom";
    const isCave = markerData.name.toLowerCase() === "cave";
    const isThingwall = markerData.type === "thingwall";
    const isQuestGiver = markerData.type === "questgiver";
    const shouldHighlightThingwall = isThingwall && thingwallHighlightEnabled;
    const shouldHighlightQuestGiver = isQuestGiver && questGiverHighlightEnabled;
    const shouldHighlight = shouldHighlightThingwall || shouldHighlightQuestGiver;

    // Marker filter mode: hide all markers except highlighted ones
    if (markerFilterModeEnabled) {
        if (!shouldHighlight) {
            return false; // Skip non-highlighted markers when filter mode is active
        }
    }

    // Use larger icons for highlighted thingwalls (48px vs 36px default)
    let iconSize = shouldHighlight ? [48, 48] : (isCustom && !isCave ? [36, 36] : [36, 36]);
    let iconAnchor = shouldHighlight ? [24, 24] : (isCustom && !isCave ? [11, 21] : [18, 18]);

    let icon;
    if (markerData.timerText) {
        // Use divIcon for markers with timers to allow HTML overlay
        const actualIconUrl = isCave ? '/gfx/hud/mmap/cave.png' : iconUrl;
        icon = L.divIcon({
            html: `
                <div class="marker-with-timer">
                    <img src="${actualIconUrl}" class="marker-icon-img" style="width: ${iconSize[0]}px; height: ${iconSize[1]}px;" />
                    <div class="marker-timer-text">${markerData.timerText}</div>
                </div>
            `,
            iconSize: [iconSize[0], iconSize[1] + 12], // Add 12px for timer text
            iconAnchor: [iconAnchor[0], iconAnchor[1] + 12],
            popupAnchor: [0, -(iconAnchor[1] + 12)],
            className: 'marker-timer-container'
        });
    } else {
        // Use regular icon for markers without timers
        icon = L.icon({
            iconUrl: isCave ? '/gfx/hud/mmap/cave.png' : iconUrl,
            iconSize: iconSize,
            iconAnchor: iconAnchor
        });
    }

    const position = mapInstance.unproject([markerData.position.x, markerData.position.y], HnHMaxZoom);
    const marker = L.marker(position, { icon: icon, riseOnHover: true });

    const color = getMarkerColor(markerData.type);
    const extra = getMarkerReadyText(markerData);

    // Determine tooltip and highlight class based on marker type
    const tooltipClass = shouldHighlightThingwall ? 'thingwall-label' :
                         shouldHighlightQuestGiver ? 'questgiver-label' : '';
    const highlightClass = shouldHighlightThingwall ? 'thingwall-highlighted' :
                           shouldHighlightQuestGiver ? 'questgiver-highlighted' : '';

    marker.bindTooltip(`<div style='color:${color};'><b>${markerData.name} ${extra}</b></div>`, {
        permanent: shouldHighlight,
        direction: 'top',
        sticky: true,
        opacity: 0.9,
        className: tooltipClass
    });

    // Add highlight class to marker element if highlighting is enabled
    if (shouldHighlight && highlightClass) {
        marker.on('add', () => {
            const el = marker.getElement();
            if (el) {
                el.classList.add(highlightClass);
            }
        });
    }

    marker.on('click', () => {
        invokeDotNetSafe('JsOnMarkerClicked', markerData.id);
    });

    marker.on('contextmenu', (e) => {
        L.DomEvent.stopPropagation(e);
        // Pass screen coordinates for proper context menu positioning
        const screenX = Math.floor(e.containerPoint.x);
        const screenY = Math.floor(e.containerPoint.y);
        invokeDotNetSafe('JsOnMarkerContextMenu', markerData.id, screenX, screenY);
    });

    // Add to appropriate layer
    if (markerData.image === "gfx/hud/mmap/cave" || markerData.image === "gfx/terobjs/mm/burrow") {
        marker.addTo(detailedMarkerLayer);
    } else {
        marker.addTo(markerLayer);
    }

    markers[markerData.id] = {
        marker: marker,
        data: markerData
    };

    // Track thingwalls separately for Voronoi adjacency computation
    if (isThingwall) {
        thingwallMarkers[markerData.id] = {
            marker: marker,
            data: markerData
        };

        // Add hover events for jump connections visualization
        // Works independently of thingwall highlighting toggle
        marker.on('mouseover', () => {
            if (VoronoiAdjacency.isEnabled()) {
                const latlng = marker.getLatLng();
                VoronoiAdjacency.showConnections(
                    markerData.id,
                    latlng,
                    getThingwallLatLng,
                    getThingwallName,
                    highlightThingwallMarker
                );
            }
        });

        marker.on('mouseout', () => {
            if (VoronoiAdjacency.isEnabled()) {
                VoronoiAdjacency.hideConnections(unhighlightThingwallMarker);
            }
        });
    }

    return true;
}

/**
 * Add multiple markers to the map in a single batch (performance optimization)
 * @param {Array} markersData - Array of marker data objects
 * @param {object} mapInstance - Leaflet map instance
 * @returns {object} - Result with counts: { added, skipped }
 */
export function addMarkersBatch(markersData, mapInstance) {
    let added = 0;
    let skipped = 0;

    for (const markerData of markersData) {
        if (addMarker(markerData, mapInstance)) {
            added++;
        } else {
            skipped++;
        }
    }

    // Initialize Voronoi adjacency if we have enough thingwalls
    if (Object.keys(thingwallMarkers).length >= 3) {
        VoronoiAdjacency.initialize(mapInstance);
        VoronoiAdjacency.setEnabled(true);
        updateVoronoiAdjacency(mapInstance);
    }

    return { added, skipped };
}

/**
 * Update an existing marker
 * @param {number} markerId - Marker ID
 * @param {object} markerData - Updated marker data
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if marker was updated
 */
export function updateMarker(markerId, markerData, mapInstance) {
    const mark = markers[markerId];
    if (mark) {
        // Remove marker if it's no longer on the current map
        if (markerData.map !== currentMapId) {
            removeMarker(markerId, mapInstance);
            return false;
        }

        // Check if timer state has changed (added, removed, or text changed)
        const oldTimerText = mark.data.timerText || null;
        const newTimerText = markerData.timerText || null;

        if (oldTimerText !== newTimerText) {
            // Timer state changed - need to recreate marker with new icon
            // Remove old marker and add new one with updated timer border/text
            removeMarker(markerId, mapInstance);
            addMarker(markerData, mapInstance);
            return true;
        }

        // Update tooltip
        const extra = getMarkerReadyText(markerData);
        const color = getMarkerColor(markerData.type);
        mark.marker.setTooltipContent(`<div style='color:${color};'><b>${markerData.name} ${extra}</b></div>`);

        // Update position (in case coordinates changed)
        const position = mapInstance.unproject([markerData.position.x, markerData.position.y], HnHMaxZoom);
        mark.marker.setLatLng(position);

        mark.data = markerData;
        return true;
    }
    return false;
}

/**
 * Remove a marker from the map
 * @param {number} markerId - Marker ID to remove
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if marker was removed
 */
export function removeMarker(markerId, mapInstance) {
    const mark = markers[markerId];
    if (mark) {
        mapInstance.removeLayer(mark.marker);
        delete markers[markerId];
        return true;
    }
    return false;
}

/**
 * Clear all markers from the map
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Always true
 */
export function clearAllMarkers(mapInstance) {
    Object.keys(markers).forEach(id => removeMarker(parseInt(id), mapInstance));
    // Also clear stored marker data
    Object.keys(allMarkerData).forEach(id => delete allMarkerData[id]);
    // Clear thingwall tracking
    Object.keys(thingwallMarkers).forEach(id => delete thingwallMarkers[id]);
    // Cleanup Voronoi state
    VoronoiAdjacency.cleanup();
    return true;
}

/**
 * Rebuild all markers from stored data based on current visibility settings
 * Used when filter mode or highlight toggles change
 * @param {object} mapInstance - Leaflet map instance
 */
function rebuildAllMarkers(mapInstance) {
    // Remove all visible markers from the map
    Object.keys(markers).forEach(id => {
        const mark = markers[id];
        if (markerLayer && markerLayer.hasLayer(mark.marker)) {
            markerLayer.removeLayer(mark.marker);
        }
        if (detailedMarkerLayer && detailedMarkerLayer.hasLayer(mark.marker)) {
            detailedMarkerLayer.removeLayer(mark.marker);
        }
        mapInstance.removeLayer(mark.marker);
    });

    // Clear markers object
    Object.keys(markers).forEach(id => delete markers[id]);

    // Clear thingwall tracking (will be repopulated during addMarker)
    Object.keys(thingwallMarkers).forEach(id => delete thingwallMarkers[id]);

    // Re-add all markers from stored data (with skipStorage=true to avoid re-storing)
    Object.values(allMarkerData).forEach(markerData => {
        addMarker(markerData, mapInstance, true);
    });

    // Always update Voronoi adjacency after rebuild if there are thingwalls
    if (Object.keys(thingwallMarkers).length >= 3) {
        VoronoiAdjacency.initialize(mapInstance);
        VoronoiAdjacency.setEnabled(true);
        updateVoronoiAdjacency(mapInstance);
    }
}

/**
 * Toggle marker tooltips by type
 * @param {string} type - Marker type (quest, thingwall, etc.)
 * @param {boolean} visible - Whether to show tooltips
 * @returns {boolean} - Always true
 */
export function toggleMarkerTooltips(type, visible) {
    Object.values(markers).forEach(mark => {
        if (mark.data.type === type) {
            if (visible) {
                mark.marker.openTooltip();
            } else {
                mark.marker.closeTooltip();
            }
        }
    });
    return true;
}

/**
 * Jump map view to a marker
 * @param {number} markerId - Marker ID to jump to
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if marker was found
 */
export function jumpToMarker(markerId, mapInstance) {
    const mark = markers[markerId];
    if (mark) {
        const latlng = mark.marker.getLatLng();
        mapInstance.setView(latlng, Math.max(mapInstance.getZoom(), 5));
        return true;
    }
    return false;
}

/**
 * Update highlighting for thingwall and questgiver markers (fast CSS + tooltip toggle)
 * Avoids full marker rebuild - just toggles CSS classes and tooltip permanence
 */
function updateMarkerHighlighting() {
    Object.values(markers).forEach(mark => {
        const data = mark.data;
        const isThingwall = data.type === "thingwall";
        const isQuestGiver = data.type === "questgiver";
        const shouldHighlightThingwall = isThingwall && thingwallHighlightEnabled;
        const shouldHighlightQuestGiver = isQuestGiver && questGiverHighlightEnabled;
        const shouldHighlight = shouldHighlightThingwall || shouldHighlightQuestGiver;

        const el = mark.marker.getElement();
        if (el) {
            // Toggle CSS highlight classes (for glow effect and scale transform)
            el.classList.toggle('thingwall-highlighted', shouldHighlightThingwall);
            el.classList.toggle('questgiver-highlighted', shouldHighlightQuestGiver);
            // Use CSS scale transform for size change (48/36 = 1.33)
            el.style.transform = shouldHighlight ? 'scale(1.33)' : '';
            el.style.transformOrigin = 'center center';
        }

        // Toggle tooltip permanence and visibility
        const tooltip = mark.marker.getTooltip();
        if (tooltip) {
            tooltip.options.permanent = shouldHighlight;
            // Leaflet needs tooltip to be removed and re-added to change permanence
            mark.marker.unbindTooltip();
            const color = getMarkerColor(data.type);
            const tooltipClass = shouldHighlightThingwall ? 'thingwall-label' :
                                 shouldHighlightQuestGiver ? 'questgiver-label' : '';
            mark.marker.bindTooltip(`<div style='color:${color};'><b>${data.name}</b></div>`, {
                permanent: shouldHighlight,
                direction: 'top',
                sticky: true,
                opacity: 0.9,
                className: tooltipClass
            });
            if (shouldHighlight) {
                mark.marker.openTooltip();
            }
        }
    });
}

/**
 * Enable/disable thingwall highlighting with glow effect and permanent labels
 * @param {boolean} enabled - Whether highlighting is enabled
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Always true
 */
export function setThingwallHighlightEnabled(enabled, mapInstance) {
    if (!mapInstance) {
        console.warn('[MarkerManager] Cannot toggle thingwall highlight - mapInstance is null');
        return false;
    }

    if (thingwallHighlightEnabled === enabled) {
        return true; // No change needed
    }

    thingwallHighlightEnabled = enabled;

    // OPTIMIZED: Use CSS toggle instead of full rebuild (10-20x faster)
    updateMarkerHighlighting();

    console.log(`[MarkerManager] Thingwall highlighting ${enabled ? 'enabled' : 'disabled'}`);
    return true;
}

/**
 * Enable/disable quest giver highlighting with glow effect and permanent labels
 * @param {boolean} enabled - Whether highlighting is enabled
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Always true
 */
export function setQuestGiverHighlightEnabled(enabled, mapInstance) {
    if (!mapInstance) {
        console.warn('[MarkerManager] Cannot toggle quest giver highlight - mapInstance is null');
        return false;
    }

    if (questGiverHighlightEnabled === enabled) {
        return true; // No change needed
    }

    questGiverHighlightEnabled = enabled;

    // OPTIMIZED: Use CSS toggle instead of full rebuild (10-20x faster)
    updateMarkerHighlighting();

    console.log(`[MarkerManager] Quest giver highlighting ${enabled ? 'enabled' : 'disabled'}`);
    return true;
}

/**
 * Get whether quest giver highlighting is enabled
 * @returns {boolean} - True if quest giver highlighting is enabled
 */
export function isQuestGiverHighlightEnabled() {
    return questGiverHighlightEnabled;
}

/**
 * Enable/disable marker filter mode
 * When enabled, hides all markers except those with active highlights (thingwalls, quest givers)
 * @param {boolean} enabled - Whether filter mode is enabled
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Always true
 */
export function setMarkerFilterModeEnabled(enabled, mapInstance) {
    if (!mapInstance) {
        console.warn('[MarkerManager] Cannot toggle marker filter mode - mapInstance is null');
        return false;
    }

    if (markerFilterModeEnabled === enabled) {
        return true; // No change needed
    }

    markerFilterModeEnabled = enabled;

    // OPTIMIZED: Use CSS visibility toggle instead of full rebuild (10-20x faster)
    // This avoids 5000+ DOM removals/additions per toggle
    updateMarkerFilterVisibility();

    console.log(`[MarkerManager] Marker filter mode ${enabled ? 'enabled' : 'disabled'}`);
    return true;
}

/**
 * Update marker visibility based on filter mode (fast CSS toggle)
 * Called instead of full rebuild for filter mode changes
 */
function updateMarkerFilterVisibility() {
    Object.values(markers).forEach(mark => {
        const data = mark.data;
        const isThingwall = data.type === "thingwall";
        const isQuestGiver = data.type === "questgiver";
        const shouldHighlightThingwall = isThingwall && thingwallHighlightEnabled;
        const shouldHighlightQuestGiver = isQuestGiver && questGiverHighlightEnabled;
        const isHighlighted = shouldHighlightThingwall || shouldHighlightQuestGiver;

        // In filter mode, only highlighted markers are visible
        const shouldShow = !markerFilterModeEnabled || isHighlighted;

        // Use CSS display instead of DOM add/remove (much faster)
        const el = mark.marker.getElement();
        if (el) {
            el.style.display = shouldShow ? '' : 'none';
        }
    });
}

/**
 * Get whether marker filter mode is enabled
 * @returns {boolean} - True if marker filter mode is enabled
 */
export function isMarkerFilterModeEnabled() {
    return markerFilterModeEnabled;
}

// Helper functions

function getMarkerColor(type) {
    if (type === "questgiver") return "#2CDB2C"; // Green for quest givers
    if (type === "thingwall") return "#00cffd";
    return "#FFF";
}

function getMarkerReadyText(markerData) {
    if (markerData.ready) {
        return "READY";
    }
    if (markerData.maxReady !== -1 && markerData.minReady !== -1) {
        const now = Date.now();
        const minTime = msToTimeStr(markerData.minReady - now);
        const maxTime = msToTimeStr(markerData.maxReady - now);
        return `(Ready in [${minTime}] to [${maxTime}])`;
    }
    return "";
}

function msToTimeStr(duration) {
    if (duration < 0) duration = 0;

    const seconds = Math.floor((duration / 1000) % 60);
    const minutes = Math.floor((duration / (1000 * 60)) % 60);
    const hours = Math.floor((duration / (1000 * 60 * 60)) % 24);
    const days = Math.floor(duration / (1000 * 60 * 60 * 24));

    const d = days > 0 ? `${days}d` : "";
    const h = (days > 0 || hours > 0) ? `${hours}h ` : "";
    const m = (days <= 0 && (hours > 0 || minutes > 0)) ? `${minutes}m ` : "";
    const s = (days <= 0 && hours <= 0) ? `${seconds}s` : "";

    return d + h + m + s;
}

// ============ Voronoi/Jump Connection Helpers ============

/**
 * Get the LatLng of a thingwall marker by ID
 * @param {number} thingwallId - Thingwall marker ID
 * @returns {object|null} - Leaflet LatLng or null if not found
 */
function getThingwallLatLng(thingwallId) {
    const tw = thingwallMarkers[thingwallId];
    return tw ? tw.marker.getLatLng() : null;
}

/**
 * Get the name of a thingwall marker by ID
 * @param {number} thingwallId - Thingwall marker ID
 * @returns {string|null} - Thingwall name or null if not found
 */
function getThingwallName(thingwallId) {
    const tw = thingwallMarkers[thingwallId];
    return tw ? tw.data.name : null;
}

/**
 * Highlight a thingwall marker as a jump target
 * @param {number} thingwallId - Thingwall marker ID
 * @param {boolean} highlight - Whether to add or remove highlight
 * @param {boolean} isUncertain - Whether this is an uncertain/far connection
 */
function highlightThingwallMarker(thingwallId, highlight, isUncertain = false) {
    const tw = thingwallMarkers[thingwallId];
    if (!tw) return;

    const el = tw.marker.getElement();

    if (highlight) {
        // Add glow effect class
        if (el) {
            if (isUncertain) {
                el.classList.add('thingwall-jump-target-uncertain');
            } else {
                el.classList.add('thingwall-jump-target');
            }
        }

        // If thingwall highlighting is OFF, show the tooltip/name
        if (!thingwallHighlightEnabled) {
            tw.marker.openTooltip();
        }
    } else {
        // Remove all highlight classes
        if (el) {
            el.classList.remove('thingwall-jump-target');
            el.classList.remove('thingwall-jump-target-uncertain');
        }

        // If thingwall highlighting is OFF, hide the tooltip
        if (!thingwallHighlightEnabled) {
            tw.marker.closeTooltip();
        }
    }
}

/**
 * Remove jump target highlight from a thingwall marker
 * @param {number} thingwallId - Thingwall marker ID
 */
function unhighlightThingwallMarker(thingwallId) {
    highlightThingwallMarker(thingwallId, false);
}

/**
 * Update Voronoi adjacency with current thingwall positions
 * @param {object} mapInstance - Leaflet map instance
 */
function updateVoronoiAdjacency(mapInstance) {
    const thingwalls = Object.values(thingwallMarkers).map(tw => ({
        id: tw.data.id,
        position: tw.data.position
    }));
    VoronoiAdjacency.updateThingwalls(thingwalls);
}

/**
 * Enable/disable jump connections visualization on thingwall hover
 * @param {boolean} enabled - Whether to show jump connections
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Success status
 */
export function setJumpConnectionsEnabled(enabled, mapInstance) {
    if (!mapInstance) {
        console.warn('[MarkerManager] Cannot toggle jump connections - mapInstance is null');
        return false;
    }

    if (jumpConnectionsEnabled === enabled) {
        return true; // No change needed
    }

    jumpConnectionsEnabled = enabled;

    if (enabled) {
        // Initialize Voronoi module and compute adjacency
        VoronoiAdjacency.initialize(mapInstance);
        VoronoiAdjacency.setEnabled(true);
        updateVoronoiAdjacency(mapInstance);
    } else {
        VoronoiAdjacency.setEnabled(false);
    }

    console.log(`[MarkerManager] Jump connections ${enabled ? 'enabled' : 'disabled'}`);
    return true;
}

/**
 * Get whether jump connections are enabled
 * @returns {boolean} - True if jump connections are enabled
 */
export function isJumpConnectionsEnabled() {
    return jumpConnectionsEnabled;
}
