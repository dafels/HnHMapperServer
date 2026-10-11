namespace HnHMapperServer.Core.Cookbook;

/// <summary>
/// Food icon sources shared by the cookbook table and the notification bell's stat
/// preview: local game-resource PNGs under wwwroot (~2000 ship with the app) when the
/// <see cref="ShippedIcons"/> manifest says the icon ships (or cannot tell), with the
/// official game server's resource renderer as the onerror fallback; an icon known NOT to
/// ship goes straight to the renderer — a local 404 first cost a round trip on every
/// render (prerender, interactive render, every circuit resume). Requires
/// https://www.havenandhearth.com in the CSP img-src (deploy/Caddyfile).
///
/// Every builder canonicalizes the stored name through
/// <see cref="FoodResourceName.Normalize"/> first: rows ingested before ingestion
/// sanitized them can carry a scheme-like prefix ("f:gfx/invobjs/leaf-brassica"),
/// which resolves against neither source and leaves the icon permanently broken.
/// Normalize is also the injection guard for the onerror handlers below (the name is
/// interpolated into a JS string literal; it drops every character outside a path charset).
/// </summary>
public static class FoodIcons
{
    /// <summary>The game's resource renderer (allowed by the CSP img-src).</summary>
    public const string RemoteRendererBase = "https://www.havenandhearth.com/mt/r/";

    /// <summary>
    /// The img src: the local PNG when it ships (or the manifest is unknown), the remote renderer
    /// when it is known not to ship, and an empty string — no request at all, no "/.png" — when
    /// the stored name has nothing usable.
    /// </summary>
    public static string Src(ShippedIcons shipped, string? resourceName)
    {
        var name = FoodResourceName.Normalize(resourceName);
        if (name.Length == 0)
            return string.Empty;

        return shipped.MayContain(name) ? "/" + name + ".png" : RemoteRendererBase + name;
    }

    /// <summary>
    /// onerror for the cookbook table: retry once on the remote renderer. Null (Blazor omits the
    /// attribute) when <see cref="Src"/> already is the remote URL or there is nothing to load.
    /// </summary>
    public static string? RemoteFallback(ShippedIcons shipped, string? resourceName)
    {
        var name = FoodResourceName.Normalize(resourceName);
        return name.Length > 0 && shipped.MayContain(name)
            ? $"this.onerror=null;this.src='{RemoteRendererBase}{name}';"
            : null;
    }

    /// <summary>
    /// onerror chain for surfaces where a broken-image glyph is unacceptable (the
    /// notification bell): try the remote renderer once (unless the src already is it), then
    /// hide the img entirely.
    /// </summary>
    public static string RemoteFallbackOrHide(ShippedIcons shipped, string? resourceName)
    {
        var name = FoodResourceName.Normalize(resourceName);
        return name.Length > 0 && shipped.MayContain(name)
            ? $"if(!this.dataset.fb){{this.dataset.fb='1';this.src='{RemoteRendererBase}{name}';}}else{{this.style.display='none';}}"
            : "this.onerror=null;this.style.display='none';";
    }
}
