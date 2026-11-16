using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using Refund.Configuration;
using Refund.DataModel;
using Refund.Jobs.Fs.Warp;
using Refund.JobQueues;
using Refund.JobExecution;
using Refund.Services.Core.DataManager;
using Refund.Services.Warp;

// Load the already-built application, rather than rebuilding or starting a second web host.
string buildDirectory = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(buildDirectory, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
await Seed(args[1]);

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task Seed(string bootstrapFile)
{
    using var document = JsonDocument.Parse(File.ReadAllText(bootstrapFile));
    var config = document.RootElement;
    string state = config.GetProperty("state").GetString()!;
    string scheduler = config.GetProperty("schedulerCommand").GetString()!;
    string username = config.GetProperty("username").GetString()!;
    string password = config.GetProperty("password").GetString()!;
    // Real-worker states carry their settings (data locations included) in the state directory only.
    bool real = config.TryGetProperty("settings", out var overrides) && overrides.ValueKind == JsonValueKind.Object;
    int poolSize = config.TryGetProperty("pool", out var pool) ? pool.GetProperty("size").GetInt32() : 1;
    int perDevice = config.TryGetProperty("pool", out pool) ? pool.GetProperty("perDevice").GetInt32() : 1;
    string label = real ? "GPU test" : "MOCK";
    var paths = new RelayConfiguration
    {
        ProjectsPath = Path.Combine(state, "projects.relay"), UsersPath = Path.Combine(state, "users.relay"),
        QueuesPath = Path.Combine(state, "queues.relay"), TokensPath = Path.Combine(state, "tokens.relay"),
        PatsPath = Path.Combine(state, "pats.relay")
    };
    File.WriteAllText(Path.Combine(state, "relay.json"), JsonSerializer.Serialize(new { Relay = paths }));
    Job.PopulateStatic();
    var manager = new DataManager(paths);
    try
    {
        if (manager.Users.Count != 0 || manager.Projects.Count != 0)
            throw new InvalidOperationException("Bootstrap requires an empty development state; existing collections are never replaced.");
        var user = await manager.CreateUser(new User { Username = username, Name = $"Warp {label} development", Role = UserRole.Admin, PasswordHash = User.HashPassword(password) });
        var project = await manager.CreateProject(user, new Project { Alias = $"{label} · Warp development" });
        var space = await manager.CreateSpace(user, project, new Space { Alias = $"{label} · local workers", RootDirectory = Path.Combine(state, "space") });
        var view = space.Views.FirstOrDefault() ?? await manager.CreateView(user, space, new View { Alias = "Sessions" });
        var queueTemplate = new ClusterQueue
        {
            Alias = $"{label} · local scheduler", QueueType = JobQueueType.GPU, SchedulerType = ClusterScheduler.Custom,
            SubmitJobTemplate = scheduler + " submit '{{script_path_abs}}'",
            StatusJobTemplate = scheduler + " job-status {{job_id}}",
            TerminalStatusJobTemplate = scheduler + " job-status {{job_id}}",
            AbortJobTemplate = scheduler + " cancel {{job_id}}",
            ListJobsTemplate = scheduler + " list",
            JobIdParseRegex = @"receipt=([a-f0-9]+)",
            // Custom scheduler status patterns are literal substrings, not regular expressions.
            JobStatusParseTemplatePending = "PENDING", JobStatusParseTemplateRunning = "RUNNING",
            JobStatusParseTemplateSucceeded = "SUCCEEDED", JobStatusParseTemplateFailed = "FAILED",
            JobStatusParseTemplateCanceled = "CANCELED",
            SubmissionScriptTemplate = "#!/bin/bash\nset -e\n# Development mock workers; this is Relay's normal external pool script.\n{{command}}\n"
        };
        // Exercise the actual Relay parser through the live development scheduler before
        // persisting queue configuration. Unknown/previous-epoch receipts must be terminal.
        var probe = await queueTemplate.ObserveReceipt("bootstrap-probe");
        if (probe.Kind != BackendObservationKind.Canceled)
            throw new InvalidOperationException($"Development scheduler preflight expected Canceled, got {probe.Kind}.");
        var queue = await manager.CreateClusterQueue(queueTemplate);
        var job = (ReadOnlyWarpJob)await manager.CreateJob(user, view, WarpJob.GuidString);
        var settings = real
            ? Overlay(new WarpSettings { Preset = "GPU test · local workers" }, overrides)
            : new WarpSettings
            {
                Preset = "MOCK · local development", InputPath = Path.Combine(state, "input"), InputPattern = "*.mrc",
                Pixel = 2, TargetPixelSize = 2, TotalDose = 40, TrainCount = 4,
                TrainingEpochs = 10, Diameter = 80, Box = 64, PickingEnabled = true, FitThickness = true
            };
        await manager.ApplyWarpSettings(user, job, settings);
        await manager.ConfigureWarpWorkers(user, job, queue.Id, poolSize, perDevice, 2);
        // DataManager has no public flush operation. Flush only its normal repositories;
        // no synthetic processing state, results, worker receipts, or catalog rows are injected.
        foreach (string field in new[] { "_userRepository", "_dataRepository" })
        {
            object repository = typeof(DataManager).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
            repository.GetType().GetMethod("SaveChanges", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(repository, new object?[] { null });
        }
        string route = $"/P{project.Id}/S{space.Id}/V{view.Id}/J{job.Id}";
        File.WriteAllText(Path.Combine(state, "bootstrap-result.json"), JsonSerializer.Serialize(new { route, projectId = project.Id, spaceId = space.Id, jobId = job.Id, queueId = queue.Id }));
        Console.WriteLine($"Initialized empty {label} session " + route);
    }
    finally { await manager.ShutdownAsync(); }
}

static WarpSettings Overlay(WarpSettings defaults, JsonElement overrides)
{
    var merged = JsonSerializer.SerializeToNode(defaults)!.AsObject();
    foreach (var property in overrides.EnumerateObject())
    {
        if (typeof(WarpSettings).GetProperty(property.Name) == null)
            throw new InvalidOperationException($"Unknown Warp setting: {property.Name}");
        merged[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
    }
    return merged.Deserialize<WarpSettings>()!;
}
