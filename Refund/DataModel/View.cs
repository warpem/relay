using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;
using Refund.Utils;
using Serilog;

namespace Refund.DataModel;

/// <summary>
/// Represents a specific visualization of jobs in a space.
/// Views define which jobs are visible and how they are organized visually,
/// with support for hierarchical folder organization.
/// Multiple views can exist for the same space, allowing different perspectives on the same workflow.
/// </summary>
public class View : RelayBase
{
    private static readonly ConditionalWeakTable<View, ReadOnlyView> ReadOnlyCache = new();

    public Space Space = null;

    [RelayProperty]
    public int Id { get; set; } = -1;

    [RelayProperty]
    public string Alias { get; set; } = "";

    public string QualifiedName => $"V{Id}: {Alias}";

    [RelayProperty]
    public DateTime CreationDate { get; set; }

    public User CreatedBy { get; set; }

    [RelayProperty]
    public DateTime UpdateDate { get; set; }

    public User UpdatedBy { get; set; }

    [RelayProperty]
    public string HeroImage { get; set; } = string.Empty;

    [RelayProperty]
    public string Notes { get; set; }

    public DiagramLayout? DiagramLayout { get; set; }

    /// <summary>
    /// Flat list of ALL jobs in this view (regardless of folder placement).
    /// </summary>
    private readonly List<Job> _Jobs = new();

    public ReadOnlyCollection<Job> Jobs => _Jobs.AsReadOnly();

    /// <summary>
    /// Flat list of ALL folders in this view.
    /// </summary>
    private readonly List<Folder> _Folders = new();

    public ReadOnlyCollection<Folder> Folders => _Folders.AsReadOnly();

    /// <summary>
    /// Flat list of ALL factory instances in this view (regardless of folder placement).
    /// </summary>
    private readonly List<FactoryInstance> _FactoryInstances = new();

    public ReadOnlyCollection<FactoryInstance> FactoryInstances => _FactoryInstances.AsReadOnly();

    /// <summary>
    /// Ordered root-level items (jobs and folders not inside any folder).
    /// </summary>
    private readonly List<IFolderContent> _RootItems = new();

    public ReadOnlyCollection<IFolderContent> RootItems => _RootItems.AsReadOnly();

    public View()
    {
    }

    public ReadOnlyView AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, view => new ReadOnlyView(view));
    }

    #region Job management

    /// <summary>
    /// Adds a job to this view, optionally placing it in a folder.
    /// </summary>
    public void AddJob(Job job, Folder targetFolder = null)
    {
        _Jobs.Add(job);

        if (targetFolder != null)
            targetFolder.AddItem(job);
        else
            _RootItems.Add(job);
    }

    /// <summary>
    /// Removes a job from this view and from whichever folder contains it.
    /// </summary>
    public void RemoveJob(Job job)
    {
        _Jobs.Remove(job);

        if (!_RootItems.Remove(job))
        {
            foreach (var folder in _Folders)
                if (folder.RemoveItemRecursive(job))
                    break;
        }
    }

    /// <summary>
    /// Removes a job from RootItems only (keeps in _Jobs list).
    /// Used for factory sub-jobs that should be in the view but not visible at root level.
    /// </summary>
    public void RemoveJobFromRootItems(Job job)
    {
        _RootItems.Remove(job);
    }

    /// <summary>
    /// Moves a job to a new position in the root items list.
    /// </summary>
    public void MoveJob(Job job, int newIndex)
    {
        int currentIndex = _RootItems.IndexOf(job);
        if (currentIndex < 0)
            throw new InvalidOperationException($"Job {job.Id} is not at root level in this view");

        _RootItems.RemoveAt(currentIndex);
        newIndex = Math.Clamp(newIndex, 0, _RootItems.Count);
        _RootItems.Insert(newIndex, job);
    }

    /// <summary>
    /// Moves any root-level item (job or folder) to a new position in the root items list.
    /// </summary>
    public void MoveItem(IFolderContent item, int newIndex)
    {
        int currentIndex = _RootItems.IndexOf(item);
        if (currentIndex < 0)
            throw new InvalidOperationException("Item is not at root level in this view");

        _RootItems.RemoveAt(currentIndex);
        newIndex = Math.Clamp(newIndex, 0, _RootItems.Count);
        _RootItems.Insert(newIndex, item);
    }

    public Job FindJob(int id)
    {
        return _Jobs.FirstOrDefault(j => j.Id == id);
    }

    #endregion

    #region Folder management

    /// <summary>
    /// Adds a folder to this view.
    /// </summary>
    public void AddFolder(Folder folder, Folder parentFolder = null)
    {
        folder.View = this;
        _Folders.Add(folder);

        if (parentFolder != null)
            parentFolder.AddItem(folder);
        else
            _RootItems.Add(folder);
    }

    /// <summary>
    /// Removes a folder from this view, moving its contents to the parent level (ungroup).
    /// </summary>
    public void RemoveFolder(Folder folder)
    {
        // Move folder's children to the parent level
        var parent = folder.ParentFolder;
        var targetList = parent != null ? null : _RootItems;

        int insertIndex;
        if (parent != null)
        {
            // Find folder's index in parent's items to insert children there
            var parentItems = parent.Items.ToList();
            insertIndex = parentItems.IndexOf(folder);
            if (insertIndex < 0) insertIndex = parentItems.Count;
        }
        else
        {
            insertIndex = _RootItems.IndexOf(folder);
            if (insertIndex < 0) insertIndex = _RootItems.Count;
        }

        // Move children to parent level at the folder's position
        foreach (var item in folder.Items.ToList())
        {
            folder.RemoveItem(item);
            if (parent != null)
            {
                parent.InsertItem(insertIndex, item);
            }
            else
            {
                _RootItems.Insert(insertIndex, item);
                if (item is Folder childFolder)
                    childFolder.ParentFolder = null;
            }
            insertIndex++;
        }

        // Remove the folder itself
        if (parent != null)
            parent.RemoveItem(folder);
        else
            _RootItems.Remove(folder);

        _Folders.Remove(folder);

        // Also remove any nested subfolders from the flat list
        foreach (var sub in folder.GetAllFoldersRecursive().ToList())
            _Folders.Remove(sub);
    }

    public Folder FindFolder(int id)
    {
        return _Folders.FirstOrDefault(f => f.Id == id);
    }

    /// <summary>
    /// Finds which folder contains the given job, or null if it's at root level.
    /// </summary>
    public Folder FindFolderContainingJob(int jobId)
    {
        foreach (var folder in _Folders)
            if (folder.Items.OfType<Job>().Any(j => j.Id == jobId))
                return folder;
        return null;
    }

    public int GetNextFolderId()
    {
        return _Folders.Select(f => f.Id).DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>
    /// Moves a job to a target folder (or root level if targetFolder is null).
    /// </summary>
    public void MoveJobToFolder(Job job, Folder targetFolder)
    {
        // Remove from current location
        if (!_RootItems.Remove(job))
        {
            foreach (var folder in _Folders)
                if (folder.RemoveItemRecursive(job))
                    break;
        }

        // Add to target
        if (targetFolder != null)
            targetFolder.AddItem(job);
        else
            _RootItems.Add(job);
    }

    /// <summary>
    /// Moves a folder to a new parent folder (or root level if targetFolder is null).
    /// </summary>
    public void MoveFolderToFolder(Folder folder, Folder targetFolder)
    {
        // Prevent moving a folder into itself or its descendants
        if (targetFolder != null)
        {
            var check = targetFolder;
            while (check != null)
            {
                if (check == folder)
                    throw new InvalidOperationException("Cannot move a folder into itself or its descendant");
                check = check.ParentFolder;
            }
        }

        // Remove from current location
        if (folder.ParentFolder != null)
            folder.ParentFolder.RemoveItem(folder);
        else
            _RootItems.Remove(folder);

        // Add to target
        if (targetFolder != null)
        {
            targetFolder.AddItem(folder);
        }
        else
        {
            folder.ParentFolder = null;
            _RootItems.Add(folder);
        }
    }

    #endregion

    #region Factory Instance management

    public void AddFactoryInstance(FactoryInstance instance, Folder targetFolder = null)
    {
        _FactoryInstances.Add(instance);

        if (targetFolder != null)
            targetFolder.AddItem(instance);
        else
            _RootItems.Add(instance);
    }

    public void RemoveFactoryInstance(FactoryInstance instance)
    {
        _FactoryInstances.Remove(instance);

        if (!_RootItems.Remove(instance))
        {
            foreach (var folder in _Folders)
                if (folder.RemoveItemRecursive(instance))
                    break;
        }
    }

    public FactoryInstance FindFactoryInstance(int id)
    {
        return _FactoryInstances.FirstOrDefault(fi => fi.Id == id);
    }

    /// <summary>
    /// Moves a factory instance to a target folder (or root level if targetFolder is null).
    /// </summary>
    public void MoveFactoryInstanceToFolder(FactoryInstance instance, Folder targetFolder)
    {
        // Remove from current location
        if (!_RootItems.Remove(instance))
        {
            foreach (var folder in _Folders)
                if (folder.RemoveItemRecursive(instance))
                    break;
        }

        // Add to target
        if (targetFolder != null)
            targetFolder.AddItem(instance);
        else
            _RootItems.Add(instance);
    }

    #endregion

    public void UpdateDiagramLayout(Space space)
    {
        DiagramLayout = DiagramLayoutComputer.ComputeLayout(this, space, DiagramLayout);
    }

    public void ResetDiagramLayout(Space space)
    {
        DiagramLayout = DiagramLayoutComputer.ComputeLayout(this, space, null);
    }

    #region Serialization

    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);

        writer["CreatedBy"] = CreatedBy?.Id;
        writer["UpdatedBy"] = UpdatedBy?.Id;

        // Keep JobIds for backward compatibility
        writer["JobIds"] = new JsonArray(Jobs.Select(j => JsonValue.Create(j.Id)).ToArray<JsonNode>());

        // Write root-level items as typed references
        var rootItemsArray = new JsonArray();
        foreach (var item in _RootItems)
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
            rootItemsArray.Add(itemNode);
        }
        writer["Items"] = rootItemsArray;

        // Write all folders (flat, each with own Items array)
        var foldersArray = new JsonArray();
        foreach (var folder in _Folders)
        {
            var folderNode = new JsonObject();
            folder.WriteToJson(folderNode);
            foldersArray.Add(folderNode);
        }
        writer["Folders"] = foldersArray;

        if (DiagramLayout != null)
        {
            var layoutNode = new JsonObject
            {
                ["GraphWidth"] = DiagramLayout.GraphWidth,
                ["GraphHeight"] = DiagramLayout.GraphHeight,
                ["ConnectivityHash"] = DiagramLayout.ConnectivityHash
            };

            var nodesArray = new JsonArray();
            foreach (var node in DiagramLayout.Nodes)
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

                edgesArray.Add(edgeNode);
            }
            layoutNode["Edges"] = edgesArray;

            writer["DiagramLayout"] = layoutNode;
        }
    }

    public void ReadFromJson(JsonNode reader, Job[] jobs, Edge[] edges, ReadOnlyCollection<User> users, Dictionary<int, FactoryInstance> factoryInstances = null)
    {
        base.ReadFromJson(reader);

        if (reader["CreatedBy"] != null)
            CreatedBy = users.FirstOrDefault(u => u.Id == reader["CreatedBy"].Deserialize<int>());

        if (CreatedBy == null)
            CreatedBy = Space?.Project?.Owner;
        if (CreatedBy == null)
            CreatedBy = Space?.Project?.Members.FirstOrDefault();
        if (CreatedBy == null)
            CreatedBy = users.FirstOrDefault();

        if (reader["UpdatedBy"] != null)
            UpdatedBy = users.FirstOrDefault(u => u.Id == reader["UpdatedBy"].Deserialize<int>());

        if (UpdatedBy == null)
            UpdatedBy = Space?.Project?.Owner;
        if (UpdatedBy == null)
            UpdatedBy = Space?.Project?.Members.FirstOrDefault();
        if (UpdatedBy == null)
            UpdatedBy = users.FirstOrDefault();

        _Jobs.Clear();
        _Folders.Clear();
        _FactoryInstances.Clear();
        _RootItems.Clear();

        // Always read the flat job list from JobIds
        if (reader["JobIds"] != null)
        {
            int[] jobIDs = reader["JobIds"].Deserialize<int[]>();
            foreach (var id in jobIDs)
                if (id < jobs.Length && jobs[id] != null)
                    _Jobs.Add(jobs[id]);
                else
                    Log.ForContext<View>().Warning("Couldn't find job with ID {JobId} in view {ViewId}", id, Id);
        }

        // Check for new format (Items + Folders)
        if (reader["Items"] != null)
        {
            // Deserialize folders first
            var foldersLookup = new Dictionary<int, Folder>();
            var folderNodes = new Dictionary<int, JsonNode>();

            if (reader["Folders"] != null)
            {
                foreach (var folderNode in reader["Folders"].AsArray())
                {
                    var folder = new Folder { View = this };
                    folder.ReadFromJson(folderNode, users);
                    _Folders.Add(folder);
                    foldersLookup[folder.Id] = folder;
                    folderNodes[folder.Id] = folderNode;
                }
            }

            // Resolve folder item references
            foreach (var kvp in folderNodes)
                foldersLookup[kvp.Key].ResolveItems(kvp.Value, jobs, foldersLookup, factoryInstances);

            // Collect factory instances from folders into _FactoryInstances
            foreach (var folder in _Folders)
                foreach (var item in folder.Items)
                    if (item is FactoryInstance folderFi && !_FactoryInstances.Contains(folderFi))
                        _FactoryInstances.Add(folderFi);

            // Resolve root items
            foreach (var itemNode in reader["Items"].AsArray())
            {
                var type = itemNode["Type"]?.GetValue<string>();
                var id = itemNode["Id"]?.GetValue<int>() ?? -1;

                if (type == "Job" && id >= 0 && id < jobs.Length && jobs[id] != null)
                {
                    _RootItems.Add(jobs[id]);
                }
                else if (type == "Folder" && foldersLookup.TryGetValue(id, out var folder))
                {
                    _RootItems.Add(folder);
                }
                else if (type == "FactoryInstance" && factoryInstances != null && factoryInstances.TryGetValue(id, out var fi))
                {
                    if (!_FactoryInstances.Contains(fi))
                        _FactoryInstances.Add(fi);
                    _RootItems.Add(fi);
                }
            }
        }
        else
        {
            // Old format: all jobs are root-level items
            foreach (var job in _Jobs)
                _RootItems.Add(job);
        }

        // Deserialize DiagramLayout
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

        // Silently ignore legacy layout keys (MainLayout, WorkbenchLayout)
    }

    public void ReadFromJson(JsonNode reader, ReadOnlyCollection<User> users)
    {
        if (Space == null)
            throw new Exception("Space can't be null because it's needed to resolve Job IDs");

        Job[] jobsLookup = new Job[Space.Jobs.Select(j => j.Id).DefaultIfEmpty(-1).Max() + 1];
        foreach (var job in Space.Jobs)
            jobsLookup[job.Id] = job;

        Edge[] edgesLookup = new Edge[Space.Edges.Select(e => e.Id).DefaultIfEmpty(-1).Max() + 1];
        foreach (var edge in Space.Edges)
            edgesLookup[edge.Id] = edge;

        var factoryInstancesLookup = new Dictionary<int, FactoryInstance>();
        foreach (var fi in Space.FactoryInstances)
            factoryInstancesLookup[fi.Id] = fi;

        ReadFromJson(reader, jobsLookup, edgesLookup, users, factoryInstancesLookup);
    }

    public static View CreateFromJson(JsonNode reader, Job[] jobs, Edge[] edges, Space space, ReadOnlyCollection<User> users, Dictionary<int, FactoryInstance> factoryInstances = null)
    {
        View result = new View();
        result.Space = space;
        result.ReadFromJson(reader, jobs, edges, users, factoryInstances);

        return result;
    }

    public View Clone()
    {
        View clone = new View();
        clone.AdoptState(this);

        clone.Space = Space;

        return clone;
    }

    #endregion
}
