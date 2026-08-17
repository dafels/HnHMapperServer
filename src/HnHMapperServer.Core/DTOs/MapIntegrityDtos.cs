namespace HnHMapperServer.Core.DTOs;

/// <summary>
/// Cross-tenant map integrity report for superadmins: where two grid ids claim the same cell
/// (the fingerprint of coordinate-frame corruption) and any legacy placeholder grid rows.
/// </summary>
public class MapIntegrityReportDto
{
    public DateTime ScannedAt { get; set; }
    public int TenantsScanned { get; set; }
    public int TotalGrids { get; set; }

    /// <summary>One entry per (tenant, map) that has at least one contested cell.</summary>
    public List<MapIntegrityIssueDto> ContestedMaps { get; set; } = new();

    /// <summary>Legacy grid rows with the placeholder id "0" (guards prevent new ones).</summary>
    public List<PlaceholderGridRowDto> PlaceholderRows { get; set; } = new();

    public bool IsClean => ContestedMaps.Count == 0 && PlaceholderRows.Count == 0;
}

/// <summary>
/// One map with contested cells: how many, where (bounding box — feeds the wipe-region tool),
/// and a capped sample of the cells with the grid ids fighting over each.
/// </summary>
public class MapIntegrityIssueDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public string MapName { get; set; } = string.Empty;

    public int ContestedCellCount { get; set; }

    // Bounding box of ALL contested cells on this map (inclusive grid coords).
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }

    /// <summary>A capped sample of contested cells, each with the grid ids fighting over it.</summary>
    public List<ContestedCellDto> SampleCells { get; set; } = new();
}

public class ContestedCellDto
{
    public int X { get; set; }
    public int Y { get; set; }
    public List<string> GridIds { get; set; } = new();
}

public class PlaceholderGridRowDto
{
    public string TenantId { get; set; } = string.Empty;
    public string TenantName { get; set; } = string.Empty;
    public int MapId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}
