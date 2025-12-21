namespace HnHMapperServer.Core.Models;

/// <summary>
/// System configuration settings
/// </summary>
public class Config
{
    public string Title { get; set; } = "HnH Automapper Server";
    public string Prefix { get; set; } = "https://hnhmap.xyz";
    public bool DefaultHide { get; set; }
    public int? MainMapId { get; set; }
    public bool AllowGridUpdates { get; set; } = true;
    public bool AllowNewMaps { get; set; } = true;
}

/// <summary>
/// Page metadata for web UI
/// </summary>
public class Page
{
    public string Title { get; set; } = "HnH Automapper Server";
}
