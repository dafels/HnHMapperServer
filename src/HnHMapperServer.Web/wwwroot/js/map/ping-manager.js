/**
 * Ping Manager Module
 * Manages temporary ping markers on the map with animations and auto-expiration
 */

import { BaseTileSize, HnHMaxZoom } from './leaflet-config.js';

// State variables
let mapInstance = null;
let activePings = {}; // pingId -> { marker, expiresAt, timeoutId, ping }
let latestPing = null; // Stores the most recent ping
const PING_ICON_SIZE = 48;
const PING_DURATION_MS = 60000; // 60 seconds

/**
 * Initialize the ping manager with a map instance
 * @param {L.Map} map - Leaflet map instance
 */
export function initialize(map) {
    mapInstance = map;
    console.log('[PingManager] Initialized');
}

/**
 * Create and add a ping marker to the map
 * @param {Object} ping - Ping data from API (id, mapId, coordX, coordY, x, y, createdBy, createdAt, expiresAt)
 * @param {boolean} playSound - Whether to play the ping sound
 */
export function addPing(ping, playSound = true) {
    console.log('[PingManager] addPing called with:', ping);

    if (!mapInstance) {
        console.warn('[PingManager] Map not initialized, cannot add ping');
        return;
    }

    // Server sends Pascal case (Id, CoordX, etc.), handle both cases
    const pingId = ping.Id || ping.id;
    const mapId = ping.MapId || ping.mapId;
    const coordX = ping.CoordX || ping.coordX;
    const coordY = ping.CoordY || ping.coordY;
    const x = ping.X || ping.x;
    const y = ping.Y || ping.y;
    const createdBy = ping.CreatedBy || ping.createdBy || 'Unknown';
    const expiresAt = ping.ExpiresAt || ping.expiresAt;

    console.log(`[PingManager] Parsed values - id:${pingId}, coords:(${coordX},${coordY}), local:(${x},${y})`);

    // Check if ping already exists
    if (activePings[pingId]) {
        console.log(`[PingManager] Ping ${pingId} already exists, skipping`);
        return;
    }

    // Calculate absolute position from grid coordinates (use BaseTileSize for 100x100 game grid)
    const absX = coordX * BaseTileSize + x;
    const absY = coordY * BaseTileSize + y;

    console.log(`[PingManager] Absolute coords: (${absX}, ${absY})`);

    // Convert to map coordinates
    const latLng = mapInstance.unproject([absX, absY], HnHMaxZoom);

    console.log(`[PingManager] LatLng:`, latLng);

    // Create pulsing ping icon using divIcon
    const pingIcon = L.divIcon({
        html: `
            <div class="ping-marker">
                <div class="ping-pulse"></div>
                <div class="ping-core"></div>
            </div>
        `,
        className: 'ping-icon-container',
        iconSize: [PING_ICON_SIZE, PING_ICON_SIZE],
        iconAnchor: [PING_ICON_SIZE / 2, PING_ICON_SIZE / 2]
    });

    // Create the marker
    const marker = L.marker(latLng, {
        icon: pingIcon,
        interactive: true,
        zIndexOffset: 10000 // Above other markers
    });

    console.log('[PingManager] Marker created:', marker);

    // Add tooltip with creator and remaining time
    marker.bindTooltip(`Ping by ${createdBy}`, {
        permanent: false,
        direction: 'top',
        offset: [0, -24]
    });

    // Add to map
    marker.addTo(mapInstance);
    console.log('[PingManager] Marker added to map');

    // Calculate time until expiration
    const expiresAtDate = new Date(expiresAt);
    const now = new Date();
    const timeUntilExpire = Math.max(0, expiresAtDate - now);

    console.log(`[PingManager] Expires at: ${expiresAtDate}, time until expire: ${timeUntilExpire}ms`);

    // Set timeout to auto-remove ping
    const timeoutId = setTimeout(() => {
        removePing(pingId, false); // Don't notify server, cleanup service will handle it
    }, timeUntilExpire);

    // Store ping data
    activePings[pingId] = {
        marker,
        expiresAt: expiresAtDate,
        timeoutId,
        ping: { Id: pingId, MapId: mapId, CoordX: coordX, CoordY: coordY, X: x, Y: y, CreatedBy: createdBy }
    };

    // Update latest ping reference
    latestPing = activePings[pingId];

    // Play sound if requested
    if (playSound) {
        console.log('[PingManager] Playing sound...');
        playPingSound();
    }

    console.log(`[PingManager] Added ping ${pingId} by ${createdBy} at (${coordX},${coordY},${x},${y}), expires in ${Math.round(timeUntilExpire / 1000)}s`);
}

/**
 * Remove a ping marker from the map
 * @param {number} pingId - ID of the ping to remove
 * @param {boolean} notifyServer - Whether to notify server of deletion (false for auto-expiration)
 */
export function removePing(pingId, notifyServer = false) {
    const pingData = activePings[pingId];
    if (!pingData) {
        return; // Ping not found or already removed
    }

    // Remove marker from map
    if (mapInstance && pingData.marker) {
        mapInstance.removeLayer(pingData.marker);
    }

    // Clear auto-expiration timeout
    if (pingData.timeoutId) {
        clearTimeout(pingData.timeoutId);
    }

    // Remove from active pings
    delete activePings[pingId];

    console.log(`[PingManager] Removed ping ${pingId}`);

    // Note: We don't notify the server because the cleanup service handles expiration
}

/**
 * Play the ping notification sound
 */
function playPingSound() {
    try {
        const audio = new Audio('/sounds/ping.wav');
        audio.volume = 0.5; // 50% volume
        // Cleanup after playback ends to prevent memory leak
        audio.onended = () => { audio.src = ''; };
        audio.play().catch(err => {
            // Browser may block autoplay, this is expected
            console.log('[PingManager] Audio playback blocked (user interaction may be required)');
            audio.src = ''; // Cleanup on error too
        });
    } catch (err) {
        console.warn('[PingManager] Error playing ping sound:', err);
    }
}

/**
 * Remove all pings from the map
 */
export function clearAllPings() {
    Object.keys(activePings).forEach(pingId => {
        removePing(parseInt(pingId), false);
    });
    console.log('[PingManager] Cleared all pings');
}

/**
 * Get count of active pings
 */
export function getActivePingCount() {
    return Object.keys(activePings).length;
}

/**
 * Get the latest ping that was created
 * @returns {Object|null} Latest ping data or null if no pings exist
 */
export function getLatestPing() {
    return latestPing;
}
