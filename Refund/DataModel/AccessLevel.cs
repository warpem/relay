namespace Refund.DataModel;

/// <summary>
/// Per-tier access a personal access token grants over MCP. Ordered: a check requiring
/// level L passes iff the token's level for that tier is >= L.
/// </summary>
public enum AccessLevel
{
    None = 0,
    Read = 1,
    EditRun = 2,
    Manage = 3
}
