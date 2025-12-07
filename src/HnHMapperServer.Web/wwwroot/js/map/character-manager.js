// Character Manager Module
// Handles character marker management and animations

import { TileSize, HnHMaxZoom } from './leaflet-config.js';

// Character storage
const characters = {};
let currentMapId = 0;
let updateIntervalMs = 2000;
let showTooltips = true; // Track tooltip visibility state

/**
 * Set the current map ID for character filtering
 */
export function setCurrentMapId(mapId) {
    currentMapId = mapId;
}

/**
 * Set the update interval for character animations
 */
export function setUpdateInterval(intervalMs) {
    updateIntervalMs = intervalMs;
}

/**
 * Add a character marker to the map
 * @param {object} characterData - Character data with id, name, map, position: {x, y}, type, rotation, speed
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if character was added
 */
export function addCharacter(characterData, mapInstance) {
    if (characterData.map !== currentMapId) {
        return false;
    }

    const iconUrl = getCharacterIcon(characterData.type);
    const icon = L.icon({
        iconUrl: iconUrl,
        iconSize: [32, 32],
        iconAnchor: [16, 16]
    });

    const position = mapInstance.unproject([characterData.position.x, characterData.position.y], HnHMaxZoom);
    const marker = L.marker(position, {
        icon: icon,
        riseOnHover: true,
        rotationAngle: characterData.rotation,
        zIndexOffset: 5000  // Always render above other markers (pings use 10000)
    });

    const color = getCharacterColor(characterData.type);
    marker.bindTooltip(`<div style='color:${color};'><b>${characterData.name}</b></div>`, {
        permanent: true,
        direction: 'top',
        sticky: true,
        opacity: 1,
        offset: [-13, 0],
        className: 'character-tooltip'
    });

    marker.addTo(mapInstance);
    if (showTooltips) {
        marker.openTooltip();
    } else {
        marker.closeTooltip();
    }

    characters[characterData.id] = {
        marker: marker,
        data: characterData
    };

    // Animate movement
    const slideTo = getSlideToPosition(characterData.speed, characterData.rotation, characterData.position.x, characterData.position.y);
    const slidePosition = mapInstance.unproject([slideTo.x, slideTo.y], HnHMaxZoom);

    if (marker.slideTo) {
        marker.slideTo(slidePosition, { duration: updateIntervalMs });
    }

    return true;
}

/**
 * Update an existing character marker or add it if it doesn't exist
 * @param {number} characterId - Character ID
 * @param {object} characterData - Updated character data
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if character was updated
 */
export function updateCharacter(characterId, characterData, mapInstance) {
    const char = characters[characterId];

    if (!char) {
        return addCharacter(characterData, mapInstance);
    }

    if (characterData.map !== currentMapId) {
        removeCharacter(characterId, mapInstance);
        return false;
    }

    const marker = char.marker;
    const iconUrl = getCharacterIcon(characterData.type);
    const color = getCharacterColor(characterData.type);

    marker.setIcon(L.icon({
        iconUrl: iconUrl,
        iconSize: [32, 32],
        iconAnchor: [16, 16]
    }));

    marker.setTooltipContent(`<div style='color:${color};'><b>${characterData.name}</b></div>`);

    const position = mapInstance.unproject([characterData.position.x, characterData.position.y], HnHMaxZoom);
    marker.setLatLng(position);

    if (marker.setRotationAngle) {
        marker.setRotationAngle(characterData.rotation);
    }

    // Animate movement
    const slideTo = getSlideToPosition(characterData.speed, characterData.rotation, characterData.position.x, characterData.position.y);
    const slidePosition = mapInstance.unproject([slideTo.x, slideTo.y], HnHMaxZoom);

    if (marker.slideTo) {
        marker.slideTo(slidePosition, { duration: updateIntervalMs });
    }

    char.data = characterData;
    return true;
}

/**
 * Remove a character marker from the map
 * @param {number} characterId - Character ID to remove
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if character was removed
 */
export function removeCharacter(characterId, mapInstance) {
    const char = characters[characterId];
    if (char) {
        mapInstance.removeLayer(char.marker);
        delete characters[characterId];
        return true;
    }
    return false;
}

/**
 * Clear all characters from the map
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Always true
 */
export function clearAllCharacters(mapInstance) {
    Object.keys(characters).forEach(id => removeCharacter(parseInt(id), mapInstance));
    return true;
}

/**
 * Set characters from a full snapshot (replaces all characters for current map)
 * @param {Array} charactersData - Array of character objects with Id, Name, Map, Position: {X, Y}, Type, Rotation, Speed
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Always true
 */
export function setCharactersSnapshot(charactersData, mapInstance) {
    if (!Array.isArray(charactersData)) {
        console.error('[Characters] setCharactersSnapshot: expected array, got', typeof charactersData);
        return false;
    }

    console.log('[Characters] setCharactersSnapshot: received', charactersData.length, 'characters. Current mapId:', currentMapId);

    // Log first character for debugging
    if (charactersData.length > 0) {
        console.log('[Characters] First character sample:', JSON.stringify(charactersData[0]));
    }

    // Track which character IDs are in the snapshot
    const snapshotIds = new Set(charactersData.map(c => c.id));

    // Remove characters that are not in the snapshot
    const existingIds = Object.keys(characters).map(id => parseInt(id));
    for (const existingId of existingIds) {
        if (!snapshotIds.has(existingId)) {
            removeCharacter(existingId, mapInstance);
        }
    }

    // Update or add characters from snapshot
    let added = 0;
    let updated = 0;
    let skippedWrongMap = 0;
    for (const char of charactersData) {
        // Backend sends Character objects with nested position (camelCase from JSON serialization)
        // Format: {id, name, map, position: {x, y}, type, rotation, speed}

        if (char.map !== currentMapId) {
            skippedWrongMap++;
            continue;
        }

        // Use updateCharacter instead of addCharacter - it will add if missing, update if exists
        const existed = characters[char.id] !== undefined;
        if (updateCharacter(char.id, char, mapInstance)) {
            if (existed) {
                updated++;
            } else {
                added++;
            }
        }
    }

    return true;
}

/**
 * Apply character delta (incremental updates and deletions)
 * @param {Object} delta - Delta object with Updates and Deletions arrays
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - Always true
 */
export function applyCharacterDelta(delta, mapInstance) {
    if (!delta || typeof delta !== 'object') {
        console.error('[Characters] applyCharacterDelta: invalid delta', delta);
        return false;
    }

    let updated = 0;
    let deleted = 0;

    // Apply updates
    if (Array.isArray(delta.updates)) {
        for (const char of delta.updates) {
            // Backend sends Character objects with nested position (camelCase from JSON serialization)
            // Format matches what Blazor sends: {id, name, map, position: {x, y}, type, rotation, speed}
            // Use updateCharacter which handles both add and update
            if (updateCharacter(char.id, char, mapInstance)) {
                updated++;
            }
        }
    }

    // Apply deletions
    if (Array.isArray(delta.deletions)) {
        for (const characterId of delta.deletions) {
            if (removeCharacter(characterId, mapInstance)) {
                deleted++;
            }
        }
    }

    return true;
}

/**
 * Toggle character tooltips visibility
 * @param {boolean} visible - Whether to show tooltips
 * @returns {boolean} - Always true
 */
export function toggleCharacterTooltips(visible) {
    showTooltips = visible; // Track state for new characters
    Object.values(characters).forEach(char => {
        if (visible) {
            char.marker.openTooltip();
        } else {
            char.marker.closeTooltip();
        }
    });
    return true;
}

/**
 * Jump map view to a character
 * @param {number} characterId - Character ID to jump to
 * @param {object} mapInstance - Leaflet map instance
 * @returns {boolean} - True if character was found
 */
export function jumpToCharacter(characterId, mapInstance) {
    const char = characters[characterId];
    if (char) {
        const latlng = char.marker.getLatLng();
        mapInstance.setView(latlng, Math.max(mapInstance.getZoom(), 5));
        return true;
    }
    return false;
}

// Helper functions

function getCharacterIcon(type) {
    if (/player-[0-6]/.test(type)) {
        return `gfx/icons/player/${type}.png`;
    }
    return 'gfx/icons/player/player-0.png';
}

function getCharacterColor(type) {
    const colors = [
        "#FFFFFFFF", // 0
        "#2CDB2CFF", // 1
        "#E10000FF", // 2
        "#4D79FFFF", // 3
        "#52F2EAFF", // 4
        "#FFDC00FF", // 5
        "#8A4DFFFF", // 6
        "#FF7300FF"  // 7
    ];
    const match = type.match(/player-([0-7])/);
    if (match) {
        return colors[parseInt(match[1])];
    }
    return colors[0];
}

function getSlideToPosition(speed, rotation, x, y) {
    const radian = rotation * (Math.PI / 180);
    const speedFactor = (speed * updateIntervalMs / 1000) / 11;
    const newX = x + speedFactor * Math.sin(radian);
    const newY = y - speedFactor * Math.cos(radian);
    return { x: newX, y: newY };
}
