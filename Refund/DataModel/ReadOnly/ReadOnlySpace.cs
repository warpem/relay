using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// A read-only decorator for the Space class, providing immutable access to space data.
/// Spaces are containers for jobs and their connections, representing complete data processing workflows.
/// </summary>
public sealed class ReadOnlySpace : IIdentifiable, IAudited, IAnnotated, IJobContainer
{
    /// <summary>
    /// The wrapped mutable space instance.
    /// </summary>
    private readonly Space _space;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlySpace"/> class.
    /// </summary>
    /// <param name="space">The mutable space to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if the space parameter is null.</exception>
    internal ReadOnlySpace(Space space)
    {
        _space = space ?? throw new ArgumentNullException(nameof(space));
    }

    /// <summary>
    /// Gets the read-only project that contains this space.
    /// </summary>
    public ReadOnlyProject Project => _space.Project?.AsReadOnly();
    
    /// <summary>
    /// Gets the unique identifier for this space.
    /// </summary>
    public int Id => _space.Id;
    
    /// <summary>
    /// Gets the path to the root directory where this space's data is stored.
    /// This directory contains all the job directories and space metadata.
    /// </summary>
    public string RootDirectory => _space.RootDirectory;
    
    /// <summary>
    /// Gets the path to the file containing this space's serialized data.
    /// </summary>
    public string FilePath => _space.FilePath;
    
    /// <summary>
    /// Gets the user-defined display name of this space.
    /// </summary>
    public string Alias => _space.Alias;
    
    /// <summary>
    /// Gets a fully qualified name that combines the ID and alias.
    /// This provides a unique, human-readable identifier for UI display.
    /// </summary>
    public string QualifiedName => _space.QualifiedName;

    public IReadOnlyList<ReadOnlyJobEvent> GetEvents(EventType? type)
    {
        if (!type.HasValue)
            return new List<ReadOnlyJobEvent>().AsReadOnly();

        return type switch
        {
            EventType.Created => new List<ReadOnlyJobEvent>([
                new ReadOnlyJobEvent(new JobEvent(EventType.Created,
                                                  _space.CreationDate,
                                                  _space.CreatedBy))
            ]).AsReadOnly(),
            
            _ => new List<ReadOnlyJobEvent>().AsReadOnly()
        };
    }

    public ReadOnlyJobEvent GetMostRecentEvent(EventType? type = null)
    {
        if (!type.HasValue || type.Value != EventType.Created)
            return null;
        
        return new ReadOnlyJobEvent(new JobEvent(EventType.Created, 
                                                 _space.CreationDate, 
                                                 _space.CreatedBy));
    }
    
    /// <summary>
    /// Gets the date and time when this space was last updated.
    /// </summary>
    public DateTime UpdateDate => _space.UpdateDate;
    
    /// <summary>
    /// Gets the user who last updated this space.
    /// </summary>
    public ReadOnlyUser UpdatedBy => _space.UpdatedBy.AsReadOnly();

    /// <summary>
    /// Gets the path to the hero image for this space.
    /// The hero image is displayed in the UI as a banner or icon.
    /// </summary>
    public string HeroImage => _space.HeroImage;
    
    /// <summary>
    /// Gets the user-provided notes or description of this space.
    /// </summary>
    public string Notes => _space.Notes;

    /// <summary>
    /// Gets a read-only collection of jobs contained in this space.
    /// Jobs are the processing units that form the workflow graph.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyJob> Jobs =>
        new(_space.Jobs.Select(j => j.AsReadOnly()).ToList());

    /// <summary>
    /// Gets a read-only collection of edges contained in this space.
    /// Edges represent connections between job ports, defining the data flow.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyEdge> Edges =>
        new(_space.Edges.Select(e => e.AsReadOnly()).ToList());

    /// <summary>
    /// Gets a read-only collection of views for this space.
    /// Views provide different visual representations of the job graph.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyView> Views =>
        new(_space.Views.Select(v => v.AsReadOnly()).ToList());

    /// <summary>
    /// Gets a read-only collection of factory definitions in this space.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyFactoryDefinition> FactoryDefinitions =>
        new(_space.FactoryDefinitions.Select(d => d.AsReadOnly()).ToList());

    /// <summary>
    /// Gets a read-only collection of factory instances in this space.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyFactoryInstance> FactoryInstances =>
        new(_space.FactoryInstances.Select(i => i.AsReadOnly()).ToList());

    public ReadOnlyFactoryDefinition FindFactoryDefinition(int id) =>
        _space.FindFactoryDefinition(id)?.AsReadOnly();

    public ReadOnlyFactoryInstance FindFactoryInstance(int id) =>
        _space.FindFactoryInstance(id)?.AsReadOnly();

    /// <summary>
    /// Gets a read-only collection of favorite jobs in this space.
    /// Favorite jobs are those marked by the user for quick access.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyJob> Favorites =>
        new(_space.Favorites.Select(j => j.AsReadOnly()).ToList());

    /// <summary>
    /// Converts an absolute path to a path relative to this space's root directory.
    /// </summary>
    /// <param name="path">The absolute path to convert.</param>
    /// <returns>A path relative to the space's root directory.</returns>
    public string GetRelativePath(string path) => _space.GetRelativePath(path);

    /// <summary>
    /// Finds a job within this space by its ID.
    /// </summary>
    /// <param name="id">The ID of the job to find.</param>
    /// <returns>A read-only wrapper of the found job, or null if no job with the given ID exists in the space.</returns>
    public ReadOnlyJob FindJob(int id) => _space.FindJob(id)?.AsReadOnly();
    
    /// <summary>
    /// Finds an edge within this space by its ID.
    /// </summary>
    /// <param name="id">The ID of the edge to find.</param>
    /// <returns>A read-only wrapper of the found edge, or null if no edge with the given ID exists in the space.</returns>
    public ReadOnlyEdge FindEdge(int id) => _space.FindEdge(id)?.AsReadOnly();
    
    /// <summary>
    /// Finds a view within this space by its ID.
    /// </summary>
    /// <param name="id">The ID of the view to find.</param>
    /// <returns>A read-only wrapper of the found view, or null if no view with the given ID exists in the space.</returns>
    public ReadOnlyView FindView(int id) => _space.FindView(id)?.AsReadOnly();

    /// <summary>
    /// Gets the root jobs in this space.
    /// Root jobs are those that have no parent jobs (no incoming connections).
    /// </summary>
    /// <returns>An enumerable collection of read-only root jobs.</returns>
    public IEnumerable<ReadOnlyJob> GetRootJobs() =>
        _space.GetRootJobs().Select(j => j.AsReadOnly());

    /// <summary>
    /// Gets the leaf jobs in this space.
    /// Leaf jobs are those that have no child jobs (no outgoing connections).
    /// </summary>
    /// <returns>An enumerable collection of read-only leaf jobs.</returns>
    public IEnumerable<ReadOnlyJob> GetLeafJobs() =>
        _space.GetLeafJobs().Select(j => j.AsReadOnly());

    /// <summary>
    /// Gets the disconnected partitions of jobs in this space.
    /// A partition is a group of connected jobs that has no connections to other groups.
    /// </summary>
    /// <param name="qualifier">An optional predicate to filter which jobs to include in the partitioning.</param>
    /// <returns>A 2D array of read-only jobs, where each inner array represents a disconnected partition.</returns>
    public ReadOnlyJob[][] GetJobPartitions(Func<ReadOnlyJob, bool> qualifier = null) =>
        _space.GetJobPartitions(qualifier == null ? null : (j => qualifier(j.AsReadOnly())))
              .Select(partition => partition.Select(j => j.AsReadOnly()).ToArray())
              .ToArray();

    /// <summary>
    /// Determines whether the job graph in this space contains a cycle.
    /// A cycle is a path from a job back to itself, which is not allowed in the workflow system.
    /// </summary>
    /// <param name="specificJob">An optional job to check for cycles starting from. If null, the entire graph is checked.</param>
    /// <returns>True if a cycle exists; otherwise, false.</returns>
    public bool HasJobGraphCycle(ReadOnlyJob specificJob = null) =>
        _space.HasJobGraphCycle(specificJob?.Id);

    /// <summary>
    /// Converts this space to a JSON representation.
    /// </summary>
    /// <returns>A JSON node containing the serialized space data.</returns>
    public JsonNode ToJson() => _space.ToJson();
}