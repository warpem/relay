namespace Refund.Mcp;

/// <summary>Serializable shapes returned by the read-only MCP tools.</summary>
public record ProjectDto(int Id, string Alias, string Role);
public record SpaceDto(int Id, string Alias);
public record JobDto(int Id, string Alias, string TypeName, string Status);

/// <summary>A job's configured value for one parameter.</summary>
public record JobParamDto(string Name, object? Value, bool Advanced);

/// <summary>The job/port on the other end of an edge.</summary>
public record JobConnectionDto(int JobId, string PortName);

/// <summary>A job port and the ports it is connected to (upstream for inputs, downstream for outputs).</summary>
public record JobPortDto(string Name, string ResourceType, IReadOnlyList<JobConnectionDto> ConnectedTo);

public record JobDetailDto(
    int Id,
    string Alias,
    string TypeName,
    string TypeGuid,
    string Status,
    IReadOnlyList<JobParamDto> Parameters,
    IReadOnlyList<JobPortDto> Inputs,
    IReadOnlyList<JobPortDto> Outputs);
/// <summary>A downloadable result artifact of a job at a given iteration.
/// (Port, Name, Iteration) is the key passed to get_job_result_link.</summary>
public record JobResultDto(string Port, string Name, string Description, int Iteration);

public record JobTypeParamDto(string Name, string Label, string Type, string? Help, bool Advanced);

/// <summary>Lean job-type listing entry. Category is the full context-menu path, e.g.
/// "Tilt-series.Reconstruction.Map". Call get_job_type for parameters and ports.</summary>
public record JobTypeSummaryDto(string TypeGuid, string TypeName, string Category);

/// <summary>A declared port on a job type. MinItems/MaxItems are set for inputs, null for outputs.</summary>
public record JobTypePortDto(string Name, string Alias, string ResourceType, int? MinItems, int? MaxItems);

/// <summary>Full detail for one job type: parameter schema plus input/output ports.</summary>
public record JobTypeDetailDto(
    string TypeGuid,
    string TypeName,
    string Category,
    IReadOnlyList<JobTypeParamDto> Parameters,
    IReadOnlyList<JobTypePortDto> Inputs,
    IReadOnlyList<JobTypePortDto> Outputs);

/// <summary>A job queue the agent may target with queue_job. Type is "local" or "cluster".</summary>
public record QueueDto(int Id, string Alias, string Type);

/// <summary>A view within a space; create_job targets a view by id.</summary>
public record ViewDto(int Id, string Alias);

/// <summary>Result of a create_* tool.</summary>
public record CreatedDto(int Id, string Alias);

/// <summary>Generic success result for mutating tools without a created entity.</summary>
public record OkDto(bool Ok);
