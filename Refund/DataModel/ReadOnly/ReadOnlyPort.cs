using System.Collections.ObjectModel;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// Base read-only decorator for the Port class, providing immutable access to port data.
/// Ports are connection points on jobs that define inputs and outputs for data flow.
/// </summary>
public class ReadOnlyPort
{
    /// <summary>
    /// The wrapped mutable port instance.
    /// </summary>
    protected readonly Port _port;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyPort"/> class.
    /// </summary>
    /// <param name="port">The mutable port to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if the port parameter is null.</exception>
    internal ReadOnlyPort(Port port)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
    }

    /// <summary>
    /// Gets the read-only job that this port belongs to.
    /// </summary>
    public ReadOnlyJob Job => _port.Job?.AsReadOnly();
    
    /// <summary>
    /// Gets the type of resource this port handles.
    /// Resource types define the kind of data that flows through this port.
    /// </summary>
    public Type ResourceType => _port.ResourceType;
    
    /// <summary>
    /// Gets the identifier name of this port.
    /// The name is used internally to reference the port.
    /// </summary>
    public string Name => _port.Name;
    
    /// <summary>
    /// Gets the user-friendly display name of this port.
    /// The alias is used in the UI to label the port.
    /// </summary>
    public string Alias => _port.Alias;
    
    /// <summary>
    /// Gets a value indicating whether this port handles streaming data.
    /// Streaming ports can process data as it becomes available, without waiting for the entire dataset.
    /// </summary>
    public bool IsStreaming => _port.IsStreaming;
    
    /// <summary>
    /// Gets a read-only collection of edges connected to this port.
    /// Edges represent connections between output and input ports.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyEdge> Edges => new(_port.Edges.Select(e => e.AsReadOnly()).ToList());
    
    /// <summary>
    /// Gets a value indicating whether this port has any connections.
    /// </summary>
    public bool IsConnected => _port.IsConnected;
}

/// <summary>
/// A read-only decorator for the PortIn class, providing immutable access to input port data.
/// Input ports receive data from other jobs' output ports.
/// </summary>
public sealed class ReadOnlyPortIn : ReadOnlyPort
{
    /// <summary>
    /// The wrapped mutable input port instance.
    /// </summary>
    private readonly PortIn _portIn;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyPortIn"/> class.
    /// </summary>
    /// <param name="port">The mutable input port to wrap.</param>
    internal ReadOnlyPortIn(PortIn port) : base(port)
    {
        _portIn = port;
    }

    /// <summary>
    /// Gets the minimum number of connections required for this input port.
    /// A port with MinItems > 0 is a required input for the job.
    /// </summary>
    public int MinItems => _portIn.MinItems;
    
    /// <summary>
    /// Gets the maximum number of connections allowed for this input port.
    /// A port with MaxItems > 1 can accept multiple inputs.
    /// </summary>
    public int MaxItems => _portIn.MaxItems;
    
    /// <summary>
    /// Gets the current number of connections to this input port.
    /// This is the number of edges that connect to this port.
    /// </summary>
    public int Count => _portIn.Count;
    
    /// <summary>
    /// Checks if this input port has any resources of the specified type T.
    /// </summary>
    /// <typeparam name="T">The resource type to check for</typeparam>
    /// <returns>True if at least one resource of type T is available, otherwise false</returns>
    public bool HasResource<T>() where T : Resource => _portIn.HasResource<T>();
    
    /// <summary>
    /// Gets the single resource of the specified type T from this input port.
    /// </summary>
    /// <typeparam name="T">The resource type to get</typeparam>
    /// <returns>The resource of type T, or null if there is none</returns>
    public T GetSingleResource<T>() where T : Resource => _portIn.GetSingleResource<T>();
    
    /// <summary>
    /// Determines if this port is currently active based on the job's state.
    /// </summary>
    /// <returns>True if the port is active, false if inactive</returns>
    public bool IsActive() => _portIn.IsActive();
}

/// <summary>
/// A read-only decorator for the PortOut class, providing immutable access to output port data.
/// Output ports provide data to other jobs' input ports.
/// </summary>
public sealed class ReadOnlyPortOut : ReadOnlyPort
{
    /// <summary>
    /// The wrapped mutable output port instance.
    /// </summary>
    private readonly PortOut _portOut;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyPortOut"/> class.
    /// </summary>
    /// <param name="port">The mutable output port to wrap.</param>
    internal ReadOnlyPortOut(PortOut port) : base(port)
    {
        _portOut = port;
    }

    /// <summary>
    /// Gets the resource produced by this output port for a specific iteration.
    /// The resource contains the data that flows to connected input ports.
    /// </summary>
    /// <param name="iteration">The iteration number to get the resource for. 
    /// A value of -1 (default) typically means the latest available iteration.</param>
    /// <returns>The resource produced by this output port.</returns>
    public Resource GetResource(int iteration = -1) => _portOut.GetResource(iteration);
}