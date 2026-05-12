using System.Text.Json.Nodes;

namespace Refund.DataModel;

/// <summary>
/// Maps an exposed property on the factory editor to a sub-job parameter inside the definition.
/// </summary>
public class ExposedProperty
{
    /// <summary>
    /// User-editable display name, pre-filled with original property label.
    /// </summary>
    public string CustomName { get; set; } = "";

    /// <summary>
    /// Blueprint-local sub-job ID owning the property.
    /// </summary>
    public int SubJobId { get; set; }

    /// <summary>
    /// Property name on the sub-job class.
    /// </summary>
    public string PropertyName { get; set; } = "";

    public JsonNode ToJson() => new JsonObject
    {
        ["CustomName"] = CustomName,
        ["SubJobId"] = SubJobId,
        ["PropertyName"] = PropertyName
    };

    public static ExposedProperty FromJson(JsonNode node) => new()
    {
        CustomName = node["CustomName"]?.GetValue<string>() ?? "",
        SubJobId = node["SubJobId"]?.GetValue<int>() ?? 0,
        PropertyName = node["PropertyName"]?.GetValue<string>() ?? ""
    };
}
