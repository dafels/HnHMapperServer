// Smart Tile Layer Module
// Custom Leaflet tile layer with caching, revision management, and smooth transitions

import { TileSize, BaseTileSize, HnHMinZoom, HnHMaxZoom } from './leaflet-config.js';

// Pre-computed scale factors for each zoom level (bit shift is 5x faster than Math.pow)
const SCALE_FACTORS = {};
for (let z = HnHMinZoom; z <= HnHMaxZoom; z++) {
    SCALE_FACTORS[z] = 1 << (HnHMaxZoom - z);  // Equivalent to Math.pow(2, HnHMaxZoom - z)
}

// Smart Tile Layer with caching and smooth transitions
export const SmartTileLayer = L.TileLayer.extend({
    cache: {},              // Per-map cache: { mapId: { tileKey: { etag, state } } }
    cacheKeys: [],          // Track insertion order for LRU eviction: [{ mapId, tileKey }]
    maxCacheEntries: 5000,  // Maximum TOTAL cache entries across all maps (increased for zoomed-out views)
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
        // Don't request tiles if mapId is invalid (0 or negative).
        //
        // IMPORTANT:
        // Returning an empty string here causes the <img> to fire "error" events in many browsers,
        // which can create a storm of expensive error handlers when Leaflet is trying to load tiles.
        // Instead we return Leaflet's built-in 1x1 transparent image URL so the tile "loads" instantly
        // with no network traffic and no error spam.
        if (!this.mapId || this.mapId <= 0) {
            return L.Util.emptyImageUrl;
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
                // Still within negative cache TTL - skip tile request.
                //
                // IMPORTANT:
                // Returning '' would still trigger an <img> error event and force Leaflet to do work.
                // Returning a transparent pixel avoids both network retries AND expensive error handling.
                return L.Util.emptyImageUrl;
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
        // CRITICAL PERFORMANCE + CORRECTNESS FIX:
        //
        // Leaflet's GridLayer expects the `done(err, tile)` callback to be invoked **exactly once**
        // per tile. The previous implementation called the base TileLayer.createTile with `done`
        // (so Leaflet invoked it), and ALSO invoked `done` again from extra DOM event listeners.
        //
        // That double-invocation amplifies Leaflet's internal bookkeeping work, and when many tiles
        // fail to load at once (common when switching tabs and the map refreshes), it results in the
        // DevTools warning spam you're seeing:
        //   "[Violation] 'error' handler took N ms"
        // and can freeze the tab for seconds.
        //
        // The correct approach is to pass a *wrapped* callback into the base implementation.
        // This lets us update `tilesLoading` and negative-cache missing tiles without calling `done` twice.
        const self = this;
        this.tilesLoading++;

        // Guard so that even if a browser misfires events, we never invoke the original done twice.
        let finished = false;

        function wrappedDone(err, tile) {
            if (finished) return;
            finished = true;

            // Always decrement loading counter (clamped).
            self.tilesLoading = Math.max(0, self.tilesLoading - 1);

            // On errors, hide the broken image and negative-cache the tile coordinates so we don't keep
            // retrying the same missing tile in tight loops.
            if (err) {
                try {
                    // Hide broken tile UI.
                    tile.src = L.Util.emptyImageUrl;
                    tile.style.visibility = 'hidden';

                    // Compute the SAME cacheKey format used in getTileUrl():
                    // `${mapId}:${x}:${y}:${hnhZoom}`
                    //
                    // This must account for:
                    // - zoomReverse / zoomOffset (HnH zoom differs from Leaflet zoom)
                    // - overlay offsets (offsetX/offsetY)
                    const leafletZoom = coords?.z ?? (self._map ? self._map.getZoom() : HnHMaxZoom);

                    // Convert Leaflet zoom -> HnH zoom (what the server URL uses).
                    let hnhZ = leafletZoom;
                    if (self.options.zoomReverse) {
                        hnhZ = self.options.maxZoom - leafletZoom;
                    }
                    hnhZ = hnhZ + (self.options.zoomOffset || 0);

                    // Apply grid offsets in the same way as getTileUrl() (rounded tile offsets).
                    const gridOffsetX = self.offsetX || 0;
                    const gridOffsetY = self.offsetY || 0;
                    const scaleAtLeafletZoom = SCALE_FACTORS[leafletZoom] || (1 << (HnHMaxZoom - leafletZoom));
                    const tileOffsetX = gridOffsetX / scaleAtLeafletZoom;
                    const tileOffsetY = gridOffsetY / scaleAtLeafletZoom;
                    const requestedX = coords.x + Math.round(tileOffsetX);
                    const requestedY = coords.y + Math.round(tileOffsetY);

                    const cacheKey = `${self.mapId}:${requestedX}:${requestedY}:${hnhZ}`;
                    self._addToNegativeCache(cacheKey);
                } catch {
                    // Never let error handling crash Leaflet's tile pipeline.
                }

                // IMPORTANT:
                // We deliberately report "success" to Leaflet (`done(null, tile)`) even though the tile failed.
                // This prevents Leaflet from retrying aggressively and keeps the map interactive.
                // The tile will render as transparent until it becomes available.
                if (typeof done === 'function') done(null, tile);
                return;
            }

            // Normal success path.
            if (typeof done === 'function') done(null, tile);
        }

        // Let Leaflet create the <img> tile element and hook up its internal load/error pipeline,
        // but route completion through our wrapper to keep counts/caches consistent.
        return L.TileLayer.prototype.createTile.call(this, coords, wrappedDone);
    },

    // Add entry to negative cache with LRU eviction
    _addToNegativeCache: function (cacheKey) {
        const now = Date.now();

        // If key already exists, just update expiry
        if (this.negativeCache[cacheKey]) {
            this.negativeCache[cacheKey] = now + this.negativeCacheTTL;
            return;
        }

        // Evict oldest entries if at max size.
        //
        // IMPORTANT:
        // Avoid Array.shift() here. shift() is O(n) and can become extremely expensive when a burst of
        // missing tiles inserts thousands of negative-cache entries (common on tab-switch reloads).
        if (typeof this._negativeCacheKeysHead !== 'number') {
            this._negativeCacheKeysHead = 0;
        }

        while ((this.negativeCacheKeys.length - this._negativeCacheKeysHead) >= this.negativeCacheMaxSize) {
            const oldestKey = this.negativeCacheKeys[this._negativeCacheKeysHead++];
            if (oldestKey) {
                delete this.negativeCache[oldestKey];
            }
        }

        // Periodically compact the key queue to keep memory bounded.
        if (this._negativeCacheKeysHead > 1000 && this._negativeCacheKeysHead > (this.negativeCacheKeys.length / 2)) {
            this.negativeCacheKeys = this.negativeCacheKeys.slice(this._negativeCacheKeysHead);
            this._negativeCacheKeysHead = 0;
        }

        // Also clean up expired entries periodically (every 100 insertions).
        // NOTE: Use the "active length" (minus head) so compaction doesn't break the cadence.
        const activeLen = this.negativeCacheKeys.length - (this._negativeCacheKeysHead || 0);
        if (activeLen > 0 && activeLen % 100 === 0) {
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

        // Respect head index if we've been evicting without shift().
        const start = typeof this._negativeCacheKeysHead === 'number' ? this._negativeCacheKeysHead : 0;
        for (let i = start; i < this.negativeCacheKeys.length; i++) {
            const key = this.negativeCacheKeys[i];
            if (this.negativeCache[key] && this.negativeCache[key] > now) {
                validKeys.push(key);
            } else {
                delete this.negativeCache[key];
            }
        }

        this.negativeCacheKeys = validKeys;
        this._negativeCacheKeysHead = 0;
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
