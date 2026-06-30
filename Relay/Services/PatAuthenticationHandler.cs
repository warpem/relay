using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refund.Services;
using Refund.Services.Core.DataManager;

namespace Relay.Services;

/// <summary>
/// Authenticates requests bearing a Relay personal access token
/// (<c>Authorization: Bearer relay_pat_...</c>) and resolves them to a Relay user.
/// Returns NoResult for any non-PAT request so it never interferes with cookie auth.
/// </summary>
public class PatAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Pat";
    private const string Prefix = "Bearer relay_pat_";

    private readonly PersonalAccessTokenService _pats;
    private readonly DataManager _dataManager;

    public PatAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PersonalAccessTokenService pats,
        DataManager dataManager) : base(options, logger, encoder)
    {
        _pats = pats;
        _dataManager = dataManager;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith(Prefix, StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());

        var raw = header["Bearer ".Length..].Trim();
        var pat = _pats.Validate(raw);
        if (pat == null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired personal access token"));

        var user = _dataManager.FindUser(pat.OwnerUserId);
        if (user == null)
            return Task.FromResult(AuthenticateResult.Fail("Token owner no longer exists"));

        Context.Items["PatGrants"] = Refund.Mcp.PatAuthorization.From(pat);

        var claims = new[]
        {
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
