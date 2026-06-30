using System.Text.Json.Nodes;
using Refund.DataModel;
using Xunit;

namespace Refund.Tests.Mcp;

public class AccessLevelSerializationTests
{
    [Fact]
    public void PersonalAccessToken_RoundTripsAccessLevels()
    {
        var original = new PersonalAccessToken
        {
            Id = 7,
            TokenHash = "abc",
            Name = "t",
            OwnerUserId = 3,
            ProjectAccess = AccessLevel.Read,
            SpaceAccess = AccessLevel.EditRun,
            JobAccess = AccessLevel.Manage
        };

        var node = new JsonObject();
        original.WriteToJson(node);

        var restored = new PersonalAccessToken();
        restored.ReadFromJson(node);

        Assert.Equal(AccessLevel.Read, restored.ProjectAccess);
        Assert.Equal(AccessLevel.EditRun, restored.SpaceAccess);
        Assert.Equal(AccessLevel.Manage, restored.JobAccess);
    }

    [Fact]
    public void DefaultAccessLevels_AreNone()
    {
        var pat = new PersonalAccessToken();
        Assert.Equal(AccessLevel.None, pat.ProjectAccess);
        Assert.Equal(AccessLevel.None, pat.SpaceAccess);
        Assert.Equal(AccessLevel.None, pat.JobAccess);
    }
}
