using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;
using Refund.Utils;

namespace Refund.DataModel;

/// <summary>
/// Represents a folder in a view that can contain jobs and other folders.
/// Folders provide hierarchical organization of items within a view.
/// </summary>
public class Folder : RelayBase, IFolderContent
{
    private static readonly ConditionalWeakTable<Folder, ReadOnlyFolder> ReadOnlyCache = new();

    [RelayProperty]
    public int Id { get; set; } = -1;

    [RelayProperty]
    public string Alias { get; set; } = "";

    public string QualifiedName => $"F{Id}: {Alias}";

    [RelayProperty]
    public string ColorTag { get; set; } = "";

    [RelayProperty]
    public string Notes { get; set; } = "";

    [RelayProperty]
    public string HeroImage { get; set; } = "";

    [RelayProperty]
    public DateTime CreationDate { get; set; }

    [RelayProperty]
    public DateTime UpdateDate { get; set; }

    public User CreatedBy { get; set; }
    public User UpdatedBy { get; set; }

    /// <summary>
    /// The view that owns this folder.
    /// </summary>
    public View View { get; set; }

    /// <summary>
    /// The parent folder, or null if this folder is at the root level.
    /// Set during deserialization and mutations; not serialized.
    /// </summary>
    public Folder ParentFolder { get; set; }

    private readonly List<IFolderContent> _Items = new();

    /// <summary>
    /// Ordered list of children — mix of Job and Folder references.
    /// </summary>
    public ReadOnlyCollection<IFolderContent> Items => _Items.AsReadOnly();

    public FolderLayout? Layout { get; set; }

    public DiagramLayout? DiagramLayout { get; set; }

    public void UpdateLayout(Space space)
    {
        Layout = FolderLayoutComputer.ComputeLayout(this, space, Layout);
    }

    public void UpdateDiagramLayout(Space space)
    {
        DiagramLayout = DiagramLayoutComputer.ComputeLayout(this, space, DiagramLayout);
    }

    public void ResetDiagramLayout(Space space)
    {
        DiagramLayout = DiagramLayoutComputer.ComputeLayout(this, space, null);
    }

    public ReadOnlyFolder AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, f => new ReadOnlyFolder(f));
    }

    public void AddItem(IFolderContent item)
    {
        _Items.Add(item);
        if (item is Folder child)
            child.ParentFolder = this;
    }

    public void RemoveItem(IFolderContent item)
    {
        _Items.Remove(item);
        if (item is Folder child && child.ParentFolder == this)
            child.ParentFolder = null;
    }

    public void InsertItem(int index, IFolderContent item)
    {
        index = Math.Clamp(index, 0, _Items.Count);
        _Items.Insert(index, item);
        if (item is Folder child)
            child.ParentFolder = this;
    }

    /// <summary>
    /// Moves an item within this folder to a new position.
    /// </summary>
    public void MoveItem(IFolderContent item, int newIndex)
    {
        int currentIndex = _Items.IndexOf(item);
        if (currentIndex < 0)
            throw new InvalidOperationException("Item is not in this folder");

        _Items.RemoveAt(currentIndex);
        newIndex = Math.Clamp(newIndex, 0, _Items.Count);
        _Items.Insert(newIndex, item);
    }

    /// <summary>
    /// Removes an item from this folder or any nested subfolder, recursively.
    /// </summary>
    public bool RemoveItemRecursive(IFolderContent item)
    {
        if (_Items.Remove(item))
        {
            if (item is Folder child && child.ParentFolder == this)
                child.ParentFolder = null;
            return true;
        }

        foreach (var child in _Items.OfType<Folder>())
            if (child.RemoveItemRecursive(item))
                return true;

        return false;
    }

    /// <summary>
    /// Returns all jobs contained in this folder and all subfolders, recursively.
    /// </summary>
    public IEnumerable<Job> GetAllJobsRecursive()
    {
        foreach (var item in _Items)
        {
            if (item is Job job)
                yield return job;
            else if (item is Folder subfolder)
                foreach (var subJob in subfolder.GetAllJobsRecursive())
                    yield return subJob;
        }
    }

    /// <summary>
    /// Returns all folders contained in this folder, recursively (including this folder's direct children).
    /// </summary>
    public IEnumerable<Folder> GetAllFoldersRecursive()
    {
        foreach (var item in _Items.OfType<Folder>())
        {
            yield return item;
            foreach (var sub in item.GetAllFoldersRecursive())
                yield return sub;
        }
    }

    #region Serialization

    /// <summary>
    /// Writes folder properties and its Items array to JSON.
    /// Items are written as typed references: [{"Type":"Job","Id":N}, {"Type":"Folder","Id":N}]
    /// </summary>
    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);

        writer["CreatedBy"] = CreatedBy?.Id;
        writer["UpdatedBy"] = UpdatedBy?.Id;

        var itemsArray = new JsonArray();
        foreach (var item in _Items)
        {
            var itemNode = new JsonObject();
            if (item is Job job)
            {
                itemNode["Type"] = "Job";
                itemNode["Id"] = job.Id;
            }
            else if (item is Folder folder)
            {
                itemNode["Type"] = "Folder";
                itemNode["Id"] = folder.Id;
            }
            else if (item is FactoryInstance fi)
            {
                itemNode["Type"] = "FactoryInstance";
                itemNode["Id"] = fi.Id;
            }
            itemsArray.Add(itemNode);
        }
        writer["Items"] = itemsArray;

        if (Layout != null)
        {
            var layoutNode = new JsonObject
            {
                ["GraphWidth"] = Layout.GraphWidth,
                ["GraphHeight"] = Layout.GraphHeight,
                ["ConnectivityHash"] = Layout.ConnectivityHash
            };

            var nodesArray = new JsonArray();
            foreach (var node in Layout.Nodes)
            {
                nodesArray.Add(new JsonObject
                {
                    ["ItemId"] = node.ItemId,
                    ["IsFolder"] = node.IsFolder,
                    ["X"] = node.X,
                    ["Y"] = node.Y,
                    ["Width"] = node.Width,
                    ["Height"] = node.Height
                });
            }
            layoutNode["Nodes"] = nodesArray;

            var edgesArray = new JsonArray();
            foreach (var edge in Layout.Edges)
            {
                var edgeNode = new JsonObject
                {
                    ["SourceX"] = edge.SourceX,
                    ["SourceY"] = edge.SourceY,
                    ["TargetX"] = edge.TargetX,
                    ["TargetY"] = edge.TargetY
                };

                if (edge.BendPoints is { Count: > 0 })
                {
                    var bpArray = new JsonArray();
                    foreach (var bp in edge.BendPoints)
                    {
                        bpArray.Add(new JsonObject
                        {
                            ["X"] = bp.X,
                            ["Y"] = bp.Y
                        });
                    }
                    edgeNode["BendPoints"] = bpArray;
                }

                edgesArray.Add(edgeNode);
            }
            layoutNode["Edges"] = edgesArray;

            writer["Layout"] = layoutNode;
        }

        if (DiagramLayout != null)
        {
            var diagramLayoutNode = new JsonObject
            {
                ["GraphWidth"] = DiagramLayout.GraphWidth,
                ["GraphHeight"] = DiagramLayout.GraphHeight,
                ["ConnectivityHash"] = DiagramLayout.ConnectivityHash
            };

            var diagramNodesArray = new JsonArray();
            foreach (var node in DiagramLayout.Nodes)
            {
                diagramNodesArray.Add(new JsonObject
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
            diagramLayoutNode["Nodes"] = diagramNodesArray;

            var diagramEdgesArray = new JsonArray();
            foreach (var edge in DiagramLayout.Edges)
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
                    {
                        bpArray.Add(new JsonObject
                        {
                            ["X"] = bp.X,
                            ["Y"] = bp.Y
                        });
                    }
                    edgeNode["BendPoints"] = bpArray;
                }

                diagramEdgesArray.Add(edgeNode);
            }
            diagramLayoutNode["Edges"] = diagramEdgesArray;

            writer["DiagramLayout"] = diagramLayoutNode;
        }
    }

    /// <summary>
    /// Reads folder properties from JSON. Item references are resolved externally by View.
    /// </summary>
    public void ReadFromJson(JsonNode reader, ReadOnlyCollection<User> users)
    {
        base.ReadFromJson(reader);

        if (reader["CreatedBy"] != null)
            CreatedBy = users.FirstOrDefault(u => u.Id == reader["CreatedBy"].Deserialize<int>());
        if (reader["UpdatedBy"] != null)
            UpdatedBy = users.FirstOrDefault(u => u.Id == reader["UpdatedBy"].Deserialize<int>());

        if (reader["Layout"] is JsonObject layoutJson)
        {
            var layout = new FolderLayout
            {
                GraphWidth = layoutJson["GraphWidth"]?.GetValue<double>() ?? 0,
                GraphHeight = layoutJson["GraphHeight"]?.GetValue<double>() ?? 0,
                ConnectivityHash = layoutJson["ConnectivityHash"]?.GetValue<string>() ?? ""
            };

            if (layoutJson["Nodes"] is JsonArray nodesJson)
            {
                foreach (var nj in nodesJson)
                {
                    layout.Nodes.Add(new FolderLayoutNode
                    {
                        ItemId = nj["ItemId"]?.GetValue<int>() ?? 0,
                        IsFolder = nj["IsFolder"]?.GetValue<bool>() ?? false,
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
                    {
                        foreach (var bp in bpJson)
                            bendPoints.Add((bp["X"]?.GetValue<double>() ?? 0, bp["Y"]?.GetValue<double>() ?? 0));
                    }

                    layout.Edges.Add(new FolderLayoutEdge
                    {
                        SourceX = ej["SourceX"]?.GetValue<double>() ?? 0,
                        SourceY = ej["SourceY"]?.GetValue<double>() ?? 0,
                        TargetX = ej["TargetX"]?.GetValue<double>() ?? 0,
                        TargetY = ej["TargetY"]?.GetValue<double>() ?? 0,
                        BendPoints = bendPoints
                    });
                }
            }

            Layout = layout;
        }

        if (reader["DiagramLayout"] is JsonObject diagramLayoutJson)
        {
            var diagramLayout = new DiagramLayout
            {
                GraphWidth = diagramLayoutJson["GraphWidth"]?.GetValue<double>() ?? 0,
                GraphHeight = diagramLayoutJson["GraphHeight"]?.GetValue<double>() ?? 0,
                ConnectivityHash = diagramLayoutJson["ConnectivityHash"]?.GetValue<string>() ?? ""
            };

            if (diagramLayoutJson["Nodes"] is JsonArray diagramNodesJson)
            {
                foreach (var nj in diagramNodesJson)
                {
                    diagramLayout.Nodes.Add(new DiagramLayoutNode
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

            if (diagramLayoutJson["Edges"] is JsonArray diagramEdgesJson)
            {
                foreach (var ej in diagramEdgesJson)
                {
                    var bendPoints = new List<(double X, double Y)>();
                    if (ej["BendPoints"] is JsonArray bpJson)
                    {
                        foreach (var bp in bpJson)
                            bendPoints.Add((bp["X"]?.GetValue<double>() ?? 0, bp["Y"]?.GetValue<double>() ?? 0));
                    }

                    diagramLayout.Edges.Add(new DiagramLayoutEdge
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

            DiagramLayout = diagramLayout;
        }
    }

    /// <summary>
    /// Resolves item references from the Items JSON array.
    /// Called after all folders and jobs are loaded so references can be resolved.
    /// </summary>
    public void ResolveItems(JsonNode reader, Job[] jobsLookup, Dictionary<int, Folder> foldersLookup, Dictionary<int, FactoryInstance> factoryInstancesLookup = null)
    {
        _Items.Clear();

        if (reader["Items"] == null)
            return;

        foreach (var itemNode in reader["Items"].AsArray())
        {
            var type = itemNode["Type"]?.GetValue<string>();
            var id = itemNode["Id"]?.GetValue<int>() ?? -1;

            if (type == "Job" && id >= 0 && id < jobsLookup.Length && jobsLookup[id] != null)
            {
                _Items.Add(jobsLookup[id]);
            }
            else if (type == "Folder" && foldersLookup.TryGetValue(id, out var folder))
            {
                _Items.Add(folder);
                folder.ParentFolder = this;
            }
            else if (type == "FactoryInstance" && factoryInstancesLookup != null && factoryInstancesLookup.TryGetValue(id, out var fi))
            {
                _Items.Add(fi);
            }
        }
    }

    #endregion
}
