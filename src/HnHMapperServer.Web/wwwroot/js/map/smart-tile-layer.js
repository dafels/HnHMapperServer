// Smart Tile Layer Module
// Custom Leaflet tile layer with caching, revision management, and smooth transitions

import { TileSize, BaseTileSize, HnHMinZoom, HnHMaxZoom } from './leaflet-config.js';

// Pre-computed scale factors for each zoom level (bit shift is 5x faster than Math.pow)
// Each zoom level covers 2x2 tiles of the next level, and 400x400 tiles cover 4x4 base 100x100 grid cells
const TILE_TO_GRID_RATIO = TileSize / BaseTileSize; // 400/100 = 4
const SCALE_FACTORS = {};
for (let z = HnHMinZoom; z <= HnHMaxZoom; z++) {
    SCALE_FACTORS[z] = (1 << (HnHMaxZoom - z)) * TILE_TO_GRID_RATIO;  // Accounts for 4x4 grids per tile
}

// Revision refreshes: debounce for back-to-back bumps, the cap on remembered failed tiles, and the cap on
// pending changed cells before a run falls back to refreshing everything (the server lists ≤ 256 per bump).
const REVISION_REFRESH_DEBOUNCE_MS = 500;
const REVISION_RETRY_MAX = 256;
const REVISION_CELLS_MAX = 256;
// Server (URL) zoom levels: 0 = the 400px zoom-0 cells .. 6 (Leaflet zoom is reversed onto these).
const MAX_SERVER_ZOOM = HnHMaxZoom - HnHMinZoom;

// Smart Tile Layer with caching and smooth transitions
export const SmartTileLayer = L.TileLayer.extend({
    cache: {},              // Per-map cache: { mapId: { tileKey: { etag, state } } }
    cacheKeys: [],          // Track insertion order for LRU eviction: [{ mapId, tileKey }]
    maxCacheEntries: 5000,  // Maximum TOTAL cache entries across all maps (increased for zoomed-out views)
    mapId: 0,
    offsetX: 0,             // X offset for overlay comparison (in grid coordinates, zoom-independent)
    offsetY: 0,             // Y offset for overlay comparison (in grid coordinates, zoom-independent)
    mapRevisions: {},       // Map ID → revision number for cache busting. Shared by every instance on purpose
                            // (server-global data, like `cache`); written only by leaflet-interop.js's
                            // setMapRevision/seedMapRevisions. Per-layer refresh state: addInitHook below.
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

        // Get tile offset at current Leaflet zoom level
        const [tileOffsetX, tileOffsetY] = this._getTileOffset();

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

    // Convert current grid offset to tile offset at current Leaflet zoom level
    _getTileOffset: function () {
        // Get grid offsets (in grid coordinates, constant across zoom)
        const gridOffsetX = this.offsetX || 0;
        const gridOffsetY = this.offsetY || 0;

        // IMPORTANT: coords.x/y are in Leaflet's zoom space, so we must use Leaflet zoom
        // for offset calculation, not HnH zoom (which is reversed via zoomReverse option)
        const leafletZoom = this._map ? this._map.getZoom() : HnHMaxZoom;

        // Scale factor: at Leaflet zoom z, one tile covers 2^(HnHMaxZoom - z) * 4 grids
        // (4x because 400x400 tiles cover 4x4 base 100x100 grid cells)
        // Using pre-computed SCALE_FACTORS lookup instead of Math.pow (5x faster)
        const scaleAtLeafletZoom = SCALE_FACTORS[leafletZoom] || ((1 << (HnHMaxZoom - leafletZoom)) * TILE_TO_GRID_RATIO);

        // Convert grid offset to tile offset at this Leaflet zoom level
        const tileOffsetX = gridOffsetX / scaleAtLeafletZoom;
        const tileOffsetY = gridOffsetY / scaleAtLeafletZoom;
        return [tileOffsetX, tileOffsetY];
    },

    // Override internal Leaflet method to apply grid offset when positioning tiles on the map
    _getTilePos: function (coords) {
        const basePos = L.TileLayer.prototype._getTilePos.call(this, coords);
        const [tileOffsetX, tileOffsetY] = this._getTileOffset();
        const pixelOffsetX = (Math.round(tileOffsetX) - tileOffsetX) * TileSize;
        const pixelOffsetY = (Math.round(tileOffsetY) - tileOffsetY) * TileSize;
        return basePos.add([pixelOffsetX, pixelOffsetY]);
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
        return this._refreshTile(key, { x, y, map: this.mapId, z }, null);
    },

    // Re-request the tile node `key` (Leaflet tile key) from server coords `data` ({ x, y, map, z }),
    // swapping the image only once the new one has loaded. `onFailed` (optional) runs if that load fails.
    _refreshTile: function (key, data, onFailed) {
        const tile = this._tiles[key];

        if (!tile) {
            return false; // Tile node doesn't exist yet; caller should redraw
        }

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
                    // Restore visibility for tiles that were hidden by error handler
                    if (tile.el.style.visibility === 'hidden') {
                        tile.el.style.visibility = '';
                    }
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
            if (onFailed) onFailed();
        };

        preloader.src = newUrl;
        return true; // Successfully initiated refresh
    },

    // Server tile coords (the URL's x/y/z) of the Leaflet tile node at (lx, ly, lz): the same math as
    // getTileUrl - HnH zoom via zoomReverse/zoomOffset, plus the overlay's grid offset - but scaled at the
    // tile's own zoom, since a level kept during a zoom animation is not at the map's current zoom.
    _serverCoords: function (lx, ly, lz) {
        const scale = SCALE_FACTORS[lz] || ((1 << (HnHMaxZoom - lz)) * TILE_TO_GRID_RATIO);
        let z = lz;
        if (this.options.zoomReverse) {
            z = this.options.maxZoom - z;
        }
        z = z + (this.options.zoomOffset || 0);
        return {
            x: lx + Math.round((this.offsetX || 0) / scale),
            y: ly + Math.round((this.offsetY || 0) / scale),
            map: this.mapId,
            z: z
        };
    },

    // The layer is still on a live map and still showing mapId.
    _isLiveFor: function (mapId) {
        return !!this._map && this.mapId === mapId && this._map.getContainer().isConnected;
    },

    // The tile node shows Leaflet's transparent placeholder instead of an image: its first load failed
    // (404, 503, network - createTile's error path swaps it in) or getTileUrl skipped it while it was
    // negative-cached. Short of being pruned and re-created, only a revision run requests it again.
    _isPlaceholder: function (key) {
        const tile = this._tiles && this._tiles[key];
        return !!tile && !!tile.el && tile.el.getAttribute('src') === L.Util.emptyImageUrl;
    },

    // Schedule a refresh after a revision bump of `mapId` (leaflet-interop.js's setMapRevision has already
    // stored the new revision, so tiles created meanwhile use it). `cells` ([[x, y], ...] server zoom-0
    // cells) limits it to the tiles covering those cells at every zoom; null refreshes every tile in view.
    // Debounced, because ZoomQ often bumps several times in a row: the pending cells accumulate, and one
    // full bump makes the whole run full. The state is per layer (see addInitHook below).
    scheduleRevisionRefresh: function (mapId, cells) {
        if (!this._isLiveFor(mapId)) return;

        const p = this._revisionRefresh;
        if (p.mapId !== mapId) {
            p.retry.clear();
            p.cells.clear();
            p.queue.clear();
            p.all = false;
        }
        p.mapId = mapId;

        if (!Array.isArray(cells)) {
            p.all = true;
        } else if (!p.all) {
            for (const cell of cells) {
                if (!Array.isArray(cell) || !Number.isInteger(cell[0]) || !Number.isInteger(cell[1])) {
                    p.all = true; // malformed: refresh everything rather than guess
                    break;
                }
                p.cells.add(`${cell[0]}:${cell[1]}`);
            }
            if (p.cells.size > REVISION_CELLS_MAX) {
                p.all = true;
            }
        }
        if (p.all) {
            p.cells.clear();
        }

        if (p.timer !== null) {
            clearTimeout(p.timer);
        }
        p.timer = setTimeout(() => {
            p.timer = null;
            this._runRevisionRefresh();
        }, REVISION_REFRESH_DEBOUNCE_MS);
    },

    // Server tile keys ("x:y:z") of the given zoom-0 cells and all their ancestors - the tiles a
    // regeneration of those cells rewrote (z0 = the cell, zN = floor(cell / 2^N); `>>` floors negatives).
    _affectedServerKeys: function (cellKeys) {
        const affected = new Set();
        for (const cellKey of cellKeys) {
            const parts = cellKey.split(':');
            const cx = parseInt(parts[0], 10);
            const cy = parseInt(parts[1], 10);
            for (let z = 0; z <= MAX_SERVER_ZOOM; z++) {
                affected.add(`${cx >> z}:${cy >> z}:${z}`);
            }
        }
        return affected;
    },

    _runRevisionRefresh: function () {
        const p = this._revisionRefresh;
        const mapId = p.mapId;

        // Liveness re-check: while the timer was pending the layer may have been removed (SPA exit,
        // initializeMap re-entry) or switched maps (changeMap redraws at the new revision anyway).
        if (!this._isLiveFor(mapId)) {
            p.retry.clear();
            p.cells.clear();
            p.queue.clear();
            p.all = false;
            return;
        }

        // A hidden tab paints nothing: every bump while hidden collapses into ONE run when the tab is
        // shown again (it used to queue one requestAnimationFrame burst per bump).
        if (document.hidden) {
            if (!p.waitingVisible) {
                p.waitingVisible = true;
                const onVisible = () => {
                    if (document.hidden) return;
                    document.removeEventListener('visibilitychange', onVisible);
                    p.waitingVisible = false;
                    this._runRevisionRefresh();
                };
                document.addEventListener('visibilitychange', onVisible);
            }
            return;
        }

        // Consume the pending work: everything, or only the tiles over the changed cells.
        const affected = p.all ? null : this._affectedServerKeys(p.cells);
        p.all = false;
        p.cells.clear();

        const mapPrefix = mapId + ':';
        const keys = new Set();
        if (affected === null) {
            // A full revision change means the server may have new tiles anywhere: every 404 remembered
            // for this map may be stale. clearVisibleNegativeCache() only clears the current
            // zoom/viewport, which misses entries.
            for (const key in this.negativeCache) {
                if (key.startsWith(mapPrefix)) {
                    delete this.negativeCache[key];
                }
            }
            // Every tile node in view (all retained levels).
            for (const key in this._tiles || {}) {
                keys.add(key);
            }
        } else {
            // Only the changed cells' tiles (also those out of view: a newly mapped area's cached 404
            // must not hide it when panned to), and only the nodes showing them, at every retained level.
            for (const serverKey of affected) {
                delete this.negativeCache[mapPrefix + serverKey];
            }
            for (const key in this._tiles || {}) {
                const parts = key.split(':');
                if (parts.length !== 3) continue;
                const data = this._serverCoords(parseInt(parts[0], 10), parseInt(parts[1], 10), parseInt(parts[2], 10));
                // Plus every placeholder node: full runs used to retry those on every bump, which is
                // what healed a transient failure (a 503, a network blip). Their negative-cache entries
                // are kept, so _refreshTile re-requests one only once its entry has expired - a lasting
                // 404 (unexplored area) costs one request per TTL, not one per bump.
                if (affected.has(`${data.x}:${data.y}:${data.z}`) || this._isPlaceholder(key)) {
                    keys.add(key);
                }
            }
        }

        // Plus the tiles whose refresh failed last time - they hold an old image whatever this bump
        // changed. Their failure put them in the negative cache, which would skip the retry.
        for (const key of p.retry) {
            keys.add(key);
            const parts = key.split(':');
            if (parts.length !== 3) continue;
            const data = this._serverCoords(parseInt(parts[0], 10), parseInt(parts[1], 10), parseInt(parts[2], 10));
            delete this.negativeCache[`${mapPrefix}${data.x}:${data.y}:${data.z}`];
        }
        p.retry.clear();

        // One shared work queue per layer: a run adds its keys and the single drain loop below works
        // them off. Runs no longer replace each other - a narrowed run only covers its own cells, so
        // superseding an earlier run would drop that run's remaining tiles. A key queued twice (before
        // its turn) is refreshed once, at the newest revision.
        for (const key of keys) {
            p.queue.add(key);
        }
        if (p.queue.size === 0 || p.draining) return;
        p.draining = true;

        const self = this;

        // Batch refresh tiles (4 per frame, reduced from 8 for smoother experience)
        // With 50ms minimum delay between batches for better visual smoothness
        const batchSize = 4;

        function refreshBatch() {
            const mapId = p.mapId;
            if (!self._isLiveFor(mapId)) {
                p.queue.clear();
                p.draining = false;
                return;
            }

            let done = 0;
            for (const key of p.queue) {
                if (done === batchSize) break;
                p.queue.delete(key);
                done++;

                // Pruned since (pan/zoom): a re-created node already requests the new revision.
                if (!self._tiles[key]) continue;

                const parts = key.split(':');
                if (parts.length !== 3) continue;
                const data = self._serverCoords(parseInt(parts[0], 10), parseInt(parts[1], 10), parseInt(parts[2], 10));
                self._refreshTile(key, data, () => {
                    // A placeholder needs no eager retry: every narrowed run picks it up again once
                    // its negative-cache entry expires. The retry set would clear that entry on each
                    // bump and re-request a lasting 404 every time.
                    if (self._isPlaceholder(key)) return;
                    if (p.mapId === mapId && p.retry.size < REVISION_RETRY_MAX) {
                        p.retry.add(key);
                    }
                });
            }

            // Schedule next batch if there are more tiles (with 50ms delay)
            if (p.queue.size > 0) {
                setTimeout(() => {
                    requestAnimationFrame(refreshBatch);
                }, 50);
            } else {
                p.draining = false;
            }
        }

        requestAnimationFrame(refreshBatch);
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
                    const [tileOffsetX, tileOffsetY] = self._getTileOffset();
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

// Per-instance revision-refresh state. Object literals passed to extend() land on the prototype and are
// shared by the main and overlay layers - the old prototype `revisionDebounce` was, so the overlay's
// setMapRevision call cleared the main layer's pending refresh timer and the refresh never ran.
SmartTileLayer.addInitHook(function () {
    this._revisionRefresh = {
        timer: null,            // pending debounce timeout
        mapId: 0,               // map the pending/last run is for
        all: false,             // pending: refresh every tile in view
        cells: new Set(),       // pending: changed server zoom-0 cells "x:y" (unused while `all`)
        retry: new Set(),       // Leaflet tile keys whose last revision refresh failed
        queue: new Set(),       // Leaflet tile keys waiting for the drain loop
        draining: false,        // the drain loop is scheduled
        waitingVisible: false   // a run is parked until the tab is visible again
    };
});
