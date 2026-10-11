using HnHMapperServer.Web.Services.Map;
using Microsoft.Extensions.Logging.Abstractions;

namespace HnHMapperServer.Web.Tests;

/// <summary>The .NET revision copy behind the OnSseMapRevision fallback (map-updates.js replays every revision on connect).</summary>
public sealed class MapNavigationServiceTests
{
    private readonly MapNavigationService _sut = new(NullLogger<MapNavigationService>.Instance);

    [Fact]
    public void SetMapRevision_reports_new_and_changed_values_and_rejects_duplicates()
    {
        Assert.True(_sut.SetMapRevision(7, 3));
        Assert.False(_sut.SetMapRevision(7, 3));
        Assert.True(_sut.SetMapRevision(7, 4));
        // Any other value is applied, including a lower one (an out-of-band import revision)
        Assert.True(_sut.SetMapRevision(7, 2));
        Assert.Equal(2, _sut.GetMapRevision(7));
    }

    [Fact]
    public void InitializeMapRevision_only_seeds_unknown_maps()
    {
        _sut.SetMapRevision(1, 9);

        _sut.InitializeMapRevision(1, 2);
        _sut.InitializeMapRevision(2, 5);

        Assert.Equal(9, _sut.GetMapRevision(1));
        Assert.Equal(5, _sut.GetMapRevision(2));
    }
}
