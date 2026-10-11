using System.Text;
using System.Text.RegularExpressions;
using HnHMapperServer.Core.Constants;

namespace HnHMapperServer.Tests;

/// <summary>
/// The /map/updates liveness contract. The server's keep-alive used to be an SSE comment (": keep-alive"), which
/// EventSource never surfaces to JavaScript, so map-updates.js's watchdog closed every quiet stream and reopened it
/// about every 17 s (and the old watchdog's failure count pushed tabs into a polling fallback they never left).
/// The heartbeat is now a named event. These tests run the frames through the WHATWG event-stream interpretation
/// rules - the same rules every browser's EventSource applies - so they document what JS actually receives.
/// </summary>
public class SseFramesTests
{
    private sealed record DispatchedEvent(string Type, string Data);

    /// <summary>
    /// Minimal WHATWG event-stream parser (HTML spec, "Interpreting an event stream"): lines end at LF, CR or CRLF;
    /// a blank line dispatches the buffered event, but only when the data buffer is non-empty; lines starting with
    /// ':' are comments and ignored; one leading space after the field colon is stripped; "event" sets the type
    /// (default "message"), "data" appends value + LF (the last LF is removed at dispatch); the type resets after
    /// each dispatch. A trailing line without a terminator is discarded, as at end of stream.
    /// </summary>
    private static List<DispatchedEvent> Dispatch(string stream)
    {
        var events = new List<DispatchedEvent>();
        var type = "";
        var data = new StringBuilder();

        var lines = stream.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        // Split leaves the text after the last terminator as the final element: an unterminated line
        foreach (var line in lines.Take(lines.Length - 1))
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    events.Add(new DispatchedEvent(type.Length > 0 ? type : "message", data.ToString(0, data.Length - 1)));
                }
                type = "";
                data.Clear();
                continue;
            }

            if (line[0] == ':')
                continue;

            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];

            switch (field)
            {
                case "event":
                    type = value;
                    break;
                case "data":
                    data.Append(value).Append('\n');
                    break;
            }
        }

        return events;
    }

    [Fact]
    public void Heartbeat_DispatchesExactlyOneNamedEvent()
    {
        var events = Dispatch(SseFrames.Heartbeat);

        // Named, so it never reaches onmessage: unnamed events ("message") are tile batches in map-updates.js
        var heartbeat = Assert.Single(events);
        Assert.Equal(SseFrames.HeartbeatEvent, heartbeat.Type);
        Assert.Equal("{}", heartbeat.Data);
    }

    [Fact]
    public void CommentKeepAlive_DispatchesNothing()
    {
        // Why the old keep-alive never reached the watchdog: comments are swallowed by the parser itself
        Assert.Empty(Dispatch(": keep-alive\n\n"));
    }

    [Fact]
    public void HeartbeatFollowedByUnnamedTileBatch_SecondIsAMessage()
    {
        // The heartbeat terminates its own frame and the event type resets, so the next unnamed frame is a tile batch
        var events = Dispatch(SseFrames.Heartbeat + "data: [{\"M\":1}]\n\n");

        Assert.Equal(2, events.Count);
        Assert.Equal(SseFrames.HeartbeatEvent, events[0].Type);
        Assert.Equal("message", events[1].Type);
        Assert.Equal("[{\"M\":1}]", events[1].Data);
    }
}

/// <summary>
/// The heartbeat is a pair: the API must send it and map-updates.js must listen for it (new JS against an old API
/// sees no heartbeat and reconnects every 20 s), and the API must subscribe before it reads its snapshot. This project
/// cannot reference Api or Web, so it reads the copied sources as text (see the csproj) - tripwires against reverting
/// either half, not behaviour tests.
/// </summary>
public class SseWireContractTests
{
    private static string ReadCopied(string service, string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sse-contract", service, fileName));

    // Comments are stripped before matching: they may mention what the code must not do. The C# file uses only
    // line comments (incl. XML docs, one of which ends in "/map/api/*"), the JS file both kinds.
    private static string StripCSharpComments(string source) => Regex.Replace(source, @"//[^\r\n]*", "");

    private static string StripJsComments(string source) =>
        Regex.Replace(Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\r\n]*", "");

    [Fact]
    public void MapEndpoints_SendsTheNamedHeartbeat_NeverACommentKeepAlive()
    {
        var code = StripCSharpComments(ReadCopied("Api", "MapEndpoints.cs.txt"));

        Assert.Contains("SseFrames.Heartbeat", code);
        Assert.DoesNotContain("\": keep-alive", code);
    }

    [Fact]
    public void MapEndpoints_SubscribesBeforeTheSnapshotReads()
    {
        // A stream lives for many minutes now; an event published between a snapshot read and the hub
        // subscription would stay lost for all of them (ghost characters, viewers stuck on an old ?v=)
        var code = StripCSharpComments(ReadCopied("Api", "MapEndpoints.cs.txt"));
        var start = code.IndexOf("Task WatchGridUpdates(", StringComparison.Ordinal);
        Assert.True(start >= 0, "WatchGridUpdates not found");
        var end = code.IndexOf("private static", start, StringComparison.Ordinal);
        var method = end > start ? code[start..end] : code[start..];

        var lastSubscribe = method.LastIndexOf("updateNotificationService.SubscribeTo", StringComparison.Ordinal);
        Assert.True(lastSubscribe >= 0, "no hub subscription found in WatchGridUpdates");
        foreach (var read in new[] { "GetTilesSinceAsync(", "revisionCache.GetAll()", "GetAllCharacters(" })
        {
            var at = method.IndexOf(read, StringComparison.Ordinal);
            Assert.True(at >= 0, $"snapshot read {read} not found in WatchGridUpdates");
            Assert.True(lastSubscribe < at, $"{read} runs before the last hub subscription");
        }
    }

    [Fact]
    public void MapUpdatesJs_ListensForTheHeartbeat_AndNeverPolls()
    {
        var js = StripJsComments(ReadCopied("Web", "map-updates.js.txt"));

        // The listener itself, not just the name: without it a heartbeat-only stream never turns healthy and
        // the "reconnecting" chip stays up. Registered directly or through the listen() helper.
        Assert.Matches($@"\bconst\s+HEARTBEAT_EVENT\s*=\s*'{Regex.Escape(SseFrames.HeartbeatEvent)}'", js);
        Assert.Matches(@"\b(?:addEventListener|listen)\(\s*HEARTBEAT_EVENT\s*,", js);
        // The polling fallback and its endpoint are gone
        Assert.DoesNotContain("/map/api/v1/poll", js);
    }
}
