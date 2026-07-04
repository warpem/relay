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
        new(p.Id, p.Alias, ComputeProjectRole(p.Owner.Id, p.Members.Select(m => m.Id), currentUserId),
            string.IsNullOrEmpty(p.HeroImage) ? null : p.HeroImage,
            string.IsNullOrEmpty(p.Notes) ? null : p.Notes);

    public static SpaceDto ToDto(ReadOnlySpace s) =>
        new(s.Id, s.Alias,
            string.IsNullOrEmpty(s.HeroImage) ? null : s.HeroImage,
            string.IsNullOrEmpty(s.Notes) ? null : s.Notes);

    public static QueueDto ToDto(ReadOnlyJobQueue q) =>
        new(q.Id, q.Alias, q.QueueType.HasFlag(JobQueueType.Local) ? "local" : "cluster");

    public static ViewDto ToDto(ReadOnlyView v) =>
        new(v.Id, v.Alias,
            string.IsNullOrEmpty(v.HeroImage) ? null : v.HeroImage,
            string.IsNullOrEmpty(v.Notes) ? null : v.Notes);

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
    /// Resolves which iteration's results to use: the caller's explicit choice if given, otherwise the
    /// greatest iteration in [0, logsAvailableIteration] that has result files, or -1 if none do.
    /// </summary>
    public static int ResolveResultIteration(int? requested, int logsAvailableIteration, Func<int, bool> hasResultFilesForIteration)
    {
        if (requested.HasValue)
            return requested.Value;
        for (int i = logsAvailableIteration; i >= 0; i--)
            if (hasResultFilesForIteration(i))
                return i;
        return -1;
    }

    public static JobResultDto ToResultDto(string port, Downloadable d, int iteration) =>
        new(port, d.Name, d.Description, iteration);

    /// <summary>
    /// Finds the downloadable matching (port, name) among the job's enumerated downloadables, or null.
    /// Used to validate a get_job_result_link request against real outputs before exposing a file URL.
    /// </summary>
    public static Downloadable MatchDownloadable(IEnumerable<(string Port, Downloadable Downloadable)> items, string port, string name)
    {
        foreach (var (p, d) in items)
            if (p == port && d.Name == name)
                return d;
        return null;
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

    /// <summary>Category (full context-menu path) for a registered job type, e.g.
    /// "Tilt-series.Reconstruction.Map". Empty string if not categorized.</summary>
    private static string CategoryOf(Type clrType) =>
        Job.TypeCategories.FirstOrDefault(kvp => kvp.Value == clrType).Key ?? "";

    /// <summary>Lean listing of every job type: guid, display name, and full category path.</summary>
    public static IReadOnlyList<JobTypeSummaryDto> BuildJobTypeSummaries()
    {
        var result = new List<JobTypeSummaryDto>();
        foreach (var (typeGuid, clrType) in Job.Types)
        {
            var name = Job.TypeNames.TryGetValue(clrType, out var n) ? n : clrType.Name;
            result.Add(new JobTypeSummaryDto(typeGuid, name, CategoryOf(clrType)));
        }
        return result;
    }

    /// <summary>Full detail for a single job type (parameters + input/output ports), or null if
    /// the guid is unknown.</summary>
    public static JobTypeDetailDto? BuildJobTypeDetail(string typeGuid)
    {
        if (!Job.Types.TryGetValue(typeGuid, out var clrType)) return null;

        var name = Job.TypeNames.TryGetValue(clrType, out var n) ? n : clrType.Name;

        var parameters = new List<JobTypeParamDto>();
        if (Job.TypeUiFields.TryGetValue(clrType, out var fields))
            foreach (var (prop, uiField) in fields)
                parameters.Add(new JobTypeParamDto(
                    Name: prop.Name,
                    Label: uiField.Label ?? prop.Name,
                    Type: prop.PropertyType.Name,
                    Help: string.IsNullOrEmpty(uiField.HelpText) ? null : uiField.HelpText,
                    Advanced: uiField.IsAdvanced));

        var inputs = new List<JobTypePortDto>();
        if (Job.AllTypesPortsIn.TryGetValue(clrType, out var portsIn))
            foreach (var (portName, port) in portsIn)
                inputs.Add(new JobTypePortDto(portName, port.Alias, port.ResourceType.Name, port.MinItems, port.MaxItems));

        var outputs = new List<JobTypePortDto>();
        if (Job.AllTypesPortsOut.TryGetValue(clrType, out var portsOut))
            foreach (var (portName, port) in portsOut)
                outputs.Add(new JobTypePortDto(portName, port.Alias, port.ResourceType.Name, null, null));

        return new JobTypeDetailDto(typeGuid, name, CategoryOf(clrType), parameters, inputs, outputs);
    }
}
