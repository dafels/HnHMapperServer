// Navigation Manager Module
// Handles road pathfinding and route calculation

import { BaseTileSize, HnHMaxZoom } from './leaflet-config.js';

// Navigation state
let currentRoute = null;
let routeMarkers = [];
let mapInstanceRef = null;

/**
 * Initialize navigation manager
 * @param {object} mapInstance - Leaflet map instance
 */
export function initializeNavigation(mapInstance) {
    mapInstanceRef = mapInstance;
}

/**
 * Set map instance reference
 * @param {object} mapInstance - Leaflet map instance
 */
export function setMapInstance(mapInstance) {
    mapInstanceRef = mapInstance;
}

/**
 * Convert waypoint to absolute pixel position
 * @param {object} wp - Waypoint with coordX, coordY, x, y
 * @returns {object} - {x, y} in absolute pixels
 */
function waypointToAbsolute(wp) {
    // Use BaseTileSize for 100x100 game grid coordinates
    return {
        x: wp.coordX * BaseTileSize + wp.x,
        y: wp.coordY * BaseTileSize + wp.y
    };
}

/**
 * Convert absolute pixel position to waypoint format
 * @param {number} absX - Absolute X coordinate
 * @param {number} absY - Absolute Y coordinate
 * @returns {object} - Waypoint format {coordX, coordY, x, y}
 */
function absoluteToWaypoint(absX, absY) {
    // Use BaseTileSize for 100x100 game grid coordinates
    return {
        coordX: Math.floor(absX / BaseTileSize),
        coordY: Math.floor(absY / BaseTileSize),
        x: Math.floor(((absX % BaseTileSize) + BaseTileSize) % BaseTileSize),
        y: Math.floor(((absY % BaseTileSize) + BaseTileSize) % BaseTileSize)
    };
}

/**
 * Calculate Euclidean distance between two points
 * @param {object} p1 - First point {x, y}
 * @param {object} p2 - Second point {x, y}
 * @returns {number} - Distance in pixels
 */
function calculateDistance(p1, p2) {
    const dx = p1.x - p2.x;
    const dy = p1.y - p2.y;
    return Math.sqrt(dx * dx + dy * dy);
}

/**
 * Get the start and end points of a road (first and last waypoints)
 * @param {object} road - Road object with waypoints
 * @returns {object} - {start: {x, y}, end: {x, y}}
 */
function getRoadEndpoints(road) {
    let waypoints = road.waypoints;

    // Handle string waypoints (JSON)
    if (typeof waypoints === 'string') {
        try {
            waypoints = JSON.parse(waypoints);
        } catch (e) {
            console.warn('[Navigation] Failed to parse waypoints for road', road.id, e);
            return null;
        }
    }

    if (!waypoints || !Array.isArray(waypoints) || waypoints.length < 2) {
        console.warn('[Navigation] Road', road.id, 'has invalid waypoints:', waypoints);
        return null;
    }

    const firstWp = waypoints[0];
    const lastWp = waypoints[waypoints.length - 1];

    return {
        start: waypointToAbsolute({
            coordX: firstWp.coordX ?? firstWp.CoordX ?? 0,
            coordY: firstWp.coordY ?? firstWp.CoordY ?? 0,
            x: firstWp.x ?? firstWp.X ?? 0,
            y: firstWp.y ?? firstWp.Y ?? 0
        }),
        end: waypointToAbsolute({
            coordX: lastWp.coordX ?? lastWp.CoordX ?? 0,
            coordY: lastWp.coordY ?? lastWp.CoordY ?? 0,
            x: lastWp.x ?? lastWp.X ?? 0,
            y: lastWp.y ?? lastWp.Y ?? 0
        })
    };
}

/**
 * Get the closest point on a road to a given point
 * @param {object} point - Point {x, y} in absolute pixels
 * @param {object} road - Road object with waypoints
 * @returns {object} - {point: {x, y}, distance: number, isStart: boolean}
 */
function getClosestEndpointOnRoad(point, road) {
    const endpoints = getRoadEndpoints(road);
    if (!endpoints) return null;

    const distToStart = calculateDistance(point, endpoints.start);
    const distToEnd = calculateDistance(point, endpoints.end);

    if (distToStart <= distToEnd) {
        return { point: endpoints.start, distance: distToStart, isStart: true };
    } else {
        return { point: endpoints.end, distance: distToEnd, isStart: false };
    }
}

/**
 * Calculate the total length of a road by summing waypoint segments
 * @param {object} road - Road object with waypoints
 * @returns {number} - Total length in pixels
 */
function getRoadLength(road) {
    const waypoints = typeof road.waypoints === 'string'
        ? JSON.parse(road.waypoints)
        : road.waypoints;

    if (!waypoints || waypoints.length < 2) return 0;

    let totalLength = 0;
    for (let i = 1; i < waypoints.length; i++) {
        const prev = waypointToAbsolute({
            coordX: waypoints[i-1].coordX ?? waypoints[i-1].CoordX ?? 0,
            coordY: waypoints[i-1].coordY ?? waypoints[i-1].CoordY ?? 0,
            x: waypoints[i-1].x ?? waypoints[i-1].X ?? 0,
            y: waypoints[i-1].y ?? waypoints[i-1].Y ?? 0
        });
        const curr = waypointToAbsolute({
            coordX: waypoints[i].coordX ?? waypoints[i].CoordX ?? 0,
            coordY: waypoints[i].coordY ?? waypoints[i].CoordY ?? 0,
            x: waypoints[i].x ?? waypoints[i].X ?? 0,
            y: waypoints[i].y ?? waypoints[i].Y ?? 0
        });
        totalLength += calculateDistance(prev, curr);
    }
    return totalLength;
}

/**
 * A* pathfinding to find optimal road sequence
 * @param {object} startPoint - Starting point {coordX, coordY, x, y}
 * @param {object} endPoint - Destination point {coordX, coordY, x, y}
 * @param {array} roads - Array of road objects
 * @returns {object} - {roads: [road objects in order], totalDistance: number, segments: [{road, entryPoint, exitPoint, jumpDistance}]}
 */
export function findRoute(startPoint, endPoint, roads) {
    console.log('[Navigation] findRoute: Processing', roads?.length || 0, 'roads');
    console.log('[Navigation] Start point:', startPoint);
    console.log('[Navigation] End point:', endPoint);

    if (!roads || roads.length === 0) {
        console.warn('[Navigation] No roads provided to findRoute');
        return { roads: [], totalDistance: 0, segments: [], error: 'No roads available' };
    }

    const start = waypointToAbsolute(startPoint);
    const end = waypointToAbsolute(endPoint);
    console.log('[Navigation] Absolute start:', start, 'end:', end);

    // Special case: if start and end are very close, no route needed
    const directDistance = calculateDistance(start, end);
    if (directDistance < 50) {
        return { roads: [], totalDistance: directDistance, segments: [], message: 'Destination is very close' };
    }

    // Build graph nodes: each road has two endpoint nodes
    // Node ID format: "road_{roadId}_{start|end}"
    const nodes = new Map();
    const roadMap = new Map();

    let roadsWithEndpoints = 0;
    roads.forEach(road => {
        const id = road.id ?? road.Id;
        const endpoints = getRoadEndpoints(road);
        if (!endpoints) {
            console.warn('[Navigation] Road', id, 'has no valid endpoints, waypoints:', road.waypoints);
            return;
        }

        roadsWithEndpoints++;
        roadMap.set(id, road);
        nodes.set(`road_${id}_start`, {
            type: 'road_endpoint',
            roadId: id,
            isStart: true,
            point: endpoints.start,
            road: road
        });
        nodes.set(`road_${id}_end`, {
            type: 'road_endpoint',
            roadId: id,
            isStart: false,
            point: endpoints.end,
            road: road
        });
    });

    console.log('[Navigation] Built graph with', roadsWithEndpoints, 'roads having valid endpoints');
    console.log('[Navigation] Road IDs in roadMap:', Array.from(roadMap.keys()));

    // Log road endpoints for debugging
    roadMap.forEach((road, id) => {
        const endpoints = getRoadEndpoints(road);
        if (endpoints) {
            console.log(`[Navigation] Road ${id} "${road.name || road.Name}": start=(${endpoints.start.x}, ${endpoints.start.y}), end=(${endpoints.end.x}, ${endpoints.end.y})`);
        }
    });

    if (roadsWithEndpoints === 0) {
        console.error('[Navigation] No roads have valid endpoints!');
        return { roads: [], totalDistance: 0, segments: [], error: 'No roads have valid waypoint data' };
    }

    // Add virtual start and end nodes
    nodes.set('start', { type: 'start', point: start });
    nodes.set('end', { type: 'end', point: end });

    // A* implementation
    const openSet = new Set(['start']);
    const cameFrom = new Map();
    const gScore = new Map();
    const fScore = new Map();

    // Initialize scores
    nodes.forEach((_, nodeId) => {
        gScore.set(nodeId, Infinity);
        fScore.set(nodeId, Infinity);
    });
    gScore.set('start', 0);
    fScore.set('start', calculateDistance(start, end));

    while (openSet.size > 0) {
        // Find node with lowest fScore in openSet
        let current = null;
        let lowestF = Infinity;
        for (const nodeId of openSet) {
            const f = fScore.get(nodeId);
            if (f < lowestF) {
                lowestF = f;
                current = nodeId;
            }
        }

        if (current === 'end') {
            // Reconstruct path
            console.log('[Navigation] A* found path to end, reconstructing...');
            console.log('[Navigation] cameFrom map size:', cameFrom.size);
            // Log the full cameFrom chain
            let traceNode = 'end';
            const trace = [];
            while (traceNode) {
                trace.unshift(traceNode);
                traceNode = cameFrom.get(traceNode);
            }
            console.log('[Navigation] Full path trace:', trace);
            return reconstructPath(cameFrom, current, nodes, roadMap, start, end);
        }

        openSet.delete(current);
        const currentNode = nodes.get(current);

        // Get neighbors
        const neighbors = getNeighbors(current, currentNode, nodes, roadMap);

        for (const { neighborId, cost } of neighbors) {
            const tentativeG = gScore.get(current) + cost;

            if (tentativeG < gScore.get(neighborId)) {
                cameFrom.set(neighborId, current);
                gScore.set(neighborId, tentativeG);

                const neighborNode = nodes.get(neighborId);
                const heuristic = calculateDistance(neighborNode.point, end);
                fScore.set(neighborId, tentativeG + heuristic);

                openSet.add(neighborId);
            }
        }
    }

    // No path found - return closest road to destination
    return findClosestRoadToDestination(start, end, roads);
}

// Jump penalty settings - makes direct jumps MUCH more expensive than road travel
// This strongly encourages the algorithm to use roads rather than jumping
const JUMP_PENALTY_MULTIPLIER = 10.0;  // 10x penalty for jump distance
const JUMP_BASE_PENALTY = 500;          // Fixed penalty for any jump (discourages many small jumps too)

/**
 * Calculate penalized jump cost
 */
function getJumpCost(distance) {
    return JUMP_BASE_PENALTY + (distance * JUMP_PENALTY_MULTIPLIER);
}

/**
 * Get neighbors for A* algorithm
 */
function getNeighbors(currentId, currentNode, nodes, roadMap) {
    const neighbors = [];

    if (currentNode.type === 'start') {
        // From start, can jump to any road endpoint (penalized)
        nodes.forEach((node, nodeId) => {
            if (node.type === 'road_endpoint') {
                const distance = calculateDistance(currentNode.point, node.point);
                const cost = getJumpCost(distance);
                neighbors.push({ neighborId: nodeId, cost });
            }
        });
    } else if (currentNode.type === 'road_endpoint') {
        // From a road endpoint, can:
        // 1. Travel along the road to the other endpoint (no penalty - roads are fast!)
        // 2. Jump to any other road's endpoint (heavily penalized)
        // 3. Jump to destination (heavily penalized)

        const road = currentNode.road;
        const roadLength = getRoadLength(road);
        const otherEndId = `road_${currentNode.roadId}_${currentNode.isStart ? 'end' : 'start'}`;

        // Travel along road to other endpoint - NO PENALTY (roads are the preferred path)
        neighbors.push({ neighborId: otherEndId, cost: roadLength });

        // Jump to other roads (heavily penalized)
        nodes.forEach((node, nodeId) => {
            if (node.type === 'road_endpoint' && node.roadId !== currentNode.roadId) {
                const distance = calculateDistance(currentNode.point, node.point);
                const cost = getJumpCost(distance);
                neighbors.push({ neighborId: nodeId, cost });
            }
        });

        // Jump to destination (heavily penalized)
        const destNode = nodes.get('end');
        const distance = calculateDistance(currentNode.point, destNode.point);
        const costToEnd = getJumpCost(distance);
        neighbors.push({ neighborId: 'end', cost: costToEnd });
    }

    return neighbors;
}

/**
 * Reconstruct path from A* result
 */
function reconstructPath(cameFrom, current, nodes, roadMap, start, end) {
    const path = [current];
    while (cameFrom.has(current)) {
        current = cameFrom.get(current);
        path.unshift(current);
    }

    console.log('[Navigation] Reconstructing path, nodes in path:', path);

    // Extract roads from path
    const segments = [];
    const visitedRoads = new Set();
    let totalDistance = 0;
    let lastPoint = start;

    for (let i = 0; i < path.length; i++) {
        const nodeId = path[i];
        const node = nodes.get(nodeId);

        console.log('[Navigation] Processing node:', nodeId, 'type:', node?.type, 'roadId:', node?.roadId);

        if (node.type === 'road_endpoint' && !visitedRoads.has(node.roadId)) {
            visitedRoads.add(node.roadId);
            const road = roadMap.get(node.roadId);
            const endpoints = getRoadEndpoints(road);

            // Calculate jump distance to this road
            const jumpDistance = calculateDistance(lastPoint, node.point);

            // Determine entry and exit points
            const entryPoint = node.point;
            const exitPoint = node.isStart ? endpoints.end : endpoints.start;
            const roadLength = getRoadLength(road);

            console.log('[Navigation] Adding road segment:', node.roadId, road.name ?? road.Name);

            segments.push({
                road: road,
                roadId: node.roadId,
                roadName: road.name ?? road.Name ?? `Road ${node.roadId}`,
                entryPoint: entryPoint,
                exitPoint: exitPoint,
                jumpDistance: Math.round(jumpDistance),
                roadLength: Math.round(roadLength)
            });

            totalDistance += jumpDistance + roadLength;
            lastPoint = exitPoint;
        } else if (node.type === 'end') {
            // Add final jump to destination
            const finalJump = calculateDistance(lastPoint, end);
            totalDistance += finalJump;
            if (segments.length > 0) {
                segments[segments.length - 1].finalJumpDistance = Math.round(finalJump);
            }
        }
    }

    console.log('[Navigation] Final segments:', segments.length, 'roads');
    segments.forEach((seg, i) => {
        console.log(`[Navigation] Segment ${i + 1}: roadId=${seg.roadId}, name=${seg.roadName}`);
    });

    return {
        roads: segments.map(s => s.road),
        totalDistance: Math.round(totalDistance),
        segments: segments,
        success: true
    };
}

/**
 * Fallback: find the single closest road to destination
 */
function findClosestRoadToDestination(start, end, roads) {
    let closestRoad = null;
    let closestDistance = Infinity;
    let closestEndpoint = null;

    roads.forEach(road => {
        const closest = getClosestEndpointOnRoad(end, road);
        if (closest && closest.distance < closestDistance) {
            closestDistance = closest.distance;
            closestRoad = road;
            closestEndpoint = closest;
        }
    });

    if (closestRoad) {
        const jumpToRoad = calculateDistance(start, closestEndpoint.point);
        return {
            roads: [closestRoad],
            totalDistance: Math.round(jumpToRoad + closestDistance),
            segments: [{
                road: closestRoad,
                roadId: closestRoad.id ?? closestRoad.Id,
                roadName: closestRoad.name ?? closestRoad.Name ?? 'Unknown Road',
                jumpDistance: Math.round(jumpToRoad),
                roadLength: Math.round(getRoadLength(closestRoad))
            }],
            success: true,
            partial: true
        };
    }

    return { roads: [], totalDistance: 0, segments: [], error: 'No suitable route found' };
}

/**
 * Get current route
 * @returns {object|null} - Current route or null
 */
export function getCurrentRoute() {
    return currentRoute;
}

/**
 * Set current route
 * @param {object} route - Route object from findRoute
 */
export function setCurrentRoute(route) {
    currentRoute = route;
}

/**
 * Clear current route
 */
export function clearCurrentRoute() {
    currentRoute = null;
}

/**
 * Convert coordinate to LatLng for map display
 * @param {object} point - Point in absolute pixels {x, y}
 * @param {object} mapInstance - Leaflet map instance
 * @returns {object} - Leaflet LatLng
 */
export function pointToLatLng(point, mapInstance) {
    return mapInstance.unproject([point.x, point.y], HnHMaxZoom);
}

/**
 * Convert waypoint to LatLng
 * @param {object} wp - Waypoint {coordX, coordY, x, y}
 * @param {object} mapInstance - Leaflet map instance
 * @returns {object} - Leaflet LatLng
 */
export function waypointToLatLng(wp, mapInstance) {
    const abs = waypointToAbsolute(wp);
    return mapInstance.unproject([abs.x, abs.y], HnHMaxZoom);
}
