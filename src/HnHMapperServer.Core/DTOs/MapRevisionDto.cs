using System.Text.Json.Serialization;

namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// DTO for map revision updates sent via SSE.
/// Clients use this to know when to invalidate cached tiles.
/// One instance is shared by every subscriber channel: never mutate it after Notify.
/// </summary>
public class MapRevisionDto
{
    /// <summary>
    /// Map ID that was updated
    /// </summary>
    public int MapId { get; set; }

    /// <summary>
    /// New revision number (incremented on any tile-affecting operation)
    /// </summary>
    public int Revision { get; set; }

    /// <summary>
    /// The 400px WebP zoom-0 cells <c>[x, y]</c> (floor(base grid / 4)) this bump regenerated, set
    /// by ZoomQ only. Null = unknown or the whole map, so clients refresh everything; omitted from
    /// the JSON then, which keeps every other bump's payload as it was. Clients honour the list only
    /// when this revision follows the one they hold by exactly 1 — otherwise they missed a bump.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int[][]? Cells { get; set; }
}
