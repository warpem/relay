using Refund.DataModel;

namespace Refund.Tests.Mcp;

public class PersonalAccessTokenTests
{
    [Fact]
    public void NewToken_HasNoExpiryByDefault_AndIsNotExpired()
    {
        var pat = new PersonalAccessToken();
        Assert.Null(pat.ExpirationDate);
        Assert.Null(pat.LastUsedDate);
        Assert.False(pat.IsExpired);
    }

    [Fact]
    public void Token_WithPastExpiration_IsExpired()
    {
        var pat = new PersonalAccessToken { ExpirationDate = DateTime.UtcNow.AddMinutes(-1) };
        Assert.True(pat.IsExpired);
    }
}
