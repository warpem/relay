using Microsoft.Extensions.Logging.Abstractions;
using Refund.Configuration;
using Refund.DataModel;
using Refund.Services;

namespace Refund.Tests.Mcp;

public class PersonalAccessTokenServiceTests
{
    private static PersonalAccessTokenService NewService(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var config = new RelayConfiguration { PatsPath = path };
        return new PersonalAccessTokenService(NullLogger<PersonalAccessTokenService>.Instance, config);
    }

    [Fact]
    public async Task Generate_ReturnsPrefixedRawToken()
    {
        var svc = NewService(out _);
        var raw = await svc.Generate(7, "laptop", AccessLevel.Read, AccessLevel.Read, AccessLevel.Read);
        Assert.StartsWith("relay_pat_", raw);
    }

    [Fact]
    public async Task Generate_DoesNotPersistRawToken()
    {
        var svc = NewService(out var path);
        var raw = await svc.Generate(7, "laptop", AccessLevel.Read, AccessLevel.Read, AccessLevel.Read);
        var fileContents = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain(raw, fileContents);
        Assert.Contains(PersonalAccessTokenService.HashToken(raw), fileContents);
    }

    [Fact]
    public async Task Validate_ReturnsOwnerId_AndStampsLastUsed()
    {
        var svc = NewService(out _);
        var raw = await svc.Generate(42, "laptop", AccessLevel.Read, AccessLevel.Read, AccessLevel.Read);
        var pat = svc.Validate(raw);
        Assert.Equal(42, pat!.OwnerUserId);
        Assert.NotNull(svc.ListForUser(42).Single().LastUsedDate);
    }

    [Fact]
    public void Validate_UnknownToken_ReturnsNull()
    {
        var svc = NewService(out _);
        Assert.Null(svc.Validate("relay_pat_bogus"));
    }

    [Fact]
    public async Task Validate_ExpiredToken_ReturnsNull()
    {
        var svc = NewService(out _);
        var raw = await svc.Generate(1, "old", AccessLevel.Read, AccessLevel.Read, AccessLevel.Read, expiry: DateTime.UtcNow.AddMinutes(-1));
        Assert.Null(svc.Validate(raw));
    }

    [Fact]
    public async Task Revoke_RemovesToken_SoItNoLongerValidates()
    {
        var svc = NewService(out _);
        var raw = await svc.Generate(5, "laptop", AccessLevel.Read, AccessLevel.Read, AccessLevel.Read);
        var id = svc.ListForUser(5).Single().Id;
        await svc.Revoke(5, id);
        Assert.Empty(svc.ListForUser(5));
        Assert.Null(svc.Validate(raw));
    }

    [Fact]
    public async Task Revoke_OtherUsersToken_DoesNothing()
    {
        var svc = NewService(out _);
        await svc.Generate(5, "mine", AccessLevel.Read, AccessLevel.Read, AccessLevel.Read);
        var id = svc.ListForUser(5).Single().Id;
        await svc.Revoke(ownerUserId: 999, tokenId: id);
        Assert.Single(svc.ListForUser(5));
    }

    [Fact]
    public async Task Tokens_SurviveReload()
    {
        var svc = NewService(out var path);
        var raw = await svc.Generate(8, "laptop", AccessLevel.Read, AccessLevel.Read, AccessLevel.Read);

        var config = new RelayConfiguration { PatsPath = path };
        var reloaded = new PersonalAccessTokenService(NullLogger<PersonalAccessTokenService>.Instance, config);
        Assert.Equal(8, reloaded.Validate(raw)!.OwnerUserId);
    }
}
