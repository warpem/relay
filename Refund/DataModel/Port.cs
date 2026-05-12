using System.Runtime.CompilerServices;
using Refund.DataModel.ReadOnly;

namespace Refund.DataModel;

/// <summary>
/// Base class for job connection points that allow data to flow between jobs.
/// Ports define the inputs and outputs of a job and serve as the endpoints for edges in the processing graph.
/// Each port has a specific resource type that defines what kind of data can flow through it.
/// </summary>
public class Port
{
    /// <summary>
    /// Cache of read-only wrappers for ports, using weak references to avoid memory leaks.
    /// </summary>
    private static readonly ConditionalWeakTable<Port, ReadOnlyPort> ReadOnlyCache = new();
    
    /// <summary>
    /// The job that owns this port.
    /// </summary>
    public readonly Job Job;
    
    /// <summary>
    /// The type of resource that can flow through this port.
    /// This defines the data compatibility between connected ports.
    /// </summary>
    public readonly Type ResourceType;
    
    /// <summary>
    /// The internal name of the port used for identification.
    /// This is typically a programmatic identifier not meant for display.
    /// </summary>
    public readonly string Name;
    
    /// <summary>
    /// The display name of the port, used for UI presentation.
    /// </summary>
    public readonly string Alias;
    
    /// <summary>
    /// Indicates whether this port handles streaming data.
    /// Streaming ports allow data to flow continuously and can process partial results.
    /// </summary>
    public readonly bool IsStreaming;

    /// <summary>
    /// Collection of edges connected to this port.
    /// </summary>
    public readonly List<Edge> Edges = new();
    
    public bool IsConnected => Edges.Count > 0;

    /// <summary>
    /// Creates a new port with the specified properties.
    /// </summary>
    /// <param name="job">The job that owns this port</param>
    /// <param name="resourceType">The type of resource that can flow through this port</param>
    /// <param name="name">The internal name of the port</param>
    /// <param name="alias">The display name of the port</param>
    /// <param name="isStreaming">Whether this port handles streaming data</param>
    public Port(Job job, Type resourceType, string name, string alias, bool isStreaming)
    {
        Job = job;
        ResourceType = resourceType;
        Name = name;
        Alias = alias;
        IsStreaming = isStreaming;
    }
        
    /// <summary>
    /// Returns a read-only wrapper for this port.
    /// The read-only wrapper provides a safe view that prevents accidental modification.
    /// The same wrapper instance is reused for each port to minimize object creation.
    /// </summary>
    /// <returns>A read-only wrapper for this port</returns>
    public ReadOnlyPort AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, port => new ReadOnlyPort(port));
    }
}

/// <summary>
/// Represents an input port for a job, which receives data from output ports of other jobs.
/// Input ports define constraints on the number of connections that can be made to them.
/// </summary>
public class PortIn : Port
{
    /// <summary>
    /// Cache of read-only wrappers for input ports, using weak references to avoid memory leaks.
    /// </summary>
    private static readonly ConditionalWeakTable<PortIn, ReadOnlyPortIn> ReadOnlyCache = new();
        
    /// <summary>
    /// The minimum number of connections required for this input port.
    /// If fewer connections exist, the job will not be ready to run.
    /// </summary>
    public int MinItems;
    
    /// <summary>
    /// The maximum number of connections allowed for this input port.
    /// If this limit is reached, no more connections can be made to this port.
    /// </summary>
    public int MaxItems;

    /// <summary>
    /// Optional delegate that determines if this port is active based on the job's current state.
    /// If null, the port is always active. If returns false, the port is considered inactive.
    /// Inactive ports are displayed transparently and their validation errors don't block job submission.
    /// </summary>
    public Func<Job, bool> IsActiveDelegate;

    /// <summary>
    /// Gets the current number of connections to this input port.
    /// TODO: Replace with counting logic that considers resource collections
    /// </summary>
    public int Count => Edges.Count;

    /// <summary>
    /// Creates a new input port with the specified properties.
    /// </summary>
    /// <param name="job">The job that owns this port</param>
    /// <param name="resourceType">The type of resource that can flow through this port</param>
    /// <param name="name">The internal name of the port</param>
    /// <param name="alias">The display name of the port</param>
    /// <param name="minItems">The minimum number of connections required</param>
    /// <param name="maxItems">The maximum number of connections allowed</param>
    /// <param name="isStreaming">Whether this port handles streaming data</param>
    /// <param name="isActiveDelegate">Optional delegate to determine if this port is active based on job state</param>
    public PortIn(Job job,
                  Type resourceType,
                  string name,
                  string alias,
                  int minItems,
                  int maxItems,
                  bool isStreaming = false,
                  Func<Job, bool> isActiveDelegate = null) : base(job,
                                                                   resourceType,
                                                                   name,
                                                                   alias,
                                                                   isStreaming)
    {
        MinItems = minItems;
        MaxItems = maxItems;
        IsActiveDelegate = isActiveDelegate;
    }
    
    /// <summary>
    /// Checks if this input port has any resources of the specified type T.
    /// </summary>
    /// <typeparam name="T">The resource type to check for</typeparam>
    /// <returns>True if at least one resource of type T is available, otherwise false</returns>
    public bool HasResource<T>() where T : Resource => Edges.Any(edge => edge.Source.GetResource() is T);
    
    /// <summary>
    /// Gets the single resource of the specified type T from this input port.
    /// </summary>
    /// <typeparam name="T">The resource type to get</typeparam>
    /// <returns>The resource of type T, or null if there is none</returns>
    public T GetSingleResource<T>(int iteration = -1) where T : Resource => Edges.FirstOrDefault()?.Source.GetResource(iteration) as T;
    
    /// <summary>
    /// Determines if this port is currently active based on the job's state.
    /// </summary>
    /// <returns>True if the port is active, false if inactive</returns>
    public bool IsActive() => IsActiveDelegate?.Invoke(Job) ?? true;
    
    /// <summary>
    /// Returns a read-only wrapper for this input port.
    /// The read-only wrapper provides a safe view that prevents accidental modification.
    /// The same wrapper instance is reused for each input port to minimize object creation.
    /// </summary>
    /// <returns>A read-only wrapper for this input port</returns>
    public new ReadOnlyPortIn AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, port => new ReadOnlyPortIn(port));
    }
}

/// <summary>
/// Represents an output port for a job, which sends data to input ports of other jobs.
/// Output ports are responsible for producing resources that can be consumed by connected input ports.
/// </summary>
public class PortOut : Port
{
    /// <summary>
    /// Cache of read-only wrappers for output ports, using weak references to avoid memory leaks.
    /// </summary>
    private static readonly ConditionalWeakTable<PortOut, ReadOnlyPortOut> ReadOnlyCache = new();
        
    /// <summary>
    /// Delegate that produces resources for this output port.
    /// This function is called when consumers request the output resource.
    /// </summary>
    private Func<int, Resource> ResourceDelegate;

    /// <summary>
    /// Creates a new output port with the specified properties.
    /// </summary>
    /// <param name="job">The job that owns this port</param>
    /// <param name="resourceType">The type of resource that can flow through this port</param>
    /// <param name="name">The internal name of the port</param>
    /// <param name="alias">The display name of the port</param>
    /// <param name="resourceDelegate">Function that produces resources for this port</param>
    /// <param name="isStreaming">Whether this port handles streaming data</param>
    public PortOut(Job job,
                   Type resourceType,
                   string name,
                   string alias,
                   Func<int, Resource> resourceDelegate,
                   bool isStreaming = false) : base(job,
                                                    resourceType,
                                                    name,
                                                    alias,
                                                    isStreaming)
    {
        ResourceDelegate = resourceDelegate;
    }
    
    /// <summary>
    /// Returns a read-only wrapper for this output port.
    /// The read-only wrapper provides a safe view that prevents accidental modification.
    /// The same wrapper instance is reused for each output port to minimize object creation.
    /// </summary>
    /// <returns>A read-only wrapper for this output port</returns>
    public new ReadOnlyPortOut AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, port => new ReadOnlyPortOut(port));
    }

    /// <summary>
    /// Gets the resource associated with this output port for a specific iteration.
    /// This method invokes the resource delegate to generate or retrieve the appropriate resource.
    /// </summary>
    /// <param name="iteration">The iteration to get the resource for. -1 represents the final iteration.</param>
    /// <returns>The resource produced by this output port, or null if the resource delegate is not set</returns>
    public Resource GetResource(int iteration = -1) => ResourceDelegate?.Invoke(iteration);
}