using Refund.Services.Core.Session;

namespace Refund.Tests.Services;

public class RelaySessionUrlTests
{
    [Fact]
    public void BuildUrl_FactoryDefinition_FormatsCorrectly()
    {
        var request = new NavigationRequest
        {
            ProjectId = 1,
            SpaceId = 2,
            FactoryDefinitionId = 3
        };
        Assert.Equal("/P1/S2/FD3", RelaySession.BuildUrl(request));
    }

    [Fact]
    public void BuildUrl_FactoryInstance_InView_FormatsCorrectly()
    {
        var request = new NavigationRequest
        {
            ProjectId = 1,
            SpaceId = 2,
            ViewId = 4,
            FactoryInstanceId = 5
        };
        Assert.Equal("/P1/S2/V4/FI5", RelaySession.BuildUrl(request));
    }

    [Fact]
    public void BuildUrl_FactoryInstance_InFolder_FormatsCorrectly()
    {
        var request = new NavigationRequest
        {
            ProjectId = 1,
            SpaceId = 2,
            ViewId = 4,
            FolderId = 3,
            FactoryInstanceId = 5
        };
        Assert.Equal("/P1/S2/V4/F3/FI5", RelaySession.BuildUrl(request));
    }

    [Fact]
    public void BuildUrl_FactoryDefinition_ExcludesViewLevel()
    {
        var request = new NavigationRequest
        {
            ProjectId = 1,
            SpaceId = 2,
            FactoryDefinitionId = 3,
            ViewId = 4
        };
        Assert.Equal("/P1/S2/FD3", RelaySession.BuildUrl(request));
    }
}
