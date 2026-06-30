namespace Refund.Mcp;

/// <summary>Serializable shapes returned by the read-only MCP tools.</summary>
public record ProjectDto(int Id, string Alias, string Role);
public record SpaceDto(int Id, string Alias);
public record JobDto(int Id, string Alias, string TypeName, string Status);
public record JobDetailDto(int Id, string Alias, string TypeName, string TypeGuid, string Status);
public record JobTypeParamDto(string Name, string Label, string Type, string? Help, bool Advanced);
public record JobTypeDto(string TypeGuid, string TypeName, string Category, IReadOnlyList<JobTypeParamDto> Parameters);
