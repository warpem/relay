using System.Text.Json.Nodes;

namespace Refund.DataModel;

/// <summary>
/// Maps an exposed port on the factory card to a sub-job port inside the definition.
/// Collection membership (ExposedPortsIn vs ExposedPortsOut) implies direction.
/// </summary>
public class ExposedPort
{
    /// <summary>
    /// User-editable display name, pre-filled with original port alias.
    /// </summary>
    public string CustomName { get; set; } = "";

    /// <summary>
    /// Blueprint-local sub-job ID owning the port.
    /// </summary>
    public int SubJobId { get; set; }

    /// <summary>
    /// Port name on the sub-job.
    /// </summary>
    public string PortName { get; set; } = "";

    /// <summary>
    /// Cached resource type name for rendering without resolving the full port.
    /// Stored as string (not System.Type) for serialization simplicity — the spec uses Type
    /// but a string is more practical for JSON round-tripping and sufficient for rendering.
    /// </summary>
    public string ResourceType { get; set; } = "";

    public JsonNode ToJson() => new JsonObject
    {
        ["CustomName"] = CustomName,
        ["SubJobId"] = SubJobId,
        ["PortName"] = PortName,
        ["ResourceType"] = ResourceType
    };

    public static ExposedPort FromJson(JsonNode node) => new()
    {
        CustomName = node["CustomName"]?.GetValue<string>() ?? "",
        SubJobId = node["SubJobId"]?.GetValue<int>() ?? 0,
        PortName = node["PortName"]?.GetValue<string>() ?? "",
        ResourceType = node["ResourceType"]?.GetValue<string>() ?? ""
    };
}
