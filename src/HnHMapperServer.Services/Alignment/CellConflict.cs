namespace HnHMapperServer.Services.Alignment;

/// <summary>
/// Deterministic, order/priority-free policy for which source wins a unified base-grid cell when
/// tenant and hmap sources overlap after alignment:
/// <list type="number">
///   <item><b>Tenant beats hmap</b> — tenant maps are the live system of record; hmaps fill gaps.</item>
///   <item>then freshest (highest cache / modified time),</item>
///   <item>then smallest source key, then smallest grid id.</item>
/// </list>
/// Independent of source iteration order or any priority field.
/// </summary>
public static class CellConflict
{
    /// <summary>True if the candidate cell should replace the current winner.</summary>
    public static bool Wins(
        bool candIsTenant, long candFreshness, string candKey, string? candGrid,
        bool curIsTenant, long curFreshness, string curKey, string? curGrid)
    {
        if (candIsTenant != curIsTenant) return candIsTenant;
        if (candFreshness != curFreshness) return candFreshness > curFreshness;
        var k = string.CompareOrdinal(candKey, curKey);
        if (k != 0) return k < 0;
        return string.CompareOrdinal(candGrid ?? "", curGrid ?? "") < 0;
    }
}
