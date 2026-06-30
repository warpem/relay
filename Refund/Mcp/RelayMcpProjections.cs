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

    public static JobDetailDto ToDetailDto(ReadOnlyJob j) =>
        new(j.Id, j.AliasOrId, j.TypeName, j.TypeGuid, j.Status.ToString());

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
