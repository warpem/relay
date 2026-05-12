using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;

namespace Refund.DataModel;

/// <summary>
/// Represents a connection between an output port of one job and an input port of another job.
/// Edges define the flow of data through the processing graph, connecting a source output port
/// to a target input port.
/// </summary>
public class Edge : RelayBase
{
    /// <summary>
    /// Cache of read-only wrappers for edges, using weak references to avoid memory leaks.
    /// </summary>
    private static readonly ConditionalWeakTable<Edge, ReadOnlyEdge> ReadOnlyCache = new();
    
    /// <summary>
    /// The space containing this edge.
    /// </summary>
    public Space Space { get; set; } = null;

    /// <summary>
    /// Unique identifier for this edge within its containing space.
    /// </summary>
    [RelayProperty(Order = 0)]
    public int Id { get; set; } = -1;

    /// <summary>
    /// The source (output) port from which data flows.
    /// </summary>
    public PortOut Source;
    
    /// <summary>
    /// The target (input) port to which data flows.
    /// </summary>
    public PortIn Target;
        
    /// <summary>
    /// Returns a read-only wrapper for this edge.
    /// The read-only wrapper provides a safe view that prevents accidental modification.
    /// The same wrapper instance is reused for each edge to minimize object creation.
    /// </summary>
    /// <returns>A read-only wrapper for this edge</returns>
    public ReadOnlyEdge AsReadOnly() 
    {
        return ReadOnlyCache.GetValue(this, edge => new ReadOnlyEdge(edge));
    }

    /// <summary>
    /// Serializes this edge to a JSON node.
    /// In addition to the base implementation, this writes the source and target port references
    /// in the format "JobId.PortName".
    /// </summary>
    /// <param name="writer">The JSON node to write to</param>
    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);

        writer["Source"] = $"{Source.Job.Id}.{Source.Name}";
        writer["Target"] = $"{Target.Job.Id}.{Target.Name}";
    }

    /// <summary>
    /// Deserializes this edge from a JSON node.
    /// Resolves source and target port references from the provided collection of jobs.
    /// </summary>
    /// <param name="reader">The JSON node to read from</param>
    /// <param name="jobs">Collection of jobs to resolve port references from</param>
    /// <exception cref="Exception">Thrown if source or target port references cannot be resolved</exception>
    public void ReadFromJson(JsonNode reader, IEnumerable<Job> jobs)
    {
        base.ReadFromJson(reader);

        #region Source

        string[] mixedSource = reader["Source"].Deserialize<string>().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (mixedSource.Length != 2)
            throw new Exception($"Source must be in the form of JobID.PortName; got {reader["Source"].Deserialize<string>()} instead");

        int sourceId = int.Parse(mixedSource[0]);
        Job sourceJob = jobs.FirstOrDefault(j => j.Id == sourceId);

        if(sourceJob == null)
            throw new Exception($"Couldn't find job with ID {sourceId}");
        if (!sourceJob.PortsOut.ContainsKey(mixedSource[1]))
            throw new Exception($"Job {sourceId} doesn't have an output port named {mixedSource[1]}");

        Source = sourceJob.PortsOut[mixedSource[1]];

        #endregion

        #region Target

        string[] mixedTarget = reader["Target"].Deserialize<string>().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (mixedTarget.Length != 2)
            throw new Exception($"Target must be in the form of JobID.PortName; got {reader["Target"].Deserialize<string>()} instead");

        int targetId = int.Parse(mixedTarget[0]);
        Job targetJob = jobs.FirstOrDefault(j => j.Id == targetId);

        if(targetJob == null)
            throw new Exception($"Couldn't find job with ID {targetId}");
        if (!targetJob.PortsIn.ContainsKey(mixedTarget[1]))
            throw new Exception($"Job {targetId} doesn't have an input port named {mixedTarget[1]}");

        Target = targetJob.PortsIn[mixedTarget[1]];

        #endregion
    }

    /// <summary>
    /// Creates a new Edge instance from a JSON node.
    /// Factory method that simplifies edge creation from serialized data.
    /// </summary>
    /// <param name="reader">The JSON node to read from</param>
    /// <param name="jobs">Collection of jobs to resolve port references from</param>
    /// <returns>A new Edge instance with properties set from the JSON node</returns>
    public static Edge CreateFromJson(JsonNode reader, IEnumerable<Job> jobs)
    {
        Edge result = new Edge();
        result.ReadFromJson(reader, jobs);

        return result;
    }

    /// <summary>
    /// Creates a shallow copy of this edge.
    /// The clone will have the same properties as this edge, including the same space reference,
    /// but will be a separate instance.
    /// </summary>
    /// <returns>A shallow copy of this edge</returns>
    public Edge Clone()
    {
        Edge clone = new Edge();
        clone.AdoptState(this);

        clone.Space = Space;

        return clone;
    }
}