using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Mcp;
using Refund.Services.Core.DataManager;
using Refund.Utils;

namespace Relay.Services;

/// <summary>
/// MCP tools exposing the calling user's Relay data. Every method resolves the current user from
/// the authenticated principal and returns only data that user can see; mutating tools additionally
/// check the token's per-tier <see cref="AccessLevel"/> grants (stashed by the Pat auth handler).
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

    private PatGrants Grants() =>
        contextAccessor.HttpContext?.Items["PatGrants"] is PatGrants g
            ? g
            : new PatGrants(AccessLevel.None, AccessLevel.None, AccessLevel.None);

    private bool Can(PermTier tier, AccessLevel level) => PatAuthorization.Allows(Grants(), tier, level);

    private void Require(PermTier tier, AccessLevel level)
    {
        if (!Can(tier, level))
            throw new McpException($"This token lacks {level} access for {tier} operations.");
    }

    /// <summary>
    /// Runs a DataManager mutation and surfaces its (business-rule) exception message to the agent.
    /// DataManager throws plain exceptions for validation/state errors; the MCP host masks
    /// non-McpException messages with a generic string, so we re-wrap them as McpException.
    /// </summary>
    private static async Task<T> Invoke<T>(Func<Task<T>> op)
    {
        try { return await op(); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message); }
    }

    private static async Task Invoke(Func<Task> op)
    {
        try { await op(); }
        catch (McpException) { throw; }
        catch (Exception ex) { throw new McpException(ex.Message); }
    }

    // ---- Read tools ---------------------------------------------------------

    [McpServerTool(Name = "list_projects"), Description("List the projects the current user can access.")]
    public IReadOnlyList<ProjectDto> ListProjects()
    {
        var user = CurrentUser();
        if (!Can(PermTier.Project, AccessLevel.Read)) return [];
        return dataManager.GetUserProjects(user)
            .Select(p => RelayMcpProjections.ToDto(p, user.Id))
            .ToList();
    }

    [McpServerTool(Name = "list_spaces"), Description("List the spaces in a project the current user can access.")]
    public IReadOnlyList<SpaceDto> ListSpaces(
        [Description("The project id.")] int projectId)
    {
        var user = CurrentUser();
        if (!Can(PermTier.Space, AccessLevel.Read)) return [];
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        if (project == null) return [];
        return project.Spaces.Select(RelayMcpProjections.ToDto).ToList();
    }

    [McpServerTool(Name = "list_views"), Description("List the views in a space; create_job targets a view by id.")]
    public IReadOnlyList<ViewDto> ListViews(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId)
    {
        var user = CurrentUser();
        if (!Can(PermTier.Space, AccessLevel.Read)) return [];
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        if (space == null) return [];
        return space.Views.Select(RelayMcpProjections.ToDto).ToList();
    }

    [McpServerTool(Name = "list_jobs"), Description("List the jobs in a space, with their status.")]
    public IReadOnlyList<JobDto> ListJobs(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId)
    {
        var user = CurrentUser();
        if (!Can(PermTier.Job, AccessLevel.Read)) return [];
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
        if (!Can(PermTier.Job, AccessLevel.Read)) return null;
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        var job = project?.FindSpace(spaceId)?.FindJob(jobId);
        return job == null ? null : RelayMcpProjections.ToDetailDto(job);
    }

    private JobLogDto ReadJobLog(int projectId, int spaceId, int jobId, int lines, bool stdout)
    {
        var user = CurrentUser();
        if (!Can(PermTier.Job, AccessLevel.Read)) return new JobLogDto(false, 0, "");
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) return new JobLogDto(false, 0, "");

        int clamped = Math.Clamp(lines, 1, 1000);
        string path = Path.Combine(job.DirectoryPath, stdout ? job.NameStdOut : job.NameStdErr);
        if (!File.Exists(path)) return new JobLogDto(false, 0, "");

        string[] tail = JobTools.ReadLogTail(path, clamped);
        return new JobLogDto(true, tail.Length, string.Join("\n", tail));
    }

    [McpServerTool(Name = "get_job_stdout"), Description("Get the last N lines of a job's stdout (progress-bar lines collapsed to their final state).")]
    public JobLogDto GetJobStdout(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Max lines to return (default 100, max 1000).")] int lines = 100)
        => ReadJobLog(projectId, spaceId, jobId, lines, stdout: true);

    [McpServerTool(Name = "get_job_stderr"), Description("Get the last N lines of a job's stderr (progress-bar lines collapsed to their final state).")]
    public JobLogDto GetJobStderr(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Max lines to return (default 100, max 1000).")] int lines = 100)
        => ReadJobLog(projectId, spaceId, jobId, lines, stdout: false);

    [McpServerTool(Name = "list_job_types"), Description("List all job types (guid, name, and full context-menu category path). Call get_job_type for a type's parameters and ports.")]
    public IReadOnlyList<JobTypeSummaryDto> ListJobTypes()
    {
        _ = CurrentUser(); // require authentication
        return RelayMcpProjections.BuildJobTypeSummaries();
    }

    [McpServerTool(Name = "get_job_type"), Description("Get the parameter schema and input/output ports for a single job type.")]
    public JobTypeDetailDto? GetJobType(
        [Description("The job type guid (from list_job_types).")] string typeGuid)
    {
        _ = CurrentUser(); // require authentication
        return RelayMcpProjections.BuildJobTypeDetail(typeGuid);
    }

    [McpServerTool(Name = "list_queues"), Description("List job queues available for queue_job (local and cluster).")]
    public IReadOnlyList<QueueDto> ListQueues()
    {
        _ = CurrentUser(); // require authentication
        var result = new List<QueueDto> { RelayMcpProjections.ToDto(dataManager.LocalQueue) };
        result.AddRange(dataManager.ClusterQueues.Select(RelayMcpProjections.ToDto));
        return result;
    }

    // ---- Project / space mutation tools -------------------------------------

    [McpServerTool(Name = "create_project"), Description("Create a new project owned by the current user.")]
    public async Task<CreatedDto> CreateProject(
        [Description("Optional project name/alias.")] string? alias = null,
        [Description("Optional emoji shown on the project card, e.g. \"🧊\".")] string? emoji = null)
    {
        var user = CurrentUser();
        Require(PermTier.Project, AccessLevel.EditRun);
        Project? template = null;
        if (!string.IsNullOrWhiteSpace(alias) || !string.IsNullOrWhiteSpace(emoji))
        {
            template = new Project();
            if (!string.IsNullOrWhiteSpace(alias)) template.Alias = alias;
            if (!string.IsNullOrWhiteSpace(emoji)) template.HeroImage = emoji;
        }
        var project = await Invoke(() => dataManager.CreateProject(user, template));
        return new CreatedDto(project.Id, project.Alias);
    }

    [McpServerTool(Name = "delete_project"), Description("Delete a project and everything in it.")]
    public async Task<OkDto> DeleteProject(
        [Description("The project id.")] int projectId)
    {
        var user = CurrentUser();
        Require(PermTier.Project, AccessLevel.Manage);
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        if (project == null) throw new McpException($"Project {projectId} not found.");
        await Invoke(() => dataManager.DeleteProject(project));
        return new OkDto(true);
    }

    [McpServerTool(Name = "create_space"), Description("Create a new space in a project.")]
    public async Task<CreatedDto> CreateSpace(
        [Description("The project id.")] int projectId,
        [Description("Optional space name/alias.")] string? alias = null,
        [Description("Optional emoji shown on the space card, e.g. \"🏖️\".")] string? emoji = null)
    {
        var user = CurrentUser();
        Require(PermTier.Space, AccessLevel.EditRun);
        var project = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId);
        if (project == null) throw new McpException($"Project {projectId} not found.");
        Space? template = null;
        if (!string.IsNullOrWhiteSpace(alias) || !string.IsNullOrWhiteSpace(emoji))
        {
            template = new Space();
            if (!string.IsNullOrWhiteSpace(alias)) template.Alias = alias;
            if (!string.IsNullOrWhiteSpace(emoji)) template.HeroImage = emoji;
        }
        var space = await Invoke(() => dataManager.CreateSpace(user, project, template));
        // Mirror the GUI's space-creation flow: a space needs a default view before jobs can be
        // placed in it (create_job targets a view), and DataManager.CreateSpace makes none.
        var viewEmoji = string.IsNullOrWhiteSpace(emoji) ? "🪟" : emoji;
        await Invoke(() => dataManager.CreateView(user, space, new View { Alias = "View 1", HeroImage = viewEmoji }));
        return new CreatedDto(space.Id, space.Alias);
    }

    [McpServerTool(Name = "delete_space"), Description("Delete a space and everything in it.")]
    public async Task<OkDto> DeleteSpace(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId)
    {
        var user = CurrentUser();
        Require(PermTier.Space, AccessLevel.Manage);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        if (space == null) throw new McpException($"Space {spaceId} not found.");
        await Invoke(() => dataManager.DeleteSpace(user, space));
        return new OkDto(true);
    }

    // ---- Job lifecycle tools ------------------------------------------------

    [McpServerTool(Name = "create_job"), Description("Create a job of the given type in a space's view.")]
    public async Task<CreatedDto> CreateJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The view id (from list_views).")] int viewId,
        [Description("The job type guid (from list_job_types).")] string typeGuid)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.EditRun);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        var view = space?.FindView(viewId);
        if (view == null) throw new McpException($"View {viewId} not found in space {spaceId}.");
        var job = await Invoke(() => dataManager.CreateJob(user, view, typeGuid));
        return new CreatedDto(job.Id, job.AliasOrId);
    }

    [McpServerTool(Name = "configure_job"), Description("Set one or more parameter values on a job (see list_job_types for names).")]
    public async Task<OkDto> ConfigureJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Map of parameter name to value.")] Dictionary<string, JsonElement> parameters)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.EditRun);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");

        IReadOnlyList<(System.Reflection.PropertyInfo Prop, object? Value)> assignments;
        try { assignments = RelayMcpParameterPatch.Resolve(job.GetOriginalType(), parameters); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }

        await Invoke(() => dataManager.UpdateJob(user, job, j =>
        {
            foreach (var (prop, value) in assignments) prop.SetValue(j, value);
        }));
        return new OkDto(true);
    }

    [McpServerTool(Name = "abort_job"), Description("Abort a running or queued job.")]
    public async Task<OkDto> AbortJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.EditRun);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");
        await Invoke(() => dataManager.AbortJob(user, job));
        return new OkDto(true);
    }

    [McpServerTool(Name = "delete_job"), Description("Delete a job.")]
    public async Task<OkDto> DeleteJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.Manage);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");
        await Invoke(() => dataManager.DeleteJob(user, job));
        return new OkDto(true);
    }

    // ---- Edge + queue tools -------------------------------------------------

    [McpServerTool(Name = "connect_jobs"), Description("Connect an output port of one job to an input port of another.")]
    public async Task<OkDto> ConnectJobs(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("Source (upstream) job id.")] int fromJobId,
        [Description("Source output port name.")] string fromPort,
        [Description("Target (downstream) job id.")] int toJobId,
        [Description("Target input port name.")] string toPort)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.EditRun);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        var fromJob = space?.FindJob(fromJobId);
        var toJob = space?.FindJob(toJobId);
        if (space == null || fromJob == null || toJob == null) throw new McpException("Space or job not found.");
        if (!fromJob.PortsOut.TryGetValue(fromPort, out var outPort)) throw new McpException($"Output port '{fromPort}' not found on job {fromJobId}.");
        if (!toJob.PortsIn.TryGetValue(toPort, out var inPort)) throw new McpException($"Input port '{toPort}' not found on job {toJobId}.");
        await Invoke(() => dataManager.CreateEdge(space, outPort, inPort));
        return new OkDto(true);
    }

    [McpServerTool(Name = "disconnect_jobs"), Description("Remove the edge between two job ports.")]
    public async Task<OkDto> DisconnectJobs(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("Source (upstream) job id.")] int fromJobId,
        [Description("Source output port name.")] string fromPort,
        [Description("Target (downstream) job id.")] int toJobId,
        [Description("Target input port name.")] string toPort)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.EditRun);
        var space = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId);
        if (space == null) throw new McpException($"Space {spaceId} not found.");
        var edge = space.Edges.FirstOrDefault(e =>
            e.Source.Job.Id == fromJobId && e.Source.Name == fromPort &&
            e.Target.Job.Id == toJobId && e.Target.Name == toPort);
        if (edge == null) throw new McpException("No such edge.");
        await Invoke(() => dataManager.DeleteEdge(edge));
        return new OkDto(true);
    }

    [McpServerTool(Name = "queue_job"), Description("Queue a job to run. Omit queueId for the local queue; pass a cluster queue id from list_queues.")]
    public async Task<OkDto> QueueJob(
        [Description("The project id.")] int projectId,
        [Description("The space id.")] int spaceId,
        [Description("The job id.")] int jobId,
        [Description("Optional cluster queue id (from list_queues). Omit or -1 for local.")] int? queueId = null)
    {
        var user = CurrentUser();
        Require(PermTier.Job, AccessLevel.EditRun);
        var job = dataManager.GetUserProjects(user).FirstOrDefault(p => p.Id == projectId)?.FindSpace(spaceId)?.FindJob(jobId);
        if (job == null) throw new McpException($"Job {jobId} not found.");

        if (queueId is null or -1)
        {
            await Invoke(() => dataManager.QueueLocalJob(user, job));
        }
        else
        {
            var queue = dataManager.FindClusterQueue(queueId.Value);
            if (queue == null) throw new McpException($"Cluster queue {queueId} not found.");
            await Invoke(() => dataManager.QueueClusterJob(user, job, queue));
        }
        return new OkDto(true);
    }
}
