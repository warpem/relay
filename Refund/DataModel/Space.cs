using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;

namespace Refund.DataModel;

/// <summary>
/// Represents a container for jobs and their connections.
/// Spaces are the primary working area for designing and executing workflows.
/// They maintain collections of jobs, edges, views, and factories, and provide methods for
/// creating and managing these objects.
/// </summary>
public class Space : RelayBase
{
    /// <summary>
    /// Cache of read-only wrappers for spaces, using weak references to avoid memory leaks.
    /// </summary>
    private static readonly ConditionalWeakTable<Space, ReadOnlySpace> ReadOnlyCache = new();
        
    /// <summary>
    /// The project that contains this space.
    /// </summary>
    public Project Project = null;

    /// <summary>
    /// Unique identifier for this space within its containing project.
    /// </summary>
    [RelayProperty]
    public int Id { get; set; } = -1;

    /// <summary>
    /// Root directory on disk where this space's data is stored.
    /// All job directories are created relative to this directory.
    /// </summary>
    [RelayProperty]
    public string RootDirectory { get; set; } = "";
    
    /// <summary>
    /// Full path to the file where this space's data is stored.
    /// </summary>
    public string FilePath => System.IO.Path.Combine(RootDirectory, "space.relay");
    
    /// <summary>
    /// Converts an absolute path to a path relative to the space's root directory.
    /// </summary>
    /// <param name="path">The absolute path to convert</param>
    /// <returns>A path relative to the space's root directory</returns>
    public string GetRelativePath(string path) => Path.GetRelativePath(Path.GetFullPath(RootDirectory), Path.GetFullPath(path));

    /// <summary>
    /// User-defined name for the space.
    /// This provides a human-readable identifier displayed in the UI.
    /// </summary>
    [RelayProperty]
    public string Alias { get; set; } = "";
    
    /// <summary>
    /// Date and time when this space was created.
    /// </summary>
    [RelayProperty]
    public DateTime CreationDate { get; set; }
    
    /// <summary>
    /// User who created this space.
    /// </summary>
    public User CreatedBy { get; set; }
    
    /// <summary>
    /// Date and time when this space was last updated.
    /// </summary>
    [RelayProperty]
    public DateTime UpdateDate { get; set; }
    
    /// <summary>
    /// User who last updated this space.
    /// </summary>
    public User UpdatedBy { get; set; }

    /// <summary>
    /// Path to the hero image for this space.
    /// The hero image is displayed in the UI as a banner or icon for the space.
    /// </summary>
    [RelayProperty]
    public string HeroImage { get; set; } = string.Empty;

    /// <summary>
    /// User-provided notes or description of this space.
    /// </summary>
    [RelayProperty]
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Internal list of jobs in this space.
    /// </summary>
    private List<Job> _Jobs = new List<Job>();
    
    /// <summary>
    /// Read-only collection of jobs in this space.
    /// Jobs represent individual processing steps in the workflow.
    /// </summary>
    public ReadOnlyCollection<Job> Jobs => _Jobs.AsReadOnly();

    /// <summary>
    /// Internal list of edges in this space.
    /// </summary>
    private List<Edge> _Edges = new List<Edge>();
    
    /// <summary>
    /// Read-only collection of edges in this space.
    /// Edges represent connections between job ports.
    /// </summary>
    public ReadOnlyCollection<Edge> Edges => _Edges.AsReadOnly();

    /// <summary>
    /// Internal list of views in this space.
    /// </summary>
    private List<View> _Views = new List<View>();
    
    /// <summary>
    /// Read-only collection of views in this space.
    /// Views represent different visual organizations of the jobs in the space.
    /// </summary>
    public ReadOnlyCollection<View> Views => _Views.AsReadOnly();

    private List<FactoryDefinition> _FactoryDefinitions = new List<FactoryDefinition>();
    public ReadOnlyCollection<FactoryDefinition> FactoryDefinitions => _FactoryDefinitions.AsReadOnly();

    private List<FactoryInstance> _FactoryInstances = new List<FactoryInstance>();
    public ReadOnlyCollection<FactoryInstance> FactoryInstances => _FactoryInstances.AsReadOnly();

    /// <summary>
    /// Internal list of favorite jobs in this space.
    /// </summary>
    private List<Job> _Favorites = new List<Job>();
    
    /// <summary>
    /// Read-only collection of favorite jobs in this space.
    /// Favorites are jobs that the user has marked for quick access.
    /// </summary>
    public ReadOnlyCollection<Job> Favorites => _Favorites.AsReadOnly();
    
    /// <summary>
    /// Gets a fully qualified name for the space, including its ID and alias.
    /// This is used in places where a unique, human-readable identifier is needed.
    /// </summary>
    public string QualifiedName => $"S{Id}: {Alias}";

    /// <summary>
    /// Event raised when significant changes are made to the space.
    /// Subscribers can use this to update views of the space.
    /// </summary>
    public event EventHandler SpaceChanged;

    /// <summary>
    /// Creates a new, empty space.
    /// </summary>
    public Space()
    {
    }
    
    /// <summary>
    /// Returns a read-only wrapper for this space.
    /// The read-only wrapper provides a safe view that prevents accidental modification.
    /// The same wrapper instance is reused for each space to minimize object creation.
    /// </summary>
    /// <returns>A read-only wrapper for this space</returns>
    public ReadOnlySpace AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, space => new ReadOnlySpace(space));
    }

    #region Jobs

    /// <summary>
    /// Creates a new job of the specified type in this space.
    /// If a template is provided, the new job will adopt its state.
    /// </summary>
    /// <param name="typeGuid">The type GUID of the job to create</param>
    /// <param name="template">Optional template job to copy settings from</param>
    /// <param name="view">The view to add the job to</param>
    /// <returns>The newly created job</returns>
    public Job CreateJob(string typeGuid, Job template, View view)
    {
        Job job = (Job)Activator.CreateInstance(Job.Types[typeGuid]);
        if (template != null)
            job.AdoptState(template);

        job.Id = _Jobs.Select(j => j.Id).DefaultIfEmpty(0).Max() + 1;

        AddJob(job, view);

        return job;
    }

    /// <summary>
    /// Adds an existing job to this space and the specified view.
    /// Sets the job's Space property to this space and adds it to the jobs collection.
    /// </summary>
    /// <param name="job">The job to add</param>
    /// <param name="view">The view to add the job to</param>
    public void AddJob(Job job, View view)
    {
        job.Space = this;
        _Jobs.Add(job);
        view?.AddJob(job);
    }

    /// <summary>
    /// Deletes a job from this space.
    /// Also deletes all edges connected to the job, removes it from favorites,
    /// and removes it from all views.
    /// </summary>
    /// <param name="job">The job to delete</param>
    public void DeleteJob(Job job)
    {
        _Jobs.Remove(job);

        foreach (var edge in job.GetEdges(TraversalDirection.Both))
            DeleteEdge(edge);

        if (_Favorites.Contains(job))
            _Favorites.Remove(job);

        foreach (var view in _Views)
            if (view.Jobs.Contains(job))
                view.RemoveJob(job);
    }

    /// <summary>
    /// Removes a job from the internal jobs list without deleting edges, views, or favorites.
    /// Used for blueprint jobs created via CreateJob that should not persist in the space.
    /// </summary>
    internal void RemoveJobFromList(Job job)
    {
        _Jobs.Remove(job);
    }

    /// <summary>
    /// Moves a job from one view to another.
    /// Removes the job from the old view and adds it to the new view.
    /// </summary>
    /// <param name="job">The job to move</param>
    /// <param name="oldView">The view to remove the job from</param>
    /// <param name="newView">The view to add the job to</param>
    public void MoveJob(Job job, View oldView, View newView)
    {
        if (oldView == newView)
            return;

        oldView.RemoveJob(job);
        newView.AddJob(job);
    }

    /// <summary>
    /// Finds a job in this space by its ID.
    /// </summary>
    /// <param name="id">The ID of the job to find</param>
    /// <returns>The job with the specified ID, or null if not found</returns>
    public Job FindJob(int id)
    {
        return _Jobs.FirstOrDefault(j => j.Id == id);
    }

    #endregion

    #region Edges

    /// <summary>
    /// Creates a new edge connecting the specified output and input ports.
    /// The edge is assigned a unique ID and added to the edges collection.
    /// Throws if the connection would create a cycle in the job graph.
    /// </summary>
    /// <param name="source">The output port to connect from</param>
    /// <param name="target">The input port to connect to</param>
    /// <returns>The newly created edge</returns>
    /// <exception cref="InvalidOperationException">Thrown if the edge would create a cycle</exception>
    public Edge CreateEdge(PortOut source, PortIn target)
    {
        if (WouldCreateCycle(source.Job, target.Job))
            throw new InvalidOperationException(
                $"Cannot create edge from job {source.Job.Id} to job {target.Job.Id}: " +
                $"this would create a cycle in the job graph.");

        Edge edge = new Edge();
        edge.Id = _Edges.Select(e => e.Id).DefaultIfEmpty(-1).Max() + 1;
        edge.Source = source;
        edge.Target = target;

        AddEdge(edge);

        return edge;
    }

    /// <summary>
    /// Checks whether adding an edge from sourceJob to targetJob would create a cycle.
    /// A cycle exists if targetJob can already reach sourceJob by following edges downstream.
    /// </summary>
    public bool WouldCreateCycle(Job sourceJob, Job targetJob)
    {
        if (sourceJob == targetJob)
            return true;

        var visited = new HashSet<Job>();
        var queue = new Queue<Job>();
        queue.Enqueue(targetJob);
        visited.Add(targetJob);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in current.GetChildren())
            {
                if (child == sourceJob)
                    return true;
                if (visited.Add(child))
                    queue.Enqueue(child);
            }
        }

        return false;
    }

    /// <summary>
    /// Detects and removes edges that form cycles in the job graph.
    /// Returns the list of removed edges. Uses DFS-based cycle detection,
    /// removing one back-edge per cycle until the graph is acyclic.
    /// </summary>
    public List<Edge> RemoveCyclicEdges()
    {
        var removed = new List<Edge>();

        while (true)
        {
            var backEdge = FindCycleEdge();
            if (backEdge == null)
                break;

            DeleteEdge(backEdge);
            removed.Add(backEdge);
        }

        return removed;
    }

    private Edge FindCycleEdge()
    {
        // DFS coloring: 0 = unvisited, 1 = in current path, 2 = fully visited
        var state = new Dictionary<Job, int>();

        foreach (var job in Jobs)
        {
            if (state.ContainsKey(job))
                continue;

            var backEdge = DfsFindBackEdge(job, state);
            if (backEdge != null)
                return backEdge;
        }

        return null;
    }

    private Edge DfsFindBackEdge(Job start, Dictionary<Job, int> state)
    {
        var stack = new Stack<(Job job, IEnumerator<(Edge edge, Job child)> children)>();

        state[start] = 1;
        stack.Push((start, GetOutgoingEdgesWithTargets(start).GetEnumerator()));

        while (stack.Count > 0)
        {
            var (job, children) = stack.Peek();

            if (children.MoveNext())
            {
                var (edge, child) = children.Current;

                state.TryGetValue(child, out int childState);
                if (childState == 1)
                    return edge;

                if (childState == 0)
                {
                    state[child] = 1;
                    stack.Push((child, GetOutgoingEdgesWithTargets(child).GetEnumerator()));
                }
            }
            else
            {
                state[job] = 2;
                children.Dispose();
                stack.Pop();
            }
        }

        return null;
    }

    private static IEnumerable<(Edge edge, Job child)> GetOutgoingEdgesWithTargets(Job job)
    {
        foreach (var port in job.PortsOut.Values)
            foreach (var edge in port.Edges)
                if (edge.Target?.Job != null)
                    yield return (edge, edge.Target.Job);
    }

    /// <summary>
    /// Adds an existing edge to this space.
    /// Sets the edge's Space property to this space, adds it to the edges collection,
    /// and adds it to the Edges collections of both its source and target ports.
    /// </summary>
    /// <param name="edge">The edge to add</param>
    public void AddEdge(Edge edge)
    {
        edge.Space = this;
        edge.Source.Edges.Add(edge);
        edge.Target.Edges.Add(edge);
        _Edges.Add(edge);
    }

    /// <summary>
    /// Deletes an edge from this space.
    /// Removes the edge from the edges collection and from the Edges collections
    /// of both its source and target ports.
    /// </summary>
    /// <param name="edge">The edge to delete</param>
    public void DeleteEdge(Edge edge)
    {
        _Edges.Remove(edge);
        edge.Source.Edges.Remove(edge);
        edge.Target.Edges.Remove(edge);
    }

    /// <summary>
    /// Finds an edge in this space by its ID.
    /// </summary>
    /// <param name="id">The ID of the edge to find</param>
    /// <returns>The edge with the specified ID, or null if not found</returns>
    public Edge FindEdge(int id)
    {
        return _Edges.FirstOrDefault(e => e.Id == id);
    }

    #endregion

    #region View

    /// <summary>
    /// Creates a new view in this space.
    /// If a template is provided, the new view will adopt its state.
    /// </summary>
    /// <param name="template">Optional template view to copy settings from</param>
    /// <returns>The newly created view</returns>
    public View CreateView(View template)
    {
        View view = new View();
        if (template != null)
            view.AdoptState(template);

        view.Id = _Views.Select(v => v.Id).DefaultIfEmpty(0).Max() + 1;

        AddView(view);

        return view;
    }

    /// <summary>
    /// Adds an existing view to this space.
    /// Sets the view's Space property to this space and adds it to the views collection.
    /// </summary>
    /// <param name="view">The view to add</param>
    public void AddView(View view)
    {
        view.Space = this;
        _Views.Add(view);
    }

    /// <summary>
    /// Deletes a view from this space.
    /// Removes the view from the views collection.
    /// </summary>
    /// <param name="view">The view to delete</param>
    public void DeleteView(View view)
    {
        _Views.Remove(view);
    }

    /// <summary>
    /// Finds a view in this space by its ID.
    /// </summary>
    /// <param name="id">The ID of the view to find</param>
    /// <returns>The view with the specified ID, or null if not found</returns>
    public View FindView(int id)
    {
        return _Views.FirstOrDefault(v => v.Id == id);
    }

    #endregion

    #region Factory Definitions

    public FactoryDefinition CreateFactoryDefinition()
    {
        var def = new FactoryDefinition();
        def.Id = _FactoryDefinitions.Select(d => d.Id).DefaultIfEmpty(0).Max() + 1;
        _FactoryDefinitions.Add(def);
        return def;
    }

    public void DeleteFactoryDefinition(FactoryDefinition definition)
    {
        _FactoryDefinitions.Remove(definition);
    }

    public FactoryDefinition FindFactoryDefinition(int id)
    {
        return _FactoryDefinitions.FirstOrDefault(d => d.Id == id);
    }

    #endregion

    #region Factory Instances

    public FactoryInstance CreateFactoryInstance(int definitionId)
    {
        var inst = new FactoryInstance();
        inst.Id = _FactoryInstances.Select(i => i.Id).DefaultIfEmpty(0).Max() + 1;
        inst.DefinitionId = definitionId;
        inst.Space = this;
        _FactoryInstances.Add(inst);
        return inst;
    }

    public void DeleteFactoryInstance(FactoryInstance instance)
    {
        _FactoryInstances.Remove(instance);
    }

    public FactoryInstance FindFactoryInstance(int id)
    {
        return _FactoryInstances.FirstOrDefault(i => i.Id == id);
    }

    #endregion

    #region Job graph-related

    /// <summary>
    /// Get all jobs without any parents in the graph.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<Job> GetRootJobs()
    {
        return _Jobs.Where(j => j.GetParents().Count() == 0);
    }

    /// <summary>
    /// Get all jobs without any children in the graph.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<Job> GetLeafJobs()
    {
        return _Jobs.Where(j => j.GetChildren().Count() == 0);
    }

    /// <summary>
    /// Gets all disconnected groups of jobs in the graph.
    /// This uses a breadth-first search to find connected components in the job graph.
    /// Optionally, jobs can be filtered by a qualifier function.
    /// </summary>
    /// <param name="qualifier">Optional function to filter jobs. Only jobs for which this function returns true will be included.</param>
    /// <returns>An array where each element is a disconnected collection of jobs representing a partition</returns>
    public IEnumerable<Job>[] GetJobPartitions(Func<Job, bool> qualifier = null)
    {
        HashSet<Job> visited = new();
        List<IEnumerable<Job>> partitions = new List<IEnumerable<Job>>();

        foreach (var job in _Jobs)
        {
            if (visited.Contains(job))
                continue;

            if (qualifier != null && !qualifier(job))
                continue;

            List<Job> partition = new List<Job>();
            Queue<Job> queue = new Queue<Job>();
            queue.Enqueue(job);

            while (queue.Count > 0)
            {
                Job current = queue.Dequeue();
                if (partition.Contains(current))
                    continue;

                if (qualifier != null && !qualifier(current))
                    continue;

                partition.Add(current);
                visited.Add(current);

                foreach (var child in current.GetNeighbors(TraversalDirection.Both))
                    queue.Enqueue(child);
            }

            partitions.Add(partition);
        }

        return partitions.ToArray();
    }

    /// <summary>
    /// Detects if there is a cycle in the job graph, optionally involving a specific job.
    /// This uses a depth-first search with a recursion stack to detect cycles.
    /// </summary>
    /// <param name="specificJobId">Optional ID of a specific job to check for involvement in a cycle. If null, checks for any cycle.</param>
    /// <returns>True if a cycle is found, false otherwise</returns>
    public bool HasJobGraphCycle(int? specificJobId = null)
    {
        HashSet<Job> visited = new();
        HashSet<Job> recursionStack = new();
        
        Job specificJob = specificJobId.HasValue ? FindJob(specificJobId.Value) : null;

        foreach (var job in _Jobs)
            if (HasCycleUtil(job, visited, recursionStack, specificJob))
                return true;

        return false;
    }

    /// <summary>
    /// Helper method for cycle detection using depth-first search.
    /// </summary>
    /// <param name="current">The current job being examined</param>
    /// <param name="visited">Set of jobs already visited in the traversal</param>
    /// <param name="recursionStack">Set of jobs in the current recursion path</param>
    /// <param name="specificJob">Optional specific job to check for involvement in a cycle</param>
    /// <returns>True if a cycle is found, false otherwise</returns>
    private bool HasCycleUtil(Job current, HashSet<Job> visited, HashSet<Job> recursionStack, Job specificJob)
    {
        if (recursionStack.Contains(current))
            return specificJob == null || recursionStack.Contains(specificJob);

        if (visited.Contains(current))
            return false;

        visited.Add(current);
        recursionStack.Add(current);

        foreach (var neighbor in current.GetNeighbors(TraversalDirection.Both))
            if (HasCycleUtil(neighbor, visited, recursionStack, specificJob))
                return true;

        recursionStack.Remove(current);

        return false;
    }

    #endregion

    #region Serialization

    /// <summary>
    /// Serializes this space to a JSON node.
    /// This saves the space's properties, jobs, edges, views, factories, and favorites.
    /// </summary>
    /// <param name="writer">The JSON node to write to</param>
    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);

        writer["CreatedBy"] = CreatedBy?.Id;
        writer["UpdatedBy"] = UpdatedBy?.Id;

        writer["Jobs"] = new JsonArray(_Jobs.Select(j =>
        {
            JsonNode jobWriter = new JsonObject();
            Job.WritePolymorphicJson(jobWriter, j);

            return jobWriter;
        }).ToArray());

        writer["Edges"] = new JsonArray(_Edges.Select(e =>
        {
            JsonNode edgeWriter = new JsonObject();
            e.WriteToJson(edgeWriter);

            return edgeWriter;
        }).ToArray());

        writer["Views"] = new JsonArray(_Views.Select(v =>
        {
            JsonNode viewWriter = new JsonObject();
            v.WriteToJson(viewWriter);

            return viewWriter;
        }).ToArray());

        writer["FactoryDefinitions"] = new JsonArray(_FactoryDefinitions.Select(d =>
        {
            JsonNode defWriter = new JsonObject();
            d.WriteToJson(defWriter);
            return defWriter;
        }).ToArray());

        writer["FactoryInstances"] = new JsonArray(_FactoryInstances.Select(i =>
        {
            JsonNode instWriter = new JsonObject();
            i.WriteToJson(instWriter);
            return instWriter;
        }).ToArray());


        writer["Favorites"] = new JsonArray(_Favorites.Select(j => JsonValue.Create<long>(j.Id)).ToArray<JsonNode>());
    }

    /// <summary>
    /// Deserializes this space from a JSON node, resolving references to users.
    /// This loads the space's properties, jobs, edges, views, factories, and favorites.
    /// </summary>
    /// <param name="reader">The JSON node to read from</param>
    /// <param name="users">Collection of users to resolve references from</param>
    public void ReadFromJson(JsonNode reader, ReadOnlyCollection<User> users)
    {
        base.ReadFromJson(reader);
        
        if (reader["CreatedBy"] != null)
            CreatedBy = users.FirstOrDefault(u => u.Id == reader["CreatedBy"].Deserialize<int>());
        
        if (CreatedBy == null)
            CreatedBy = Project?.Owner;
        if (CreatedBy == null)
            CreatedBy = Project?.Members.FirstOrDefault();
        if (CreatedBy == null)
            CreatedBy = users.FirstOrDefault();

        if (reader["UpdatedBy"] != null)
            UpdatedBy = users.FirstOrDefault(u => u.Id == reader["UpdatedBy"].Deserialize<int>());
        
        if (UpdatedBy == null)
            UpdatedBy = Project?.Owner;
        if (UpdatedBy == null)
            UpdatedBy = Project?.Members.FirstOrDefault();
        if (UpdatedBy == null)
            UpdatedBy = users.FirstOrDefault();

        // 1. Factory Definitions first (sub-job blueprints don't need _Jobs)
        _FactoryDefinitions.Clear();
        if (reader["FactoryDefinitions"] != null)
            _FactoryDefinitions.AddRange(reader["FactoryDefinitions"].AsArray().Select(fdn =>
            {
                var def = new FactoryDefinition();
                def.ReadFromJson(fdn, this, users);
                return def;
            }));

        // 2. Jobs (including factory sub-jobs with FactoryInstanceId set)
        _Jobs.Clear();
        if (reader["Jobs"] != null)
            _Jobs.AddRange(reader["Jobs"].AsArray().Select(jn =>
            {
                var loadedJob = Job.CreateFromPolymorphicJson(jn, this, users);
                return loadedJob;
            }));

        // 3. Factory Instances (need _Jobs for SubJobIds resolution)
        _FactoryInstances.Clear();
        if (reader["FactoryInstances"] != null)
            _FactoryInstances.AddRange(reader["FactoryInstances"].AsArray().Select(fin =>
            {
                var inst = new FactoryInstance();
                inst.ReadFromJson(fin, users);
                inst.Space = this;
                return inst;
            }));

        // 4. Edges (need _Jobs for port resolution)
        _Edges.Clear();
        if (reader["Edges"] != null)
            foreach (var en in reader["Edges"].AsArray())
            {
                try
                {
                    var loadedEdge = Edge.CreateFromJson(en, _Jobs);
                    loadedEdge.Space = this;
                    loadedEdge.Source.Edges.Add(loadedEdge);
                    loadedEdge.Target.Edges.Add(loadedEdge);
                    _Edges.Add(loadedEdge);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Project {Project.Id}, space {Id}: Error loading edge: {ex.Message}");
                }
            }

        // 5. Lookup arrays for view deserialization
        Job[] jobsLookup = new Job[_Jobs.Select(j => j.Id).DefaultIfEmpty(-1).Max() + 1];
        foreach (var job in _Jobs)
            jobsLookup[job.Id] = job;

        Edge[] edgesLookup = new Edge[_Edges.Select(e => e.Id).DefaultIfEmpty(-1).Max() + 1];
        foreach (var edge in _Edges)
            edgesLookup[edge.Id] = edge;

        // Build factory instance lookup for View deserialization
        var factoryInstancesLookup = new Dictionary<int, FactoryInstance>();
        foreach (var fi in _FactoryInstances)
            factoryInstancesLookup[fi.Id] = fi;

        // 6. Views (need jobs, edges, and factory instances)
        _Views.Clear();
        if (reader["Views"] != null)
            _Views.AddRange(reader["Views"].AsArray().Select(vn =>
            {
                var loadedView = View.CreateFromJson(vn, jobsLookup, edgesLookup, this, users, factoryInstancesLookup);
                loadedView.Space = this;
                return loadedView;
            }));

        // 7. Backward compat: silently ignore old "Factories" key (old Factory subclass data is dropped)

        _Favorites.Clear();
        if (reader["Favorites"] != null)
            _Favorites.AddRange(reader["Favorites"].Deserialize<int[]>().Select(id => FindJob(id)).Where(j => j != null));

        // Compute layouts for folders/views/FIs that don't have one (legacy data)
        foreach (var view in _Views)
        {
            foreach (var folder in view.Folders)
            {
                if (folder.Layout == null)
                    folder.UpdateLayout(this);
                if (folder.DiagramLayout == null)
                    folder.UpdateDiagramLayout(this);
            }
            if (view.DiagramLayout == null)
                view.UpdateDiagramLayout(this);
        }

        foreach (var fi in _FactoryInstances)
        {
            if (fi.DiagramLayout == null && fi.SubJobIds.Count > 0)
                fi.UpdateDiagramLayout(this);
        }

        SpaceChanged?.Invoke(this, null);
    }

    #endregion

    #region Cloning

    /// <summary>
    /// Creates a shallow copy of this space.
    /// The clone will have the same properties as this space, including the same project reference,
    /// but will not have copies of jobs, edges, views, etc.
    /// </summary>
    /// <returns>A shallow copy of this space</returns>
    public Space Clone()
    {
        Space clone = new Space();
        clone.AdoptState(this);

        clone.Project = Project;

        return clone;
    }

    /// <summary>
    /// Creates a deep copy of this space.
    /// The clone will have copies of all properties, jobs, edges, views, etc.,
    /// but will share the same project reference.
    /// </summary>
    /// <returns>A deep copy of this space</returns>
    public Space DeepClone()
    {
        Space clone = new Space();
        clone.ReadFromJson(ToJson());

        clone.Project = Project;

        return clone;
    }

    #endregion
}