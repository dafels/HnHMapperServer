using System.IO.Compression;
using System.Text;

namespace HnHMapperServer.Services.Services;

/// <summary>
/// Reads and parses .hmap files exported from Haven &amp; Hearth game client
/// </summary>
public class HmapReader
{
    private const string SIGNATURE = "Haven Mapfile 1";

    public HmapData Read(Stream stream)
    {
        var data = new HmapData();

        // Read signature (15 bytes - "Haven Mapfile 1")
        var sigBytes = new byte[15];
        stream.ReadExactly(sigBytes);
        var signature = Encoding.ASCII.GetString(sigBytes);

        if (signature != SIGNATURE)
        {
            throw new InvalidDataException($"Invalid signature: '{signature}'. Expected: '{SIGNATURE}'");
        }

        data.Signature = signature;

        // Rest is Z-compressed (zlib/deflate)
        // Skip the 2-byte zlib header (78 DA = best compression)
        var zlibHeader = new byte[2];
        stream.ReadExactly(zlibHeader);

        using var deflateStream = new DeflateStream(stream, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        deflateStream.CopyTo(ms);
        ms.Position = 0;

        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        // Read records
        while (ms.Position < ms.Length)
        {
            var recordType = ReadNullTerminatedString(reader);
            if (string.IsNullOrEmpty(recordType))
                break;

            var recordLength = reader.ReadInt32();
            var recordData = reader.ReadBytes(recordLength);

            if (recordType == "grid")
            {
                var grid = ParseGrid(recordData);
                data.Grids.Add(grid);
            }
            else if (recordType == "mark")
            {
                var marker = ParseMarker(recordData, out var issue);
                if (marker != null)
                {
                    data.Markers.Add(marker);
                }
                else
                {
                    data.MarkersUnreadable++;
                    if (issue != null)
                        data.MarkerParseIssues.Add(issue);
                }
            }
            else
            {
                data.UnknownRecords.Add(recordType);
            }
        }

        return data;
    }

    private HmapGridData ParseGrid(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var grid = new HmapGridData();

        grid.Version = reader.ReadByte();
        grid.GridId = reader.ReadInt64();
        grid.SegmentId = reader.ReadInt64();
        grid.ModifiedTime = reader.ReadInt64();
        grid.TileX = reader.ReadInt32();
        grid.TileY = reader.ReadInt32();

        if (grid.Version >= 4)
        {
            grid.GridSize = reader.ReadInt32();

            // Read tilesets
            var tilesetCount = reader.ReadUInt16();
            for (int i = 0; i < tilesetCount; i++)
            {
                var tileset = new HmapTilesetInfo
                {
                    ResourceName = ReadNullTerminatedString(reader),
                    ResourceVersion = reader.ReadUInt16(),
                    Priority = reader.ReadByte()
                };
                grid.Tilesets.Add(tileset);
            }

            // Read tile indices
            var tileCount = grid.GridSize;
            grid.TileIndices = new int[tileCount];

            if (tilesetCount <= 256)
            {
                for (int i = 0; i < tileCount; i++)
                    grid.TileIndices[i] = reader.ReadByte();
            }
            else
            {
                for (int i = 0; i < tileCount; i++)
                    grid.TileIndices[i] = reader.ReadUInt16();
            }

            // Read z-map (height data) for cliff/ridge rendering
            if (ms.Position < ms.Length)
            {
                var zFormat = reader.ReadByte();
                grid.ZMap = new float[tileCount];

                switch (zFormat)
                {
                    case 0: // Uniform height - single value for entire grid
                        var uniformZ = reader.ReadSingle();
                        Array.Fill(grid.ZMap, uniformZ);
                        break;

                    case 1: // Byte-quantized with min + step
                        var minZ1 = reader.ReadSingle();
                        var stepZ1 = reader.ReadSingle();
                        for (int i = 0; i < tileCount; i++)
                            grid.ZMap[i] = minZ1 + reader.ReadByte() * stepZ1;
                        break;

                    case 2: // Word-quantized with min + step
                        var minZ2 = reader.ReadSingle();
                        var stepZ2 = reader.ReadSingle();
                        for (int i = 0; i < tileCount; i++)
                            grid.ZMap[i] = minZ2 + reader.ReadUInt16() * stepZ2;
                        break;

                    case 3: // Full precision - float per tile
                        for (int i = 0; i < tileCount; i++)
                            grid.ZMap[i] = reader.ReadSingle();
                        break;

                    default:
                        // Unknown format - skip z-map
                        grid.ZMap = null;
                        break;
                }
            }

            // Parse overlays (claims, villages, provinces)
            // Format: [resource_name (null-terminated), version (uint16), bitpacked_data]...
            // Terminated by empty string
            if (ms.Position < ms.Length)
            {
                while (ms.Position < ms.Length)
                {
                    var resourceName = ReadNullTerminatedString(reader);
                    if (string.IsNullOrEmpty(resourceName))
                        break;

                    var resourceVersion = reader.ReadUInt16();
                    var dataLength = (tileCount + 7) / 8;  // 10000 tiles / 8 = 1250 bytes
                    var overlayData = reader.ReadBytes(dataLength);

                    grid.Overlays.Add(new HmapOverlayData
                    {
                        ResourceName = resourceName,
                        ResourceVersion = resourceVersion,
                        Data = overlayData
                    });
                }
            }
        }

        return grid;
    }

    // Tag ids of the game client's tagged-value encoding, used by version 2+ "mark" records.
    private const byte T_END = 0;
    private const byte T_INT = 1;
    private const byte T_STR = 2;
    private const byte T_COORD = 3;
    private const byte T_UINT8 = 4;
    private const byte T_UINT16 = 5;
    private const byte T_COLOR = 6;
    private const byte T_TTOL = 8;
    private const byte T_INT8 = 9;
    private const byte T_INT16 = 10;
    private const byte T_NIL = 12;
    private const byte T_UID = 13;
    private const byte T_BYTES = 14;
    private const byte T_FLOAT32 = 15;
    private const byte T_FLOAT64 = 16;
    private const byte T_FCOORD32 = 18;
    private const byte T_FCOORD64 = 19;
    private const byte T_RESID = 0x22;

    private readonly record struct TaggedCoord(int X, int Y);

    private readonly record struct TaggedResource(string Name, ushort Version);

    /// <summary>
    /// Parses a "mark" record. Two on-disk layouts exist and both are supported:
    ///
    ///   version 1  - fixed struct: segment id, tile coord, name, a type byte
    ///                ('p' PMarker / 's' SMarker) and that type's payload.
    ///   version 2+ - the tagged attribute map the game client switched to (exports in the
    ///                wild carry version 4): a marker-kind byte, then [T_STR key, tagged
    ///                value] pairs terminated by T_END. Observed keys: nm (name), c (tile
    ///                coord), seg (segment id), oid (object id), res (resource name +
    ///                version), color.
    ///
    /// The marker class is decided by the attributes present rather than by the kind byte,
    /// so a kind this reader has never seen still imports as long as it carries the usual
    /// attributes. Returns null with <paramref name="issue"/> set for records that cannot
    /// be decoded, so the caller can report them instead of silently dropping every marker
    /// in the file.
    /// </summary>
    private HmapMarkerData? ParseMarker(byte[] data, out string? issue)
    {
        issue = null;

        if (data.Length == 0)
        {
            issue = "empty mark record";
            return null;
        }

        var version = data[0];

        if (version == 1)
            return ParseMarkerV1(data, out issue);

        // Whether the kind byte is present is not documented, so try both framings and
        // keep whichever consumes the record exactly - a variant without it still reads.
        foreach (var (skipKindByte, requireExact) in new[] { (true, true), (false, true), (true, false) })
        {
            var marker = TryParseTaggedMarker(data, skipKindByte, requireExact);
            if (marker != null)
                return marker;
        }

        var kind = data.Length > 1 ? data[1] : (byte)0;
        issue = $"marker version {version} (kind 0x{kind:x2}) not understood";
        return null;
    }

    /// <summary>
    /// Legacy fixed-layout marker record (version 1).
    /// </summary>
    private static HmapMarkerData? ParseMarkerV1(byte[] data, out string? issue)
    {
        issue = null;

        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            reader.ReadByte(); // version (1)
            var segmentId = reader.ReadInt64();
            var tileX = reader.ReadInt32();
            var tileY = reader.ReadInt32();
            var name = ReadNullTerminatedString(reader);
            var markerType = (char)reader.ReadByte();

            switch (markerType)
            {
                case 'p': // PMarker - player-placed marker
                    return new HmapPMarker
                    {
                        SegmentId = segmentId,
                        TileX = tileX,
                        TileY = tileY,
                        Name = name,
                        ColorR = reader.ReadByte(),
                        ColorG = reader.ReadByte(),
                        ColorB = reader.ReadByte(),
                        ColorA = reader.ReadByte()
                    };

                case 's': // SMarker - system/game object (includes thingwalls)
                    return new HmapSMarker
                    {
                        SegmentId = segmentId,
                        TileX = tileX,
                        TileY = tileY,
                        Name = name,
                        ObjectId = reader.ReadInt64(),
                        ResourceName = ReadNullTerminatedString(reader),
                        ResourceVersion = reader.ReadUInt16()
                    };

                default:
                    issue = $"marker version 1 type '{markerType}' not understood";
                    return null;
            }
        }
        catch (EndOfStreamException)
        {
            issue = "truncated version 1 mark record";
            return null;
        }
    }

    /// <summary>
    /// Attempts one framing of the version 2+ tagged marker layout. Returns null when the
    /// bytes do not fit it, so the caller can fall through to the next candidate framing.
    /// </summary>
    private static HmapMarkerData? TryParseTaggedMarker(byte[] data, bool skipKindByte, bool requireExact)
    {
        try
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

            reader.ReadByte(); // version
            if (skipKindByte)
                reader.ReadByte(); // marker kind

            var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
            var terminated = false;

            while (ms.Position < ms.Length)
            {
                var tag = reader.ReadByte();
                if (tag == T_END)
                {
                    terminated = true;
                    break;
                }

                if (tag != T_STR)
                    return null; // not an attribute list - wrong framing

                attributes[ReadNullTerminatedString(reader)] = ReadTaggedValue(reader, ms);
            }

            if (!terminated)
                return null;

            if (requireExact && ms.Position != ms.Length)
                return null;

            return BuildTaggedMarker(attributes);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads one tagged value. Unknown tags throw: their payload length is unknown, so
    /// skipping one would desynchronise the rest of the record.
    /// </summary>
    private static object? ReadTaggedValue(BinaryReader reader, MemoryStream ms)
    {
        var tag = reader.ReadByte();

        switch (tag)
        {
            case T_INT: return (long)reader.ReadInt32();
            case T_STR: return ReadNullTerminatedString(reader);
            case T_COORD: return new TaggedCoord(reader.ReadInt32(), reader.ReadInt32());
            case T_UINT8: return (long)reader.ReadByte();
            case T_UINT16: return (long)reader.ReadUInt16();
            case T_COLOR: return reader.ReadBytes(4);
            case T_INT8: return (long)reader.ReadSByte();
            case T_INT16: return (long)reader.ReadInt16();
            case T_NIL: return null;
            case T_UID: return reader.ReadInt64();
            case T_FLOAT32: return (double)reader.ReadSingle();
            case T_FLOAT64: return reader.ReadDouble();
            case T_RESID: return new TaggedResource(ReadNullTerminatedString(reader), reader.ReadUInt16());

            case T_FCOORD32:
                reader.ReadSingle();
                reader.ReadSingle();
                return null;

            case T_FCOORD64:
                reader.ReadDouble();
                reader.ReadDouble();
                return null;

            case T_BYTES:
            {
                int length = reader.ReadByte();
                if ((length & 0x80) != 0)
                    length = reader.ReadInt32();
                return reader.ReadBytes(length);
            }

            case T_TTOL:
            {
                var list = new List<object?>();
                while (ms.Position < ms.Length)
                {
                    if (reader.ReadByte() == T_END)
                        break;
                    ms.Position--;
                    list.Add(ReadTaggedValue(reader, ms));
                }
                return list;
            }

            default:
                throw new InvalidDataException($"unknown marker value tag 0x{tag:x2}");
        }
    }

    /// <summary>
    /// Turns a decoded attribute map into a marker. A resource reference means an SMarker
    /// (natural feature, thingwall, ...); anything else is treated as a player marker.
    /// </summary>
    private static HmapMarkerData? BuildTaggedMarker(Dictionary<string, object?> attributes)
    {
        // A marker without a position cannot be placed on a map.
        if (attributes.GetValueOrDefault("c") is not TaggedCoord coord)
            return null;

        var segmentId = attributes.GetValueOrDefault("seg") as long? ?? 0L;
        var name = attributes.GetValueOrDefault("nm") as string ?? "";

        if (attributes.GetValueOrDefault("res") is TaggedResource resource)
        {
            return new HmapSMarker
            {
                SegmentId = segmentId,
                TileX = coord.X,
                TileY = coord.Y,
                Name = name,
                ObjectId = attributes.GetValueOrDefault("oid") as long? ?? 0L,
                ResourceName = resource.Name,
                ResourceVersion = resource.Version
            };
        }

        var color = attributes.GetValueOrDefault("color") as byte[];
        var hasColor = color is { Length: 4 };

        return new HmapPMarker
        {
            SegmentId = segmentId,
            TileX = coord.X,
            TileY = coord.Y,
            Name = name,
            ColorR = hasColor ? color![0] : (byte)255,
            ColorG = hasColor ? color![1] : (byte)255,
            ColorB = hasColor ? color![2] : (byte)255,
            ColorA = hasColor ? color![3] : (byte)255
        };
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = reader.ReadByte()) != 0)
        {
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

/// <summary>
/// Parsed .hmap file data
/// </summary>
public class HmapData
{
    public string Signature { get; set; } = "";
    public List<HmapGridData> Grids { get; set; } = new();
    public List<HmapMarkerData> Markers { get; set; } = new();
    public List<string> UnknownRecords { get; set; } = new();

    /// <summary>
    /// Number of "mark" records this reader could not decode (for example a marker layout
    /// newer than any version it knows). Non-zero means markers were dropped.
    /// </summary>
    public int MarkersUnreadable { get; set; }

    /// <summary>
    /// Distinct reasons behind <see cref="MarkersUnreadable"/>, for logging.
    /// </summary>
    public HashSet<string> MarkerParseIssues { get; } = new();

    /// <summary>
    /// Get all unique segment IDs (each segment is a separate map region)
    /// </summary>
    public IEnumerable<long> GetSegmentIds() => Grids.Select(g => g.SegmentId).Distinct();

    /// <summary>
    /// Get grids for a specific segment
    /// </summary>
    public List<HmapGridData> GetGridsForSegment(long segmentId) =>
        Grids.Where(g => g.SegmentId == segmentId).ToList();

    /// <summary>
    /// Get markers for a specific segment
    /// </summary>
    public List<HmapMarkerData> GetMarkersForSegment(long segmentId) =>
        Markers.Where(m => m.SegmentId == segmentId).ToList();
}

/// <summary>
/// Grid data from .hmap file
/// </summary>
public class HmapGridData
{
    public byte Version { get; set; }
    public long GridId { get; set; }
    public long SegmentId { get; set; }
    public long ModifiedTime { get; set; }
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int GridSize { get; set; }
    public List<HmapTilesetInfo> Tilesets { get; set; } = new();
    public int[]? TileIndices { get; set; }

    /// <summary>
    /// Height map data for cliff/ridge detection (100x100 floats)
    /// </summary>
    public float[]? ZMap { get; set; }

    /// <summary>
    /// Overlay data (claims, villages, provinces) - bitpacked boolean arrays
    /// </summary>
    public List<HmapOverlayData> Overlays { get; set; } = new();

    /// <summary>
    /// Get GridId as string for storage in database
    /// </summary>
    public string GridIdString => GridId.ToString();
}

/// <summary>
/// Tileset information from .hmap grid
/// </summary>
public class HmapTilesetInfo
{
    public string ResourceName { get; set; } = "";
    public ushort ResourceVersion { get; set; }
    public byte Priority { get; set; }
}

/// <summary>
/// Base marker data from .hmap file
/// </summary>
public abstract class HmapMarkerData
{
    public long SegmentId { get; set; }
    public int TileX { get; set; }
    public int TileY { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>
/// Player-placed marker (PMarker)
/// </summary>
public class HmapPMarker : HmapMarkerData
{
    public byte ColorR { get; set; }
    public byte ColorG { get; set; }
    public byte ColorB { get; set; }
    public byte ColorA { get; set; }
}

/// <summary>
/// System/game object marker (SMarker) - includes thingwalls
/// </summary>
public class HmapSMarker : HmapMarkerData
{
    public long ObjectId { get; set; }
    public string ResourceName { get; set; } = "";
    public ushort ResourceVersion { get; set; }
}

/// <summary>
/// Overlay data from .hmap grid (claims, villages, provinces)
/// </summary>
public class HmapOverlayData
{
    /// <summary>
    /// Resource name (e.g., "gfx/tiles/claims/claimfloor")
    /// </summary>
    public string ResourceName { get; set; } = string.Empty;

    /// <summary>
    /// Resource version
    /// </summary>
    public ushort ResourceVersion { get; set; }

    /// <summary>
    /// Bitpacked overlay data (1250 bytes for 100x100 grid, LSB first)
    /// Each bit represents whether the overlay is present at that tile position.
    /// </summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();
}
