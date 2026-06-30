using Refund.DataModel;
using Refund.Mcp;
using Xunit;

namespace Refund.Tests.Mcp;

public class PatAuthorizationTests
{
    [Theory]
    [InlineData(AccessLevel.EditRun, AccessLevel.EditRun, true)]  // exact
    [InlineData(AccessLevel.Manage, AccessLevel.EditRun, true)]   // higher allows lower
    [InlineData(AccessLevel.Read, AccessLevel.EditRun, false)]    // lower denies
    [InlineData(AccessLevel.None, AccessLevel.Read, false)]
    [InlineData(AccessLevel.EditRun, AccessLevel.Manage, false)]  // delete needs Manage
    public void Allows_RespectsOrdering(AccessLevel held, AccessLevel required, bool expected)
    {
        var grants = new PatGrants(held, AccessLevel.None, AccessLevel.None);
        Assert.Equal(expected, PatAuthorization.Allows(grants, PermTier.Project, required));
    }

    [Fact]
    public void Allows_ChecksTheRequestedTierOnly()
    {
        var grants = new PatGrants(AccessLevel.None, AccessLevel.None, AccessLevel.Manage);
        Assert.True(PatAuthorization.Allows(grants, PermTier.Job, AccessLevel.Manage));
        Assert.False(PatAuthorization.Allows(grants, PermTier.Project, AccessLevel.Read));
        Assert.False(PatAuthorization.Allows(grants, PermTier.Space, AccessLevel.Read));
    }

    [Fact]
    public void From_MapsPatFields()
    {
        var pat = new PersonalAccessToken
        {
            ProjectAccess = AccessLevel.Read,
            SpaceAccess = AccessLevel.EditRun,
            JobAccess = AccessLevel.Manage
        };
        var grants = PatAuthorization.From(pat);
        Assert.Equal(AccessLevel.Read, grants.Project);
        Assert.Equal(AccessLevel.EditRun, grants.Space);
        Assert.Equal(AccessLevel.Manage, grants.Job);
    }
}
