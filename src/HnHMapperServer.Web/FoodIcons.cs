namespace HnHMapperServer.Web;

/// <summary>
/// Food icon sources shared by the cookbook table and the notification bell's stat
/// preview: local game-resource PNGs under wwwroot (~2000 ship with the app), with
/// the official game server's resource renderer as the onerror fallback.
/// </summary>
public static class FoodIcons
{
    public static string LocalSrc(string resourceName) => "/" + resourceName + ".png";

    public static string RemoteFallback(string resourceName) =>
        $"this.onerror=null;this.src='https://www.havenandhearth.com/mt/r/{resourceName}';";

    /// <summary>
    /// onerror chain for surfaces where a broken-image glyph is unacceptable (the
    /// notification bell): try the remote renderer once, then hide the img entirely.
    /// Requires https://www.havenandhearth.com in the CSP img-src (deploy/Caddyfile).
    /// </summary>
    public static string RemoteFallbackOrHide(string resourceName) =>
        $"if(!this.dataset.fb){{this.dataset.fb='1';this.src='https://www.havenandhearth.com/mt/r/{resourceName}';}}else{{this.style.display='none';}}";
}
