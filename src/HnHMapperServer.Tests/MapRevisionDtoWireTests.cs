using System.Text.Json;
using HnHMapperServer.Core.DTOs;
using HnHMapperServer.Services.Interfaces;
using Moq;

namespace HnHMapperServer.Tests;

/// <summary>
/// The SSE `mapRevision` payload (MapEndpoints serializes MapRevisionDto with camelCase options):
/// unchanged for every bump without cells, `cells` added for ZoomQ bumps — the shape map-updates.js
/// reads (`revision.cells`).
/// </summary>
public class MapRevisionDtoWireTests
{
    private static readonly JsonSerializerOptions SseOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public void WithoutCells_IsByteIdenticalToThePreviousPayload()
    {
        var json = JsonSerializer.Serialize(new MapRevisionDto { MapId = 5, Revision = 7 }, SseOptions);

        Assert.Equal(JsonSerializer.Serialize(new { MapId = 5, Revision = 7 }, SseOptions), json);
        Assert.Equal("{\"mapId\":5,\"revision\":7}", json);
    }

    [Fact]
    public void WithCells_AddsACamelCaseArrayOfPairs()
    {
        var json = JsonSerializer.Serialize(
            new MapRevisionDto { MapId = 5, Revision = 7, Cells = new[] { new[] { 1, -2 }, new[] { 3, 4 } } },
            SseOptions);

        Assert.Equal("{\"mapId\":5,\"revision\":7,\"cells\":[[1,-2],[3,4]]}", json);
    }

    [Fact]
    public void TwoArgumentOverload_StaysARealMember()
    {
        // Compiles only while NotifyMapRevision(int, int) exists as its own overload: an optional
        // third parameter would make this expression tree an error (CS0854) in every mock-based test
        var notifications = new Mock<IUpdateNotificationService>();

        notifications.Object.NotifyMapRevision(1, 2, null);

        notifications.Verify(n => n.NotifyMapRevision(It.IsAny<int>(), It.IsAny<int>()), Times.Never());
        notifications.Verify(n => n.NotifyMapRevision(1, 2, null), Times.Once());
    }
}
