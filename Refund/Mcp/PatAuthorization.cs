using Refund.DataModel;

namespace Refund.Mcp;

/// <summary>The three per-tier access levels a token carries, decoupled from HTTP/DataManager.</summary>
public readonly record struct PatGrants(AccessLevel Project, AccessLevel Space, AccessLevel Job);

public enum PermTier { Project, Space, Job }

/// <summary>
/// Pure permission checks for MCP tools. <see cref="Allows"/> returns true iff the token's level
/// for <paramref name="tier"/> is at least <paramref name="required"/>. AccessLevel is ordered.
/// </summary>
public static class PatAuthorization
{
    public static PatGrants From(PersonalAccessToken pat) =>
        new(pat.ProjectAccess, pat.SpaceAccess, pat.JobAccess);

    public static bool Allows(PatGrants grants, PermTier tier, AccessLevel required)
    {
        var held = tier switch
        {
            PermTier.Project => grants.Project,
            PermTier.Space => grants.Space,
            PermTier.Job => grants.Job,
            _ => AccessLevel.None
        };
        return held >= required;
    }
}
