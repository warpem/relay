using Microsoft.Extensions.Logging.Abstractions;
using Refund.Configuration;
using Refund.DataModel;
using Refund.Services;
using Xunit;

namespace Refund.Tests.Mcp;

public class PersonalAccessTokenServiceValidateTests
{
    private static PersonalAccessTokenService NewService(string path) =>
        new(NullLogger<PersonalAccessTokenService>.Instance, new RelayConfiguration { PatsPath = path });

    [Fact]
    public async Task Validate_ReturnsTokenWithLevels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pats-{Guid.NewGuid():N}.relay");
        try
        {
            var svc = NewService(path);
            var raw = await svc.Generate(9, "a", AccessLevel.None, AccessLevel.EditRun, AccessLevel.Read);
            var pat = svc.Validate(raw);
            Assert.NotNull(pat);
            Assert.Equal(9, pat!.OwnerUserId);
            Assert.Equal(AccessLevel.EditRun, pat.SpaceAccess);
            Assert.Null(svc.Validate("relay_pat_nope"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task Load_MigratesAllNoneTokenToManage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pats-{Guid.NewGuid():N}.relay");
        try
        {
            // First service writes a legacy token whose levels are all None.
            var first = NewService(path);
            var raw = await first.Generate(5, "legacy", AccessLevel.None, AccessLevel.None, AccessLevel.None);

            // Second service loads it from disk and should migrate.
            var second = NewService(path);
            var pat = second.Validate(raw);
            Assert.NotNull(pat);
            Assert.Equal(AccessLevel.Manage, pat!.ProjectAccess);
            Assert.Equal(AccessLevel.Manage, pat.SpaceAccess);
            Assert.Equal(AccessLevel.Manage, pat.JobAccess);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
