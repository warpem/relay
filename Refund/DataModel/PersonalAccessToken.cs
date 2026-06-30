namespace Refund.DataModel;

/// <summary>
/// A personal access token used to authenticate an LLM agent (over MCP) as a Relay user.
/// Only the SHA-256 hash of the raw token is ever stored; the raw value is shown once at creation.
/// </summary>
public class PersonalAccessToken : RelayBase
{
    [RelayProperty] public int Id { get; set; }

    /// <summary>SHA-256 (hex) hash of the raw token. The raw token is never persisted.</summary>
    [RelayProperty] public string TokenHash { get; set; } = "";

    /// <summary>User-supplied label, e.g. "Claude on my laptop".</summary>
    [RelayProperty] public string Name { get; set; } = "";

    /// <summary>Id of the owning <see cref="User"/>.</summary>
    [RelayProperty] public int OwnerUserId { get; set; }

    [RelayProperty] public DateTime CreationDate { get; set; } = DateTime.UtcNow;

    /// <summary><c>null</c> means the token has never been used.</summary>
    [RelayProperty] public DateTime? LastUsedDate { get; set; }

    /// <summary><c>null</c> means the token never expires.</summary>
    [RelayProperty] public DateTime? ExpirationDate { get; set; }

    public bool IsExpired => ExpirationDate.HasValue && ExpirationDate.Value <= DateTime.UtcNow;
}
