using System.Reflection;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Refund.Mcp;

/// <summary>
/// Pure projections from Relay's read-only model (and the static job-type registry) to MCP DTOs.
/// Kept free of ASP.NET / DataManager dependencies so it is unit-testable in Refund.Tests.
/// </summary>
public static class RelayMcpProjections
{
    public static string ComputeProjectRole(int ownerId, IEnumerable<int> memberIds, int currentUserId)
    {
        if (currentUserId == ownerId) return "owner";
        return memberIds.Contains(currentUserId) ? "member" : "none";
    }

    public static ProjectDto ToDto(ReadOnlyProject p, int currentUserId) =>
        new(p.Id, p.Alias, ComputeProjectRole(p.Owner.Id, p.Members.Select(m => m.Id), currentUserId));

    public static SpaceDto ToDto(ReadOnlySpace s) => new(s.Id, s.Alias);

    public static JobDto ToDto(ReadOnlyJob j) =>
        new(j.Id, j.AliasOrId, j.TypeName, j.Status.ToString());

    public static JobDetailDto ToDetailDto(ReadOnlyJob j)
    {
        var jobType = j.GetOriginalType();

        var parameters = new List<JobParamDto>();
        if (Job.TypeParameters.TryGetValue(jobType, out var props))
        {
            Job.TypeAdvancedParameters.TryGetValue(jobType, out var advanced);
            foreach (var prop in props)
                parameters.Add(new JobParamDto(
                    prop.Name,
                    ToJsonSafeValue(j.GetParameterValue(prop)),
                    advanced != null && advanced.Contains(prop)));
        }

        var inputs = new List<JobPortDto>();
        foreach (var (name, portIn) in j.PortsIn)
        {
            var conns = new List<JobConnectionDto>();
            foreach (var edge in portIn.Edges)
            {
                var src = edge.Source; // wrappers are rebuilt per access; cache the local
                conns.Add(new JobConnectionDto(src.Job.Id, src.Name));
            }
            inputs.Add(new JobPortDto(name, portIn.ResourceType.Name, conns));
        }

        var outputs = new List<JobPortDto>();
        foreach (var (name, portOut) in j.PortsOut)
        {
            var conns = new List<JobConnectionDto>();
            foreach (var edge in portOut.Edges)
            {
                var tgt = edge.Target; // wrappers are rebuilt per access; cache the local
                conns.Add(new JobConnectionDto(tgt.Job.Id, tgt.Name));
            }
            outputs.Add(new JobPortDto(name, portOut.ResourceType.Name, conns));
        }

        return new JobDetailDto(
            j.Id, j.AliasOrId, j.TypeName, j.TypeGuid, j.Status.ToString(),
            parameters, inputs, outputs);
    }

    /// <summary>
    /// Coerces a parameter value into something that serializes cleanly to JSON for the agent:
    /// primitives pass through, enums and anything else become their string form, null stays null.
    /// </summary>
    private static object? ToJsonSafeValue(object? value) => value switch
    {
        null => null,
        string or bool or byte or sbyte or short or ushort or int or uint or long or ulong
            or float or double or decimal or DateTime => value,
        Enum e => e.ToString(),
        _ => value.ToString()
    };

    public static IReadOnlyList<JobTypeDto> BuildJobTypeCatalog()
    {
        var result = new List<JobTypeDto>();
        foreach (var (typeGuid, clrType) in Job.Types)
        {
            var name = Job.TypeNames.TryGetValue(clrType, out var n) ? n : clrType.Name;
            var category = Job.TypeCategories.FirstOrDefault(kvp => kvp.Value == clrType).Key ?? "";
            var parameters = new List<JobTypeParamDto>();
            if (Job.TypeUiFields.TryGetValue(clrType, out var fields))
                foreach (var (prop, uiField) in fields)
                    parameters.Add(new JobTypeParamDto(
                        Name: prop.Name,
                        Label: uiField.Label ?? prop.Name,
                        Type: prop.PropertyType.Name,
                        Help: string.IsNullOrEmpty(uiField.HelpText) ? null : uiField.HelpText,
                        Advanced: uiField.IsAdvanced));
            result.Add(new JobTypeDto(typeGuid, name, category, parameters));
        }
        return result;
    }
}
