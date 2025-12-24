// Smart Tile Layer Module
// Custom Leaflet tile layer with caching, revision management, and smooth transitions

import { TileSize, HnHMinZoom, HnHMaxZoom } from './leaflet-config.js';

// Pre-computed scale factors for each zoom level (bit shift is 5x faster than Math.pow)
const SCALE_FACTORS = {};
for (let z = HnHMinZoom; z <= HnHMaxZoom; z++) {
    SCALE_FACTORS[z] = 1 << (HnHMaxZoom - z);  // Equivalent to Math.pow(2, HnHMaxZoom - z)
}

// Smart Tile Layer with caching and smooth transitions
export const SmartTileLayer = L.TileLayer.extend({
    cache: {},              // Per-map cache: { mapId: { tileKey: { etag, state } } }
    cacheKeys: [],          // Track insertion order for LRU eviction: [{ mapId, tileKey }]
    maxCacheEntries: 1000,  // Maximum TOTAL cache entries across all maps (prevents GB memory usage)
    mapId: 0,
    offsetX: 0,             // X offset for overlay comparison (in grid coordinates, zoom-independent)
    offsetY: 0,             // Y offset for overlay comparison (in grid coordinates, zoom-independent)
    mapRevisions: {},       // Map ID → revision number for cache busting
    revisionDebounce: {},   // Map ID → timeout handle for debouncing revision updates
    negativeCache: {},      // Temporary negative cache: cacheKey → expiry timestamp (Date.now() + TTL)
    negativeCacheTTL: 30000, // 30 seconds TTL for negative cache (reduced from 2 minutes for faster retry)
    negativeCacheKeys: [],  // Track insertion order for LRU eviction
    negativeCacheMaxSize: 10000, // Maximum entries to prevent memory bloat
    tileStates: {},         // Track tile loading state: tileKey → 'new'|'loaded'|'refreshing'
    tilesLoading: 0,        // Count of currently loading tiles
    tileQueue: [],          // Progressive loading queue: sorted by distance from center
    processingQueue: false, // Whether we're currently processing the queue

    getTileUrl: function (coords) {
        // Don't request tiles if mapId is invalid (0 or negative)
        if (!this.mapId || this.mapId <= 0) {
            return '';  // Return empty string to skip tile request
        }

        // Get grid offsets (in grid coordinates, constant across zoom)
        const gridOffsetX = this.offsetX || 0;
        const gridOffsetY = this.offsetY || 0;

        // IMPORTANT: coords.x/y are in Leaflet's zoom space, so we must use Leaflet zoom
        // for offset calculation, not HnH zoom (which is reversed via zoomReverse option)
        const leafletZoom = this._map ? this._map.getZoom() : HnHMaxZoom;
        // Scale factor: at Leaflet zoom z, one tile covers 2^(HnHMaxZoom - z) grids
        // Using pre-computed SCALE_FACTORS lookup instead of Math.pow (5x faster)
        const scaleAtLeafletZoom = SCALE_FACTORS[leafletZoom] || (1 << (HnHMaxZoom - leafletZoom));
        // Convert grid offset to tile offset at this Leaflet zoom level
        const tileOffsetX = gridOffsetX / scaleAtLeafletZoom;
        const tileOffsetY = gridOffsetY / scaleAtLeafletZoom;

        // Get HnH zoom for the URL z parameter (server needs this for tile path)
        const hnhZoom = this._getZoomForUrl();

        // Apply tile offset (coords are in Leaflet space, offset now also in Leaflet space)
        const data = {
            x: coords.x + Math.round(tileOffsetX),
            y: coords.y + Math.round(tileOffsetY),
            map: this.mapId,
            z: hnhZoom  // Server needs HnH zoom for tile path
        };

        const cacheKey = `${data.map}:${data.x}:${data.y}:${data.z}`;
        const tileKey = `${data.x}:${data.y}:${data.z}`;

        // Initialize per-map cache if needed
        if (!this.cache[data.map]) {
            this.cache[data.map] = {};
        }

        // Get cache entry for this tile
        const cacheEntry = this.cache[data.map][tileKey];
        data.cache = cacheEntry?.etag || '';

        // Check if tile is in negative cache (temporary blacklist)
        const negativeExpiry = this.negativeCache[cacheKey];
        if (negativeExpiry) {
            const now = Date.now();
            if (now < negativeExpiry) {
                // Still within negative cache TTL - skip tile request
                return '';
            } else {
                // Negative cache expired - clear it and retry with nonce
                delete this.negativeCache[cacheKey];
                // Add retry nonce to bypass any browser/CDN 404 cache
                data.cache = `r=${now}`;
            }
        }

        // Track tile state (new tiles start in 'new' state)
        if (!this.tileStates[cacheKey]) {
            this.tileStates[cacheKey] = 'new';
        }

        // If cache exists and is valid, use it; otherwise use retry nonce (if set above) or empty
        if (!data.cache) {
            data.cache = '';
        }

        // Add revision parameter for cache busting (if we have a revision for this map)
        const revision = this.mapRevisions[this.mapId] || 1;
        data.v = revision;

        return L.Util.template(this._url, L.Util.extend(data, this.options));
    },

    refresh: function (x, y, z) {
        let zoom = z;
        const maxZoom = this.options.maxZoom;
        const zoomReverse = this.options.zoomReverse;
        const zoomOffset = this.options.zoomOffset;

        if (zoomReverse) {
            zoom = maxZoom - zoom;
        }
        zoom = zoom + zoomOffset;

        const key = `${x}:${y}:${zoom}`;
        const tile = this._tiles[key];

        if (!tile) {
            return false; // Tile node doesn't exist yet; caller should redraw
        }

        const data = { x, y, map: this.mapId, z };
        const cacheKey = `${data.map}:${data.x}:${data.y}:${data.z}`;
        const tileKey = `${data.x}:${data.y}:${data.z}`;

        // Initialize per-map cache if needed
        if (!this.cache[data.map]) {
            this.cache[data.map] = {};
        }

        // Get cache entry
        const cacheEntry = this.cache[data.map][tileKey];
        data.cache = cacheEntry?.etag || '';

        // Check negative cache with TTL
        const negativeExpiry = this.negativeCache[cacheKey];
        if (negativeExpiry) {
            const now = Date.now();
            if (now < negativeExpiry) {
                // Still negative - keep current tile visible instead of blanking it
                // Don't change anything, just return
                return true;
            } else {
                // Expired - clear and retry with nonce
                delete this.negativeCache[cacheKey];
                data.cache = `r=${now}`;
            }
        }

        // If cache doesn't exist, use retry nonce (if set) or empty string
        if (!data.cache) {
            data.cache = '';
        }

        // Add revision parameter for cache busting
        const revision = this.mapRevisions[this.mapId] || 1;
        data.v = revision;

        // Preload the new tile URL and only swap on successful load
        // This prevents blackouts - the old tile stays visible until the new one is ready
        const newUrl = L.Util.template(this._url, L.Util.extend(data, this.options));

        // Normalize URLs to absolute form for reliable comparison (avoid unnecessary swaps)
        const currentSrc = tile.el.src ? new URL(tile.el.src, location.origin).href : '';
        const targetSrc = new URL(newUrl, location.origin).href;

        // If the URL hasn't changed, no need to reload
        if (currentSrc === targetSrc) {
            return true;
        }

        // Mark tile as refreshing (important for crossfade logic)
        const tileState = this.tileStates[cacheKey] || 'new';
        this.tileStates[cacheKey] = 'refreshing';

        // Preload off-DOM to avoid flicker
        const preloader = new Image();
        const self = this;

        // Cleanup function to release Image memory after use
        const cleanupPreloader = () => {
            preloader.onload = preloader.onerror = null;
            preloader.src = '';
        };

        preloader.onload = () => {
            // Swap tile immediately without CSS transitions (better performance)
            const swapTile = () => {
                if (tile.el && self._tiles[key]) {
                    tile.el.src = newUrl;
                    self.tileStates[cacheKey] = 'loaded';
                }
                cleanupPreloader();
            };

            // Decode image if supported, then swap
            if (preloader.decode) {
                preloader.decode().then(swapTile).catch(swapTile);
            } else {
                swapTile();
            }
        };

        preloader.onerror = () => {
            // Mark as temporarily unavailable but keep old tile visible
            self._addToNegativeCache(cacheKey);
            // Reset state
            self.tileStates[cacheKey] = tileState;
            cleanupPreloader();
        };

        preloader.src = newUrl;
        return true; // Successfully initiated refresh
    },

    // Update revision for a map and force refresh all tiles for that map (debounced)
    setMapRevision: function (mapId, revision) {
        const oldRevision = this.mapRevisions[mapId];

        // Clear existing debounce timeout for this map
        if (this.revisionDebounce[mapId]) {
            clearTimeout(this.revisionDebounce[mapId]);
        }

        // Store the new revision immediately (for new tile requests)
        this.mapRevisions[mapId] = revision;

        // If this is the currently displayed map and revision changed, debounce the refresh
        if (this.mapId === mapId && oldRevision !== revision) {
            if (!this._map) return;

            const self = this;

            // Debounce tile refresh by 500ms to batch multiple revision updates
            this.revisionDebounce[mapId] = setTimeout(() => {
                delete self.revisionDebounce[mapId];

                // Gather all visible tiles that need refreshing
                const keys = Object.keys(self._tiles || {});
                const tilesToRefresh = [];

                for (let i = 0; i < keys.length; i++) {
                    const key = keys[i];
                    const parts = key.split(":");
                    if (parts.length !== 3) continue;
                    const x = parseInt(parts[0], 10);
                    const y = parseInt(parts[1], 10);
                    const processedZoom = parseInt(parts[2], 10);

                    // Invert zoom transform used to build tile key so refresh() finds the node
                    let z = processedZoom - (self.options.zoomOffset || 0);
                    if (self.options.zoomReverse) {
                        z = self.options.maxZoom - z;
                    }

                    tilesToRefresh.push({ x, y, z });
                }

                // Batch refresh tiles (4 per frame, reduced from 8 for smoother experience)
                // With 50ms minimum delay between batches for better visual smoothness
                const batchSize = 4;
                let currentIndex = 0;

                function refreshBatch() {
                    const endIndex = Math.min(currentIndex + batchSize, tilesToRefresh.length);
                    for (let i = currentIndex; i < endIndex; i++) {
                        const tile = tilesToRefresh[i];
                        self.refresh(tile.x, tile.y, tile.z);
                    }
                    currentIndex = endIndex;

                    // Schedule next batch if there are more tiles (with 50ms delay)
                    if (currentIndex < tilesToRefresh.length) {
                        setTimeout(() => {
                            requestAnimationFrame(refreshBatch);
                        }, 50);
                    }
                }

                // Start batched refresh
                if (tilesToRefresh.length > 0) {
                    requestAnimationFrame(refreshBatch);
                }
            }, 500); // 500ms debounce
        }
    },

    // Clear negative cache entries for visible tiles in current viewport
    // Called after zoom/move to allow retry of tiles that may have been temporarily unavailable
    clearVisibleNegativeCache: function () {
        if (!this._map || !this.mapId || this.mapId <= 0) {
            return;
        }

        const bounds = this._map.getBounds();
        const zoom = this._map.getZoom();
        const tileRange = this._pxBoundsToTileRange(this._map.getPixelBounds());

        // Iterate through visible tile coordinates
        for (let x = tileRange.min.x; x <= tileRange.max.x; x++) {
            for (let y = tileRange.min.y; y <= tileRange.max.y; y++) {
                // Convert to HnH coordinates (account for zoomReverse)
                let hnhZ = zoom;
                if (this.options.zoomReverse) {
                    hnhZ = this.options.maxZoom - zoom;
                }

                const cacheKey = `${this.mapId}:${x}:${y}:${hnhZ}`;
                if (this.negativeCache[cacheKey]) {
                    delete this.negativeCache[cacheKey];
                }
            }
        }
    },

    // Override createTile to track loading state
    createTile: function (coords, done) {
        const tile = L.TileLayer.prototype.createTile.call(this, coords, done);

        // Track loading state
        this.tilesLoading++;

        // Update loading overlay if it exists
        this._updateLoadingOverlay();

        // Wrap the done callback to track when tile finishes loading
        const originalDone = done;
        const self = this;

        tile.addEventListener('load', function() {
            self.tilesLoading = Math.max(0, self.tilesLoading - 1);
            self._updateLoadingOverlay();
            if (originalDone) originalDone(null, tile);
        });

        tile.addEventListener('error', function() {
            self.tilesLoading = Math.max(0, self.tilesLoading - 1);
            self._updateLoadingOverlay();
            // Hide the broken image by clearing src and making invisible
            tile.src = '';
            tile.style.visibility = 'hidden';
            if (originalDone) originalDone(null, tile);
        });

        return tile;
    },

    // Add entry to negative cache with LRU eviction
    _addToNegativeCache: function (cacheKey) {
        const now = Date.now();

        // If key already exists, just update expiry
        if (this.negativeCache[cacheKey]) {
            this.negativeCache[cacheKey] = now + this.negativeCacheTTL;
            return;
        }

        // Evict oldest entries if at max size
        while (this.negativeCacheKeys.length >= this.negativeCacheMaxSize) {
            const oldestKey = this.negativeCacheKeys.shift();
            delete this.negativeCache[oldestKey];
        }

        // Also clean up expired entries periodically (every 100 insertions)
        if (this.negativeCacheKeys.length > 0 && this.negativeCacheKeys.length % 100 === 0) {
            this._cleanExpiredNegativeCache();
        }

        // Add new entry
        this.negativeCache[cacheKey] = now + this.negativeCacheTTL;
        this.negativeCacheKeys.push(cacheKey);
    },

    // Remove expired entries from negative cache
    _cleanExpiredNegativeCache: function () {
        const now = Date.now();
        const validKeys = [];

        for (let i = 0; i < this.negativeCacheKeys.length; i++) {
            const key = this.negativeCacheKeys[i];
            if (this.negativeCache[key] && this.negativeCache[key] > now) {
                validKeys.push(key);
            } else {
                delete this.negativeCache[key];
            }
        }

        this.negativeCacheKeys = validKeys;
    },

    // Update loading overlay based on tiles loading
    _updateLoadingOverlay: function () {
        if (!this._map) return;

        const container = this._map.getContainer();
        if (!container) return;

        let overlay = container.querySelector('.tile-loading-overlay');

        // Calculate loading percentage
        const totalTiles = Object.keys(this._tiles || {}).length;
        const loadingPercentage = totalTiles > 0 ? (this.tilesLoading / totalTiles) : 0;

        // Show overlay if more than 50% of tiles are loading
        if (loadingPercentage > 0.5 && this.tilesLoading > 5) {
            if (!overlay) {
                overlay = document.createElement('div');
                overlay.className = 'tile-loading-overlay';
                container.appendChild(overlay);
            }
            overlay.style.opacity = '0.05'; // Very subtle
        } else {
            if (overlay) {
                overlay.style.opacity = '0';
                setTimeout(() => {
                    if (overlay && overlay.parentNode) {
                        overlay.parentNode.removeChild(overlay);
                    }
                }, 300);
            }
        }
    }
});
