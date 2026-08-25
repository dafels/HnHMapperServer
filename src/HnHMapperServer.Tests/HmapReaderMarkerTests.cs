using System.Text;
using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// Regression tests for the "markers are not imported" report: the game client changed the
/// "mark" record layout from the fixed version-1 struct to a tagged attribute map (exports
/// in the wild carry version 4). <see cref="HmapReader"/> rejected anything but version 1
/// and returned null, so every marker in a current export was dropped before the import
/// service ever saw one — and because the import's marker phase is gated on
/// <c>Markers.Count &gt; 0</c> the result reported 0 imported / 0 skipped, with no error.
///
/// The v4 bytes below are copied from a real export (test.hmap): a "Fairy Stone" SMarker
/// carrying res / c / seg / oid / nm.
/// </summary>
public class HmapReaderMarkerTests
{
    private const string Signature = "Haven Mapfile 1";

    // Tagged-value tag ids used by the version 2+ marker layout.
    private const byte TEnd = 0x00;
    private const byte TStr = 0x02;
    private const byte TCoord = 0x03;
    private const byte TColor = 0x06;
    private const byte TUid = 0x0D;
    private const byte TResid = 0x22;

    // The marker-kind byte every observed v4 record starts with.
    private const byte KindByte = 0x20;

    [Fact]
    public void Read_TaggedSMarker_IsParsed()
    {
        var record = TaggedMarker(
            kindByte: KindByte,
            Attr("res", Resource("gfx/terobjs/mm/fairystone", 2)),
            Attr("c", Coord(-821, -435)),
            Attr("seg", Uid(-1866084793332318016L)),
            Attr("oid", Uid(7673937158616386484L)),
            Attr("nm", Str("Fairy Stone")));

        var data = new HmapReader().Read(BuildHmap(("mark", record)));

        Assert.Equal(0, data.MarkersUnreadable);
        var marker = Assert.IsType<HmapSMarker>(Assert.Single(data.Markers));
        Assert.Equal("Fairy Stone", marker.Name);
        Assert.Equal("gfx/terobjs/mm/fairystone", marker.ResourceName);
        Assert.Equal(2, marker.ResourceVersion);
        Assert.Equal(-1866084793332318016L, marker.SegmentId);
        Assert.Equal(7673937158616386484L, marker.ObjectId);
        Assert.Equal(-821, marker.TileX);
        Assert.Equal(-435, marker.TileY);
    }

    [Fact]
    public void Read_TaggedPlayerMarker_IsParsedWithColor()
    {
        // No resource reference, so this is a player-placed marker.
        var record = TaggedMarker(
            kindByte: 0x10,
            Attr("c", Coord(1200, -40)),
            Attr("seg", Uid(42L)),
            Attr("nm", Str("Home")),
            Attr("color", Color(255, 128, 0, 255)));

        var data = new HmapReader().Read(BuildHmap(("mark", record)));

        Assert.Equal(0, data.MarkersUnreadable);
        var marker = Assert.IsType<HmapPMarker>(Assert.Single(data.Markers));
        Assert.Equal("Home", marker.Name);
        Assert.Equal(42L, marker.SegmentId);
        Assert.Equal(1200, marker.TileX);
        Assert.Equal(-40, marker.TileY);
        Assert.Equal(255, marker.ColorR);
        Assert.Equal(128, marker.ColorG);
        Assert.Equal(0, marker.ColorB);
        Assert.Equal(255, marker.ColorA);
    }

    [Fact]
    public void Read_TaggedMarkerWithoutKindByte_IsStillParsed()
    {
        // The kind byte is undocumented, so the reader tries both framings. A record whose
        // attribute list starts straight after the version byte must still read.
        var record = TaggedMarker(
            kindByte: null,
            Attr("c", Coord(500, 500)),
            Attr("nm", Str("Kindless")));

        var data = new HmapReader().Read(BuildHmap(("mark", record)));

        Assert.Equal(0, data.MarkersUnreadable);
        var marker = Assert.Single(data.Markers);
        Assert.Equal("Kindless", marker.Name);
        Assert.Equal(500, marker.TileX);
        Assert.Equal(500, marker.TileY);
    }

    [Theory]
    [InlineData((byte)'p')]
    [InlineData((byte)'s')]
    public void Read_LegacyVersion1Marker_StillParses(byte type)
    {
        // Files exported before the format change (and every existing .hmap source in the
        // library) carry version 1 records — they must keep working.
        var body = new MemoryStream();
        using (var w = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((byte)1);
            w.Write(-99L);              // segment
            w.Write(1129);              // tile X
            w.Write(1039);              // tile Y
            w.Write(Encoding.UTF8.GetBytes("Burrow"));
            w.Write((byte)0);
            w.Write(type);
            if (type == (byte)'p')
            {
                w.Write((byte)255); w.Write((byte)0); w.Write((byte)0); w.Write((byte)255);
            }
            else
            {
                w.Write(7L);            // object id
                w.Write(Encoding.UTF8.GetBytes("gfx/terobjs/mm/burrow"));
                w.Write((byte)0);
                w.Write((ushort)1);
            }
        }

        var data = new HmapReader().Read(BuildHmap(("mark", body.ToArray())));

        Assert.Equal(0, data.MarkersUnreadable);
        var marker = Assert.Single(data.Markers);
        Assert.Equal("Burrow", marker.Name);
        Assert.Equal(-99L, marker.SegmentId);
        Assert.Equal(1129, marker.TileX);
        Assert.Equal(1039, marker.TileY);
        if (type == (byte)'s')
            Assert.Equal("gfx/terobjs/mm/burrow", Assert.IsType<HmapSMarker>(marker).ResourceName);
        else
            Assert.IsType<HmapPMarker>(marker);
    }

    [Fact]
    public void Read_UndecodableMarker_IsReportedAndDoesNotDropTheRest()
    {
        // The failure mode that hid this bug: an unknown layout must be counted and
        // explained, not silently swallowed, and it must not take readable markers with it.
        var unreadable = new byte[] { 9, 0xEE, 0xEE, 0xEE, 0xEE };
        var readable = TaggedMarker(KindByte, Attr("c", Coord(10, 20)), Attr("nm", Str("Fine")));

        var data = new HmapReader().Read(BuildHmap(("mark", unreadable), ("mark", readable)));

        Assert.Equal(1, data.MarkersUnreadable);
        Assert.NotEmpty(data.MarkerParseIssues);
        Assert.Equal("Fine", Assert.Single(data.Markers).Name);
    }

    // ---- builders -------------------------------------------------------------------

    private static byte[] Attr(string key, byte[] value)
    {
        var buf = new MemoryStream();
        buf.WriteByte(TStr);
        buf.Write(Encoding.UTF8.GetBytes(key));
        buf.WriteByte(0);
        buf.Write(value);
        return buf.ToArray();
    }

    private static byte[] Str(string value)
    {
        var buf = new MemoryStream();
        buf.WriteByte(TStr);
        buf.Write(Encoding.UTF8.GetBytes(value));
        buf.WriteByte(0);
        return buf.ToArray();
    }

    private static byte[] Coord(int x, int y)
    {
        var buf = new MemoryStream();
        buf.WriteByte(TCoord);
        buf.Write(BitConverter.GetBytes(x));
        buf.Write(BitConverter.GetBytes(y));
        return buf.ToArray();
    }

    private static byte[] Uid(long value)
    {
        var buf = new MemoryStream();
        buf.WriteByte(TUid);
        buf.Write(BitConverter.GetBytes(value));
        return buf.ToArray();
    }

    private static byte[] Color(byte r, byte g, byte b, byte a) => new[] { TColor, r, g, b, a };

    private static byte[] Resource(string name, ushort version)
    {
        var buf = new MemoryStream();
        buf.WriteByte(TResid);
        buf.Write(Encoding.UTF8.GetBytes(name));
        buf.WriteByte(0);
        buf.Write(BitConverter.GetBytes(version));
        return buf.ToArray();
    }

    private static byte[] TaggedMarker(byte? kindByte, params byte[][] attributes)
    {
        var buf = new MemoryStream();
        buf.WriteByte(4); // version
        if (kindByte.HasValue)
            buf.WriteByte(kindByte.Value);
        foreach (var attribute in attributes)
            buf.Write(attribute);
        buf.WriteByte(TEnd);
        return buf.ToArray();
    }

    private static MemoryStream BuildHmap(params (string Type, byte[] Body)[] records)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes(Signature));
        ms.WriteByte(0x78);
        ms.WriteByte(0xDA);
        using (var deflate = new System.IO.Compression.DeflateStream(
                   ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            foreach (var (type, body) in records)
            {
                deflate.Write(Encoding.UTF8.GetBytes(type));
                deflate.WriteByte(0);
                deflate.Write(BitConverter.GetBytes(body.Length));
                deflate.Write(body);
            }
        }
        ms.Position = 0;
        return ms;
    }
}
