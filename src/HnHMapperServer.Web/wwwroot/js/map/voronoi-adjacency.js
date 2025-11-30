// Voronoi Adjacency Module
// Computes thingwall adjacency using Delaunay triangulation
// Two thingwalls are "adjacent" if they share a Delaunay edge (equivalent to sharing a Voronoi border)

import { HnHMaxZoom } from './leaflet-config.js';

// State
let delaunay = null;
let adjacencyMap = new Map(); // thingwallId -> Set<adjacentIds>
let thingwallPositions = []; // [{id, x, y}]
let positionIndexMap = new Map(); // thingwallId -> index in positions array
let connectionLinesLayer = null;
let highlightedMarkerIds = new Set();
let mapInstanceRef = null;
let isFeatureEnabled = false;

// Visual styles for each connection level (fading out progressively)
const LEVEL_STYLES = [
    { color: '#00cffd', weight: 2, opacity: 0.8, dashArray: '8, 4' },     // Level 1: Cyan
    { color: '#4169E1', weight: 1.5, opacity: 0.5, dashArray: '6, 4' },   // Level 2: Blue
    { color: '#888888', weight: 1, opacity: 0.3, dashArray: '4, 4' }      // Level 3: Gray
];

// Uncertain styles (even more faded) for each level
const UNCERTAIN_LEVEL_STYLES = [
    { color: '#ff9800', weight: 1.5, opacity: 0.4, dashArray: '4, 8' },   // Level 1: Orange
    { color: '#9370DB', weight: 1, opacity: 0.25, dashArray: '4, 6' },    // Level 2: Purple-ish
    { color: '#666666', weight: 0.5, opacity: 0.15, dashArray: '3, 5' }   // Level 3: Dark gray
];

// Threshold multiplier: if distance > median * this, mark as uncertain
const UNCERTAIN_DISTANCE_MULTIPLIER = 2.0;

/**
 * Initialize the voronoi adjacency module
 * @param {object} mapInstance - Leaflet map instance
 */
export function initialize(mapInstance) {
    if (mapInstanceRef === mapInstance && connectionLinesLayer) {
        return; // Already initialized for this map
    }

    mapInstanceRef = mapInstance;

    // Create layer group for connection lines
    if (connectionLinesLayer) {
        connectionLinesLayer.clearLayers();
        if (mapInstanceRef) {
            mapInstanceRef.removeLayer(connectionLinesLayer);
        }
    }
    connectionLinesLayer = L.layerGroup().addTo(mapInstance);

    console.log('[VoronoiAdjacency] Initialized');
}

/**
 * Enable/disable the jump connections feature
 * @param {boolean} enabled - Whether the feature is enabled
 */
export function setEnabled(enabled) {
    isFeatureEnabled = enabled;
    if (!enabled) {
        clearConnectionLines();
        clearHighlights();
    }
    console.log(`[VoronoiAdjacency] Feature ${enabled ? 'enabled' : 'disabled'}`);
}

/**
 * Check if feature is enabled
 * @returns {boolean}
 */
export function isEnabled() {
    return isFeatureEnabled;
}

/**
 * Update thingwall positions and recompute adjacency
 * Called when thingwall markers change
 * @param {Array} thingwalls - Array of {id, position: {x, y}}
 */
export function updateThingwalls(thingwalls) {
    if (!thingwalls || thingwalls.length < 3) {
        // Need at least 3 points for meaningful Delaunay
        adjacencyMap.clear();
        positionIndexMap.clear();
        thingwallPositions = [];
        delaunay = null;
        console.log('[VoronoiAdjacency] Not enough thingwalls for adjacency computation');
        return;
    }

    // Build positions array
    thingwallPositions = thingwalls.map((tw, index) => {
        positionIndexMap.set(tw.id, index);
        return {
            id: tw.id,
            x: tw.position.x,
            y: tw.position.y
        };
    });

    // Compute Delaunay triangulation using d3-delaunay
    // d3.Delaunay is available globally from the script tag
    const points = thingwallPositions.map(p => [p.x, p.y]);

    try {
        delaunay = d3.Delaunay.from(points);
    } catch (e) {
        console.error('[VoronoiAdjacency] Error computing Delaunay:', e);
        return;
    }

    // Build adjacency map from Delaunay edges
    adjacencyMap.clear();

    // Initialize empty sets for all thingwalls
    for (const tw of thingwallPositions) {
        adjacencyMap.set(tw.id, new Set());
    }

    // Extract edges from triangulation
    // Delaunay.triangles contains triangle vertex indices as [i0, j0, k0, i1, j1, k1, ...]
    const triangles = delaunay.triangles;
    for (let i = 0; i < triangles.length; i += 3) {
        const a = triangles[i];
        const b = triangles[i + 1];
        const c = triangles[i + 2];

        // Each triangle has 3 edges: a-b, b-c, c-a
        addEdge(a, b);
        addEdge(b, c);
        addEdge(c, a);
    }

    console.log(`[VoronoiAdjacency] Computed adjacency for ${thingwalls.length} thingwalls`);
}

/**
 * Add a bidirectional edge between two thingwalls in the adjacency map
 * @param {number} indexA - Index in thingwallPositions array
 * @param {number} indexB - Index in thingwallPositions array
 */
function addEdge(indexA, indexB) {
    const idA = thingwallPositions[indexA].id;
    const idB = thingwallPositions[indexB].id;
    adjacencyMap.get(idA).add(idB);
    adjacencyMap.get(idB).add(idA);
}

/**
 * Get adjacent thingwall IDs for a given thingwall
 * @param {number} thingwallId
 * @returns {Set<number>} Set of adjacent thingwall IDs
 */
export function getAdjacentThingwalls(thingwallId) {
    return adjacencyMap.get(thingwallId) || new Set();
}

/**
 * Get the position of a thingwall by its ID
 * @param {number} thingwallId - Thingwall ID
 * @returns {object|null} - {x, y} position or null if not found
 */
function getThingwallPosition(thingwallId) {
    const index = positionIndexMap.get(thingwallId);
    if (index === undefined) return null;
    return thingwallPositions[index];
}

/**
 * Calculate distance between two positions
 * @param {object} pos1 - {x, y}
 * @param {object} pos2 - {x, y}
 * @returns {number} - Euclidean distance
 */
function calculateDistance(pos1, pos2) {
    const dx = pos2.x - pos1.x;
    const dy = pos2.y - pos1.y;
    return Math.sqrt(dx * dx + dy * dy);
}

/**
 * Calculate median of an array of numbers
 * @param {number[]} values - Array of numbers
 * @returns {number} - Median value
 */
function calculateMedian(values) {
    if (values.length === 0) return 0;
    const sorted = [...values].sort((a, b) => a - b);
    const mid = Math.floor(sorted.length / 2);
    return sorted.length % 2 !== 0 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

/**
 * Calculate global median distance for uncertainty threshold
 * @param {Set} thingwallIds - Set of thingwall IDs to consider
 * @returns {number} - Median distance
 */
function calculateGlobalMedianDistance(thingwallIds) {
    const distances = [];
    thingwallIds.forEach(id => {
        const pos = getThingwallPosition(id);
        if (!pos) return;
        getAdjacentThingwalls(id).forEach(adjId => {
            const adjPos = getThingwallPosition(adjId);
            if (adjPos) {
                distances.push(calculateDistance(pos, adjPos));
            }
        });
    });
    return calculateMedian(distances);
}

/**
 * Draw a connection line between two thingwalls
 * @param {number} fromId - Source thingwall ID
 * @param {number} toId - Target thingwall ID
 * @param {number} level - Connection level (0, 1, or 2)
 * @param {function} getMarkerLatLng - Function to get LatLng for a marker ID
 * @param {number} uncertainThreshold - Distance threshold for uncertain connections
 */
function drawConnection(fromId, toId, level, getMarkerLatLng, uncertainThreshold) {
    const fromLatLng = getMarkerLatLng(fromId);
    const toLatLng = getMarkerLatLng(toId);
    if (!fromLatLng || !toLatLng) return;

    // Calculate distance to determine if uncertain
    const fromPos = getThingwallPosition(fromId);
    const toPos = getThingwallPosition(toId);
    const distance = (fromPos && toPos) ? calculateDistance(fromPos, toPos) : 0;
    const isUncertain = distance > uncertainThreshold;

    // Get style based on level and uncertainty
    const styleIndex = Math.min(level, LEVEL_STYLES.length - 1);
    const style = isUncertain ? UNCERTAIN_LEVEL_STYLES[styleIndex] : LEVEL_STYLES[styleIndex];

    // Draw the line
    const line = L.polyline([fromLatLng, toLatLng], style);
    connectionLinesLayer.addLayer(line);
}

/**
 * Show multi-level connection lines and highlights for a hovered thingwall
 * @param {number} thingwallId - The hovered thingwall ID
 * @param {object} sourceLatLng - Leaflet LatLng of hovered thingwall
 * @param {function} getMarkerLatLng - Function to get LatLng for a marker ID
 * @param {function} highlightMarker - Function to highlight a marker
 */
export function showConnections(thingwallId, sourceLatLng, getMarkerLatLng, highlightMarker) {
    if (!isFeatureEnabled) return;

    clearConnectionLines();
    clearHighlights();

    const level1 = getAdjacentThingwalls(thingwallId);
    if (!level1 || level1.size === 0) {
        console.log(`[VoronoiAdjacency] No adjacent thingwalls found for ID ${thingwallId}`);
        return;
    }

    // Track visited thingwalls to avoid duplicate lines
    const visited = new Set([thingwallId]);
    const drawnEdges = new Set(); // Track "fromId-toId" to avoid duplicate lines

    // Helper to create edge key (always smaller ID first for consistency)
    const edgeKey = (a, b) => a < b ? `${a}-${b}` : `${b}-${a}`;

    // Calculate global median for uncertainty threshold
    const allThingwalls = new Set([thingwallId, ...level1]);
    level1.forEach(l1Id => {
        getAdjacentThingwalls(l1Id).forEach(l2Id => allThingwalls.add(l2Id));
    });
    const medianDistance = calculateGlobalMedianDistance(allThingwalls);
    const uncertainThreshold = medianDistance * UNCERTAIN_DISTANCE_MULTIPLIER;

    // === Level 1: Direct connections from hovered thingwall ===
    level1.forEach(l1Id => {
        const key = edgeKey(thingwallId, l1Id);
        if (!drawnEdges.has(key)) {
            drawConnection(thingwallId, l1Id, 0, getMarkerLatLng, uncertainThreshold);
            drawnEdges.add(key);
        }
        visited.add(l1Id);

        // Highlight Level 1 markers (gold glow + tooltip)
        const fromPos = getThingwallPosition(thingwallId);
        const toPos = getThingwallPosition(l1Id);
        const distance = (fromPos && toPos) ? calculateDistance(fromPos, toPos) : 0;
        const isUncertain = distance > uncertainThreshold;
        highlightMarker(l1Id, true, isUncertain);
        highlightedMarkerIds.add(l1Id);
    });

    // === Level 2: Connections from Level 1 thingwalls ===
    const level2 = new Set();
    level1.forEach(l1Id => {
        getAdjacentThingwalls(l1Id).forEach(l2Id => {
            if (!visited.has(l2Id)) {
                const key = edgeKey(l1Id, l2Id);
                if (!drawnEdges.has(key)) {
                    drawConnection(l1Id, l2Id, 1, getMarkerLatLng, uncertainThreshold);
                    drawnEdges.add(key);
                }
                level2.add(l2Id);
            }
        });
    });
    level2.forEach(id => visited.add(id));

    // === Level 3: Connections from Level 2 thingwalls ===
    level2.forEach(l2Id => {
        getAdjacentThingwalls(l2Id).forEach(l3Id => {
            if (!visited.has(l3Id)) {
                const key = edgeKey(l2Id, l3Id);
                if (!drawnEdges.has(key)) {
                    drawConnection(l2Id, l3Id, 2, getMarkerLatLng, uncertainThreshold);
                    drawnEdges.add(key);
                }
                visited.add(l3Id);
            }
        });
    });

    console.log(`[VoronoiAdjacency] Showing connections for thingwall ${thingwallId}: L1=${level1.size}, L2=${level2.size}, edges=${drawnEdges.size}`);
}

/**
 * Hide all connection lines and remove highlights
 * @param {function} unhighlightMarker - Function to remove highlight from a marker
 */
export function hideConnections(unhighlightMarker) {
    clearConnectionLines();

    highlightedMarkerIds.forEach(id => {
        unhighlightMarker(id);
    });
    clearHighlights();
}

/**
 * Clear all connection lines from the map
 */
function clearConnectionLines() {
    if (connectionLinesLayer) {
        connectionLinesLayer.clearLayers();
    }
}

/**
 * Clear the set of highlighted marker IDs
 */
function clearHighlights() {
    highlightedMarkerIds.clear();
}

/**
 * Clean up when switching maps or destroying
 */
export function cleanup() {
    clearConnectionLines();
    clearHighlights();
    adjacencyMap.clear();
    positionIndexMap.clear();
    thingwallPositions = [];
    delaunay = null;
    console.log('[VoronoiAdjacency] Cleaned up');
}

/**
 * Get the number of thingwalls being tracked
 * @returns {number}
 */
export function getThingwallCount() {
    return thingwallPositions.length;
}

/**
 * Check if we have computed adjacency data
 * @returns {boolean}
 */
export function hasAdjacencyData() {
    return delaunay !== null && adjacencyMap.size > 0;
}
