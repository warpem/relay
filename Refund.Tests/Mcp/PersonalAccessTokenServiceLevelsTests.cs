using Microsoft.Extensions.Logging.Abstractions;
using Refund.Configuration;
using Refund.DataModel;
using Refund.Services;
using Xunit;

namespace Refund.Tests.Mcp;

public class PersonalAccessTokenServiceLevelsTests
{
    private static PersonalAccessTokenService NewService(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), $"pats-{Guid.NewGuid():N}.relay");
        var config = new RelayConfiguration { PatsPath = path };
        return new PersonalAccessTokenService(NullLogger<PersonalAccessTokenService>.Instance, config);
    }

    [Fact]
    public async Task Generate_PersistsLevels()
    {
        var svc = NewService(out var path);
        try
        {
            await svc.Generate(42, "agent", AccessLevel.Read, AccessLevel.EditRun, AccessLevel.Manage);
            var stored = svc.ListForUser(42);
            Assert.Single(stored);
            Assert.Equal(AccessLevel.Read, stored[0].ProjectAccess);
            Assert.Equal(AccessLevel.EditRun, stored[0].SpaceAccess);
            Assert.Equal(AccessLevel.Manage, stored[0].JobAccess);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
