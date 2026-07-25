// Glow Icon Module
// Bakes a coloured glow into a copy of a marker icon so highlighted markers can stay
// ordinary <img> icons.
//
// WHY THIS EXISTS
// Highlighting used to be `filter: drop-shadow(...) drop-shadow(...)` on the marker
// element. `filter` promotes every marker to its own compositing surface and re-runs both
// Gaussian blurs whenever that surface rasterizes - on every zoom, and on every add/remove
// the cluster group does. With ~130 thingwalls highlighted that dominates frame time and
// the map feels like treacle.
//
// Baking the glow into the bitmap once (per icon URL + colour) turns highlighted markers
// back into plain images the browser can batch like any other, at the cost of a single
// canvas render.

// key -> Promise<{url, size, pad}|null>
const cache = new Map();

/**
 * Get a glow-baked version of an icon.
 * @param {string} iconUrl - Source icon URL (must be same-origin, or the canvas taints)
 * @param {string} color - CSS colour of the glow
 * @param {number} size - Rendered icon size in CSS pixels (square)
 * @param {number} blur - Base blur radius; the halo pass uses double this
 * @returns {Promise<{url: string, size: number, pad: number}|null>} - null if baking failed
 */
export function getGlowIcon(iconUrl, color, size = 36, blur = 8) {
    const key = `${iconUrl}|${color}|${size}|${blur}`;
    let pending = cache.get(key);
    if (!pending) {
        pending = bake(iconUrl, color, size, blur);
        cache.set(key, pending);
    }
    return pending;
}

function bake(iconUrl, color, size, blur) {
    return new Promise(resolve => {
        const img = new Image();

        img.onload = () => {
            try {
                // Padding has to clear the widest blur, or the halo gets clipped
                const pad = blur * 2;
                const dim = size + pad * 2;
                const dpr = window.devicePixelRatio || 1;

                const canvas = document.createElement('canvas');
                canvas.width = dim * dpr;
                canvas.height = dim * dpr;

                const ctx = canvas.getContext('2d');
                ctx.scale(dpr, dpr);
                ctx.shadowColor = color;

                // Two shadow passes reproduce the old double drop-shadow: a tight bright
                // core and a wider halo.
                ctx.shadowBlur = blur;
                ctx.drawImage(img, pad, pad, size, size);
                ctx.shadowBlur = blur * 2;
                ctx.drawImage(img, pad, pad, size, size);

                // Final pass without a shadow so the icon itself stays crisp
                ctx.shadowBlur = 0;
                ctx.drawImage(img, pad, pad, size, size);

                resolve({ url: canvas.toDataURL('image/png'), size: dim, pad: pad });
            } catch (e) {
                console.warn('[GlowIcon] Failed to bake glow for', iconUrl, e);
                resolve(null);
            }
        };

        img.onerror = () => {
            console.warn('[GlowIcon] Failed to load icon', iconUrl);
            resolve(null);
        };

        img.src = iconUrl;
    });
}
