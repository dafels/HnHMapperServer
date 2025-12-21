namespace HnHMapperServer.Web.Models;

public class Position
{
    public int X { get; set; }
    public int Y { get; set; }
}

public class CharacterModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Map { get; set; }
    public Position Position { get; set; } = new();
    public int Rotation { get; set; }
    public int Speed { get; set; }
    public string Type { get; set; } = "player";
}

public class MarkerModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Map { get; set; }
    public Position Position { get; set; } = new();
    public string Image { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public long MinReady { get; set; } = -1;
    public long MaxReady { get; set; } = -1;
    public bool Ready { get; set; }
    public string? TimerText { get; set; } // Timer countdown text for display on map

    public string Type
    {
        get
        {
            if (Image == "gfx/invobjs/small/bush" ||
                Image == "gfx/invobjs/small/bumling" ||
                Image == "gfx/terobjs/mm/gianttoad")
                return "questgiver";

            if (Image == "gfx/terobjs/mm/thingwall")
                return "thingwall";

            if (Image == "custom")
                return "custom";

            var lastSlash = Image.LastIndexOf('/');
            return lastSlash == -1 ? Image : Image.Substring(lastSlash + 1);
        }
    }
}

public class MapInfoModel
{
    public int ID { get; set; }
    public MapMetadata MapInfo { get; set; } = new();
    public int Size { get; set; }

    // For MudBlazor autocomplete
    public string Text => MapInfo.Name;
    public int Value => ID;
}

public class MapMetadata
{
    public string Name { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public int Priority { get; set; }
    public int Revision { get; set; } = 1; // Default revision for cache busting
    public bool IsMainMap { get; set; }
    public int? DefaultStartX { get; set; }
    public int? DefaultStartY { get; set; }
}

public class TileUpdate
{
    public int M { get; set; } // Map ID
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; } // Zoom
    public long T { get; set; } // Timestamp
}

public class MapMerge
{
    public int From { get; set; }
    public int To { get; set; }
    public Position Shift { get; set; } = new();
}

public class MapConfig
{
    public string Title { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
    public int? MainMapId { get; set; }
    public bool AllowGridUpdates { get; set; } = true;
    public bool AllowNewMaps { get; set; } = true;
}

public class CharacterDeltaModel
{
    public List<CharacterModel> Updates { get; set; } = new();
    public List<int> Deletions { get; set; } = new();
}

/// <summary>
/// Event model for SSE game marker events
/// </summary>
public class MarkerEventModel
{
    public int Id { get; set; }
    public int MapId { get; set; }
    public string GridId { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Image { get; set; } = string.Empty;
    public bool Hidden { get; set; }
    public long MaxReady { get; set; } = -1;
    public long MinReady { get; set; } = -1;
    public bool Ready { get; set; }
}
