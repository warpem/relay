using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;

namespace Refund.DataModel;

/// <summary>
/// A space-level blueprint that can be instantiated as factory instances.
/// Contains sub-job templates, internal/external edges, and exposure mappings.
/// </summary>
public class FactoryDefinition : RelayBase
{
    private static readonly ConditionalWeakTable<FactoryDefinition, ReadOnlyFactoryDefinition> ReadOnlyCache = new();

    [RelayProperty]
    public int Id { get; set; } = -1;

    [RelayProperty]
    public string Alias { get; set; } = "";

    public string QualifiedName => $"FD{Id}: {Alias}";

    /// <summary>
    /// Sub-job blueprints serialized as regular jobs but NOT added to Space._Jobs.
    /// Blueprint IDs are local to the definition (1, 2, 3...).
    /// </summary>
    public List<Job> SubJobs { get; set; } = new();

    public List<FactoryEdge> InternalEdges { get; set; } = new();

    public List<FactoryExternalEdge> ExternalEdges { get; set; } = new();

    public List<ExposedPort> ExposedPortsIn { get; set; } = new();

    public List<ExposedPort> ExposedPortsOut { get; set; } = new();

    public List<ExposedProperty> ExposedProperties { get; set; } = new();

    /// <summary>
    /// Optional per-sub-job queue assignments. Key = blueprint sub-job ID, Value = queue ID (null = unassigned).
    /// </summary>
    public Dictionary<int, int?> QueueAssignments { get; set; } = new();

    public DiagramLayout? DiagramLayout { get; set; }

    /// <summary>
    /// Compact layout for card minimap rendering (small nodes, tight spacing).
    /// Not serialized — recomputed alongside DiagramLayout.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public FolderLayout? CardLayout { get; set; }

    public ReadOnlyFactoryDefinition AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, d => new ReadOnlyFactoryDefinition(d));
    }

    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);

        // SubJobs — polymorphic serialization (same as Space serializes jobs)
        writer["SubJobs"] = new JsonArray(SubJobs.Select(j =>
        {
            JsonNode jobWriter = new JsonObject();
            Job.WritePolymorphicJson(jobWriter, j);
            return jobWriter;
        }).ToArray());

        writer["InternalEdges"] = new JsonArray(InternalEdges.Select(e => e.ToJson()).ToArray());
        writer["ExternalEdges"] = new JsonArray(ExternalEdges.Select(e => e.ToJson()).ToArray());
        writer["ExposedPortsIn"] = new JsonArray(ExposedPortsIn.Select(p => p.ToJson()).ToArray());
        writer["ExposedPortsOut"] = new JsonArray(ExposedPortsOut.Select(p => p.ToJson()).ToArray());
        writer["ExposedProperties"] = new JsonArray(ExposedProperties.Select(p => p.ToJson()).ToArray());

        var qaNode = new JsonObject();
        foreach (var kvp in QueueAssignments)
        {
            qaNode[kvp.Key.ToString()] = kvp.Value.HasValue ? JsonValue.Create(kvp.Value.Value) : null;
        }
        writer["QueueAssignments"] = qaNode;

        if (DiagramLayout != null)
            writer["DiagramLayout"] = SerializeDiagramLayout(DiagramLayout);
    }

    /// <summary>
    /// Deserializes simple properties and collections that don't require Space/User context.
    /// Call this overload when sub-job blueprints are not needed (e.g., standalone tests).
    /// </summary>
    public override void ReadFromJson(JsonNode reader)
    {
        base.ReadFromJson(reader);
        ReadCollectionsFromJson(reader);
    }

    /// <summary>
    /// Deserializes everything including sub-job blueprints, which require Space and Users.
    /// </summary>
    public void ReadFromJson(JsonNode reader, Space space, ReadOnlyCollection<User> users)
    {
        base.ReadFromJson(reader);

        SubJobs.Clear();
        if (reader["SubJobs"] != null)
        {
            foreach (var sjNode in reader["SubJobs"].AsArray())
            {
                var blueprint = Job.CreateFromPolymorphicJson(sjNode, space, users);
                SubJobs.Add(blueprint);
            }
        }

        ReadCollectionsFromJson(reader);
    }

    private void ReadCollectionsFromJson(JsonNode reader)
    {
        InternalEdges.Clear();
        if (reader["InternalEdges"] != null)
            InternalEdges.AddRange(reader["InternalEdges"].AsArray().Select(FactoryEdge.FromJson));

        ExternalEdges.Clear();
        if (reader["ExternalEdges"] != null)
            ExternalEdges.AddRange(reader["ExternalEdges"].AsArray().Select(FactoryExternalEdge.FromJson));

        ExposedPortsIn.Clear();
        if (reader["ExposedPortsIn"] != null)
            ExposedPortsIn.AddRange(reader["ExposedPortsIn"].AsArray().Select(ExposedPort.FromJson));

        ExposedPortsOut.Clear();
        if (reader["ExposedPortsOut"] != null)
            ExposedPortsOut.AddRange(reader["ExposedPortsOut"].AsArray().Select(ExposedPort.FromJson));

        ExposedProperties.Clear();
        if (reader["ExposedProperties"] != null)
            ExposedProperties.AddRange(reader["ExposedProperties"].AsArray().Select(ExposedProperty.FromJson));

        QueueAssignments.Clear();
        if (reader["QueueAssignments"] is JsonObject qaObj)
        {
            foreach (var kvp in qaObj)
            {
                if (int.TryParse(kvp.Key, out int subJobId))
                {
                    // kvp.Value is null for JSON null — don't call GetValue on it
                    int? queueId = kvp.Value is JsonValue jv ? jv.GetValue<int>() : null;
                    QueueAssignments[subJobId] = queueId;
                }
            }
        }

        if (reader["DiagramLayout"] is JsonObject layoutJson)
            DiagramLayout = DeserializeDiagramLayout(layoutJson);
    }

    #region DiagramLayout serialization helpers

    internal static JsonNode SerializeDiagramLayout(DiagramLayout layout)
    {
        var layoutNode = new JsonObject
        {
            ["GraphWidth"] = layout.GraphWidth,
            ["GraphHeight"] = layout.GraphHeight,
            ["ConnectivityHash"] = layout.ConnectivityHash
        };

        var nodesArray = new JsonArray();
        foreach (var node in layout.Nodes)
        {
            nodesArray.Add(new JsonObject
            {
                ["ItemId"] = node.ItemId,
                ["IsFolder"] = node.IsFolder,
                ["IsFactoryInstance"] = node.IsFactoryInstance,
                ["X"] = node.X,
                ["Y"] = node.Y,
                ["Width"] = node.Width,
                ["Height"] = node.Height
            });
        }
        layoutNode["Nodes"] = nodesArray;

        var edgesArray = new JsonArray();
        foreach (var edge in layout.Edges)
        {
            var edgeNode = new JsonObject
            {
                ["SourceJobId"] = edge.SourceJobId,
                ["SourcePortName"] = edge.SourcePortName,
                ["TargetJobId"] = edge.TargetJobId,
                ["TargetPortName"] = edge.TargetPortName,
                ["ResourceType"] = edge.ResourceType,
                ["SourceX"] = edge.SourceX,
                ["SourceY"] = edge.SourceY,
                ["TargetX"] = edge.TargetX,
                ["TargetY"] = edge.TargetY
            };

            if (edge.BendPoints is { Count: > 0 })
            {
                var bpArray = new JsonArray();
                foreach (var bp in edge.BendPoints)
                    bpArray.Add(new JsonObject { ["X"] = bp.X, ["Y"] = bp.Y });
                edgeNode["BendPoints"] = bpArray;
            }

            edgesArray.Add(edgeNode);
        }
        layoutNode["Edges"] = edgesArray;

        return layoutNode;
    }

    internal static DiagramLayout DeserializeDiagramLayout(JsonObject layoutJson)
    {
        var layout = new DiagramLayout
        {
            GraphWidth = layoutJson["GraphWidth"]?.GetValue<double>() ?? 0,
            GraphHeight = layoutJson["GraphHeight"]?.GetValue<double>() ?? 0,
            ConnectivityHash = layoutJson["ConnectivityHash"]?.GetValue<string>() ?? ""
        };

        if (layoutJson["Nodes"] is JsonArray nodesJson)
        {
            foreach (var nj in nodesJson)
            {
                layout.Nodes.Add(new DiagramLayoutNode
                {
                    ItemId = nj["ItemId"]?.GetValue<int>() ?? 0,
                    IsFolder = nj["IsFolder"]?.GetValue<bool>() ?? false,
                    IsFactoryInstance = nj["IsFactoryInstance"]?.GetValue<bool>() ?? false,
                    X = nj["X"]?.GetValue<double>() ?? 0,
                    Y = nj["Y"]?.GetValue<double>() ?? 0,
                    Width = nj["Width"]?.GetValue<double>() ?? 0,
                    Height = nj["Height"]?.GetValue<double>() ?? 0
                });
            }
        }

        if (layoutJson["Edges"] is JsonArray edgesJson)
        {
            foreach (var ej in edgesJson)
            {
                var bendPoints = new List<(double X, double Y)>();
                if (ej["BendPoints"] is JsonArray bpJson)
                    foreach (var bp in bpJson)
                        bendPoints.Add((bp["X"]?.GetValue<double>() ?? 0, bp["Y"]?.GetValue<double>() ?? 0));

                layout.Edges.Add(new DiagramLayoutEdge
                {
                    SourceJobId = ej["SourceJobId"]?.GetValue<int>() ?? 0,
                    SourcePortName = ej["SourcePortName"]?.GetValue<string>() ?? "",
                    TargetJobId = ej["TargetJobId"]?.GetValue<int>() ?? 0,
                    TargetPortName = ej["TargetPortName"]?.GetValue<string>() ?? "",
                    ResourceType = ej["ResourceType"]?.GetValue<string>() ?? "",
                    SourceX = ej["SourceX"]?.GetValue<double>() ?? 0,
                    SourceY = ej["SourceY"]?.GetValue<double>() ?? 0,
                    TargetX = ej["TargetX"]?.GetValue<double>() ?? 0,
                    TargetY = ej["TargetY"]?.GetValue<double>() ?? 0,
                    BendPoints = bendPoints
                });
            }
        }

        return layout;
    }

    #endregion
}
