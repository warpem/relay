using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// A read-only decorator for the Edge class, providing immutable access to edge data.
/// Edges represent connections between job ports in the workflow, defining the flow of data.
/// </summary>
public sealed class ReadOnlyEdge
{
    private readonly Edge _edge;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyEdge"/> class.
    /// </summary>
    /// <param name="edge">The mutable edge to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if the edge parameter is null.</exception>
    internal ReadOnlyEdge(Edge edge)
    {
        _edge = edge ?? throw new ArgumentNullException(nameof(edge));
    }
    
    /// <summary>
    /// Gets the read-only space that contains this edge.
    /// The space is the parent container for the workflow graph.
    /// </summary>
    public ReadOnlySpace Space => _edge.Space?.AsReadOnly();
    
    /// <summary>
    /// Gets the unique identifier for this edge.
    /// </summary>
    public int Id => _edge.Id;
    
    /// <summary>
    /// Gets the read-only source (output) port where this edge originates.
    /// The source port belongs to the job that produces data.
    /// </summary>
    public ReadOnlyPortOut Source => _edge.Source?.AsReadOnly();
    
    /// <summary>
    /// Gets the read-only target (input) port where this edge terminates.
    /// The target port belongs to the job that consumes data.
    /// </summary>
    public ReadOnlyPortIn Target => _edge.Target?.AsReadOnly();
    
    /// <summary>
    /// Converts this edge to a JSON representation.
    /// </summary>
    /// <returns>A JSON node containing the serialized edge data.</returns>
    public JsonNode ToJson() => _edge.ToJson();
    
    /// <summary>
    /// Converts this edge to a JSON string representation.
    /// </summary>
    /// <returns>A JSON string containing the serialized edge data.</returns>
    public string ToJsonString() => _edge.ToJsonString();
}