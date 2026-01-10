// Leaflet Configuration Module
// Constants and Custom Coordinate Reference System for Haven & Hearth

// Map constants - using 400x400 tiles for better performance (16x fewer HTTP requests)
// Each 400x400 tile contains 4x4 = 16 original 100x100 grid cells
export const TileSize = 400;
export const BaseTileSize = 100;  // Original grid cell size (for coordinate conversion)
export const HnHMaxZoom = 7;
export const HnHMinZoom = 1;

// Coordinate normalization factors (based on original 100px grid cells, not display tile size)
const latNormalization = 90.0 * BaseTileSize / 2500000.0;
const lngNormalization = 180.0 * BaseTileSize / 2500000.0;

// Custom HnH Projection
const HnHProjection = {
    project: function (latlng) {
        return L.point(latlng.lat / latNormalization, latlng.lng / lngNormalization);
    },
    unproject: function (point) {
        return L.latLng(point.x * latNormalization, point.y * lngNormalization);
    },
    bounds: L.bounds(
        [-latNormalization, -lngNormalization],
        [latNormalization, lngNormalization]
    )
};

// Custom HnH Coordinate Reference System
export const HnHCRS = L.extend({}, L.CRS.Simple, {
    projection: HnHProjection
});
