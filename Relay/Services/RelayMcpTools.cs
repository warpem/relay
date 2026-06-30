using System.ComponentModel;
using ModelContextProtocol.Server;
using Refund.DataModel.ReadOnly;
using Refund.Mcp;
using Refund.Services.Core.DataManager;

namespace Relay.Services;

/// <summary>
/// Read-only MCP tools exposing the calling user's Relay data. Every method resolves the
/// current user from the authenticated principal and returns only data that user can see.
/// </summary>
[McpServerToolType]
public class RelayMcpTools(IHttpContextAccessor contextAccessor, DataManager dataManager)
{
    private ReadOnlyUser CurrentUser()
    {
        var username = contextAccessor.HttpContext?.User?.Identity?.Name
            ?? throw new InvalidOperationException("No authenticated user.");
        return dataManager.FindUser(username)
            ?? throw new InvalidOperationException("Authenticated user not found.");
    }

    [McpServerTool(Name = "list_projects"), Description("List the projects the current user can access.")]
    public IReadOnlyList<ProjectDto> ListProjects()
    {
        var user = CurrentUser();
        return dataManager.GetUserProjects(user)
            .Select(p => RelayMcpProjections.ToDto(p, user.Id))
            .ToList();
    }

    [McpServerTool(Name = "list_spaces"), Description("List the spaces in a project the current user can access.")]
    public IReadOnlyList<SpaceDto> ListSpaces(
        [Description("The project id.")] int projectId)
    {
        var user = CurrentUser();
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        if (project == null) return [];
        return project.Spaces.Select(RelayMcpProjections.ToDto).ToList();
    }

    [McpServerTool(Name = "list_jobs"), Description("List the jobs in a space, with their status.")]
    public IReadOnlyList<JobDto> ListJobs(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId)
    {
        var user = CurrentUser();
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        var space = project?.FindSpace(spaceId);
        if (space == null) return [];
        return space.Jobs.Select(RelayMcpProjections.ToDto).ToList();
    }

    [McpServerTool(Name = "get_job"), Description("Get details for a single job.")]
    public JobDetailDto? GetJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId)
    {
        var user = CurrentUser();
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        var job = project?.FindSpace(spaceId)?.FindJob(jobId);
        return job == null ? null : RelayMcpProjections.ToDetailDto(job);
    }

    [McpServerTool(Name = "list_job_types"), Description("List all available job types and their parameters.")]
    public IReadOnlyList<JobTypeDto> ListJobTypes()
    {
        _ = CurrentUser(); // require authentication
        return RelayMcpProjections.BuildJobTypeCatalog();
    }
}
