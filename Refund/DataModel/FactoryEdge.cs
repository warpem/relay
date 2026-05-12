using System.Text.Json.Nodes;

namespace Refund.DataModel;

/// <summary>
/// An internal edge within a factory definition, connecting two sub-job ports.
/// Uses "subJobId.portName" string format for source and target.
/// </summary>
public readonly record struct FactoryEdge(string Source, string Target)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["Source"] = Source,
        ["Target"] = Target
    };

    public static FactoryEdge FromJson(JsonNode node) => new(
        node["Source"]?.GetValue<string>() ?? "",
        node["Target"]?.GetValue<string>() ?? ""
    );
}

/// <summary>
/// A fixed external edge in a factory definition, connecting a sub-job port
/// to an existing job's port outside the factory.
/// </summary>
public readonly record struct FactoryExternalEdge(
    int SubJobId,
    string SubJobPort,
    int ExternalJobId,
    string ExternalPort)
{
    public JsonNode ToJson() => new JsonObject
    {
        ["SubJobId"] = SubJobId,
        ["SubJobPort"] = SubJobPort,
        ["ExternalJobId"] = ExternalJobId,
        ["ExternalPort"] = ExternalPort
    };

    public static FactoryExternalEdge FromJson(JsonNode node) => new(
        node["SubJobId"]?.GetValue<int>() ?? 0,
        node["SubJobPort"]?.GetValue<string>() ?? "",
        node["ExternalJobId"]?.GetValue<int>() ?? 0,
        node["ExternalPort"]?.GetValue<string>() ?? ""
    );
}
