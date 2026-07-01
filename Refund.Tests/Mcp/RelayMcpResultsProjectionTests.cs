using Refund.DataModel;
using Refund.Mcp;

namespace Refund.Tests.Mcp;

public class RelayMcpResultsProjectionTests
{
    [Fact]
    public void ResolveResultIteration_UsesRequested_WhenProvided()
    {
        Assert.Equal(3, RelayMcpProjections.ResolveResultIteration(3, 10, _ => false));
    }

    [Fact]
    public void ResolveResultIteration_PicksLatestWithResults_WhenNull()
    {
        var withResults = new HashSet<int> { 0, 2, 5 };
        Assert.Equal(5, RelayMcpProjections.ResolveResultIteration(null, 7, withResults.Contains));
    }

    [Fact]
    public void ResolveResultIteration_ReturnsMinusOne_WhenNoneHaveResults()
    {
        Assert.Equal(-1, RelayMcpProjections.ResolveResultIteration(null, 4, _ => false));
    }

    [Fact]
    public void ToResultDto_MapsFields()
    {
        var d = new Downloadable("Half-map 1", "the first half map", "/data/job/half1.mrc");
        var dto = RelayMcpProjections.ToResultDto("Volume", d, 5);
        Assert.Equal("Volume", dto.Port);
        Assert.Equal("Half-map 1", dto.Name);
        Assert.Equal("the first half map", dto.Description);
        Assert.Equal(5, dto.Iteration);
    }

    [Fact]
    public void MatchDownloadable_FindsByPortAndName()
    {
        var items = new (string, Downloadable)[]
        {
            ("Volume", new Downloadable("Half-map 1", "", "/d/h1.mrc")),
            ("Volume", new Downloadable("Mask", "", "/d/mask.mrc")),
        };
        var match = RelayMcpProjections.MatchDownloadable(items, "Volume", "Mask");
        Assert.NotNull(match);
        Assert.Equal("/d/mask.mrc", match.ServerPath);
    }

    [Fact]
    public void MatchDownloadable_ReturnsNull_WhenNoMatch()
    {
        var items = new (string, Downloadable)[] { ("Volume", new Downloadable("Half-map 1", "", "/d/h1.mrc")) };
        Assert.Null(RelayMcpProjections.MatchDownloadable(items, "Volume", "Nonexistent"));
    }
}
