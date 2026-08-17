using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Serilog;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues.ReadOnly;
using Refund.Utils;

namespace Refund.JobQueues;

/// <summary>
/// Manages job submission and execution on a remote computing cluster.
/// Supports various cluster schedulers including SLURM, LSF, PBS, SGE and Flux — selected with
/// <see cref="SchedulerType"/> — with configurable templates for each phase of job submission
/// and monitoring.
/// </summary>
/// <remarks>
/// ClusterQueue is a central component in the distributed job execution system:
/// 
/// 1. It's used by QueueRepository to manage remote cluster job submission and monitoring
/// 2. Cluster queues are created through several mechanisms:
///    - Default queues created during initialization with TryAddRealQueue method
///    - Loaded from JSON state during application startup
///    - Created programmatically through CreateClusterQueue
///    cluster schedulers, running actual commands via ExecuteOnCluster
/// 4. The class supports extensive templating for different scheduler types
/// 5. It's exposed as an immutable ReadOnlyClusterQueue via AsReadOnly() method
///    to prevent external code from modifying configuration
///    through the QueueRepository serialization
/// </remarks>
public class ClusterQueue : JobQueue, IPoolQueue
{
    private static readonly ConditionalWeakTable<ClusterQueue, ReadOnlyClusterQueue> ReadOnlyCache = new();
    
    /// <summary>
    /// Tracks jobs that are currently in the staging phase before submission to the cluster
    /// </summary>
    private Dictionary<Job, CancellationTokenSource> StagingJobs = new();
    
    /// <summary>
    /// Tracks jobs that have been staged but have not yet received a cluster job ID
    /// </summary>
    private HashSet<Job> JobsInLimbo = new HashSet<Job>();
    
    /// <summary>
    /// Lock object for thread synchronization when accessing shared collections
    /// </summary>
    private object Sync = new();
    
    /// <summary>
    /// Semaphore to limit concurrent cluster command executions
    /// </summary>
    private readonly SemaphoreSlim _clusterCommandSemaphore = new(Environment.ProcessorCount);

    /// <summary>
    /// Which scheduler this queue talks to. Selects the parsers used to read job IDs and job
    /// states out of scheduler output.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="ClusterScheduler.Slurm"/>, which is what every queue was effectively
    /// getting before this field existed — see the remarks on <see cref="ClusterScheduler"/>.
    /// </remarks>
    [RelayProperty]
    public ClusterScheduler SchedulerType { get; set; } = ClusterScheduler.Slurm;

    /// <summary>Total CPU cores a managed queue may hand out. Ignored unless SchedulerType is Managed.</summary>
    [RelayProperty]
    public int ManagedCores { get; set; } = Environment.ProcessorCount;

    /// <summary>Total memory in GB a managed queue may hand out. Ignored unless SchedulerType is Managed.</summary>
    [RelayProperty]
    public int ManagedMemoryGb { get; set; } = 64;

    /// <summary>Number of GPUs on this host. Ignored unless SchedulerType is Managed.</summary>
    [RelayProperty]
    public int ManagedGpus { get; set; } = 1;

    /// <summary>True when Relay schedules this queue's jobs itself.</summary>
    public bool IsManaged => SchedulerType == ClusterScheduler.Managed;

    /// <summary>
    /// Why this managed queue is not allowed to admit anything, or null in the normal case.
    /// Deliberately not persisted: it is a verdict on the configuration as loaded, recomputed each
    /// startup by <see cref="ManagedQueueRules.DisableDuplicateManagedQueues"/> and cleared as soon
    /// as the queue is edited, since the edit itself goes through the same rules.
    /// </summary>
    public string ManagedDisabledReason { get; set; }

    /// <summary>
    /// Read fresh on every use rather than snapshotted: this object is constructed before
    /// ReadFromJson hydrates the persisted values, and the editor can change them later.
    /// </summary>
    public ResourceTotals ManagedTotals => new(ManagedCores, ManagedMemoryGb, ManagedGpus);

    /// <summary>
    /// The host-wide executor, injected by QueueRepository. Null on a queue that was constructed
    /// outside the repository (templates, copies, tests).
    /// </summary>
    public ManagedExecutor Executor { get; set; }

    /// <summary>
    /// Custom shell executable path for running cluster commands.
    /// When empty, defaults to cmd.exe on Windows or /bin/bash on Unix/Linux.
    /// </summary>
    [RelayProperty]
    public string CustomShell { get; set; } = "";

    /// <summary>
    /// Arguments to pass to the custom shell when executing cluster commands.
    /// Can include {{command}} placeholder that will be replaced with the actual command.
    /// </summary>
    [RelayProperty]
    public string CustomShellArguments { get; set; } = "";

    /// <summary>
    /// Template for executing any command on the cluster.
    /// Should include {{command}} placeholder for the actual command to run.
    /// Used for all communication with the cluster system.
    /// </summary>
    [RelayProperty]
    public string SendCommmandTemplate { get; set; } = "";

    /// <summary>
    /// Template for submitting a job to the cluster scheduler.
    /// Should include {{script_path_abs}} placeholder for the submission script path.
    /// </summary>
    [RelayProperty]
    public string SubmitJobTemplate { get; set; } = "";

    /// <summary>
    /// Template for checking the status of a job on the cluster.
    /// Should include {{job_id}} placeholder for the cluster job ID.
    /// </summary>
    [RelayProperty]
    public string StatusJobTemplate { get; set; } = "";

    /// <summary>
    /// Template for aborting/canceling a job on the cluster.
    /// Should include {{job_id}} placeholder for the cluster job ID.
    /// </summary>
    [RelayProperty]
    public string AbortJobTemplate { get; set; } = "";

    /// <summary>
    /// Regular expression for extracting the job ID from the cluster scheduler output; the ID is
    /// taken from the first capture group. Used only when <see cref="SchedulerType"/> is
    /// <see cref="ClusterScheduler.Custom"/>.
    /// </summary>
    [RelayProperty]
    public string JobIdParseRegex { get; set; } = "";

    /// <summary>
    /// String pattern that indicates a job is pending in the cluster queue.
    /// Used only when <see cref="SchedulerType"/> is <see cref="ClusterScheduler.Custom"/>.
    /// </summary>
    [RelayProperty]
    public string JobStatusParseTemplatePending { get; set; } = "";

    /// <summary>
    /// String pattern that indicates a job is running on the cluster.
    /// Used only when <see cref="SchedulerType"/> is <see cref="ClusterScheduler.Custom"/>.
    /// </summary>
    [RelayProperty]
    public string JobStatusParseTemplateRunning { get; set; } = "";

    /// <summary>
    /// String pattern that indicates a job has failed on the cluster.
    /// Used only when <see cref="SchedulerType"/> is <see cref="ClusterScheduler.Custom"/>.
    /// </summary>
    [RelayProperty]
    public string JobStatusParseTemplateFailed { get; set; } = "";

    /// <summary>
    /// Template for generating the job submission script sent to the cluster.
    /// Can include placeholders for job-specific values, custom variables,
    /// and conditional blocks for different job types.
    /// </summary>
    /// <remarks>
    /// This template is used extensively during job submission to generate cluster-specific
    /// job scripts. It supports a rich templating system including:
    /// 
    /// 1. Simple variable substitution with {{variable_name}} syntax
    /// 2. Conditional module blocks with {{module_name}} ... {{/module_name}}
    /// 3. Custom variables defined in the CustomVariables dictionary
    /// 
    /// It can be modified through the UI in the Relay application, where users can 
    /// programmatically add variables and module blocks through QueueEditor.
    /// </remarks>
    [RelayProperty]
    public string SubmissionScriptTemplate { get; set; } = "";

    /// <summary>
    /// Command that returns one active cluster job ID per line for the submitting user.
    /// Required when this queue is used as a pool queue for WarpTools GPU jobs.
    /// Example SLURM: squeue -u $USER -h -o "%i"
    /// </summary>
    [RelayProperty]
    public string ListJobsTemplate { get; set; } = "";

    /// <summary>
    /// Command to cancel multiple jobs in one call.
    /// Supports {{job_ids}} placeholder (space-separated IDs).
    /// Required when this queue is used as a pool queue for WarpTools GPU jobs.
    /// Example SLURM: scancel {{job_ids}}
    /// </summary>
    [RelayProperty]
    public string CancelManyJobsTemplate { get; set; } = "";

    /// <summary>
    /// Custom variables that can be used in the submission script template.
    /// Keys are variable names, values are tuples containing description and default value.
    /// These can be overridden during job submission.
    /// </summary>
    /// <remarks>
    /// This dictionary is used to store user-defined variables for cluster job scripts.
    /// It's exposed in the UI via the QueueEditor, where users can:
    /// 
    /// 1. Add new variables with default values and descriptions
    /// 2. Remove existing variables
    /// 3. Modify variable values during job submission
    /// 
    /// The collection is serialized to JSON when saving queue state and deserialized
    /// when loading. It's also exposed in read-only form through the ReadOnlyClusterQueue
    /// wrapper.
    /// </remarks>
    public Dictionary<string, (string description, string defaultValue)> CustomVariables { get; set; } = new();

    /// <summary>
    /// Parser functions for extracting job IDs from scheduler output, one per scheduler.
    /// The one used is selected by <see cref="SchedulerType"/>; parsers return null when the
    /// output holds no ID they recognise.
    /// </summary>
    private Dictionary<ClusterScheduler, Func<string, string>> JobIdParsers;

    /// <summary>
    /// Parser functions for determining job status from scheduler output, one per scheduler.
    /// The one used is selected by <see cref="SchedulerType"/>; parsers return null when the
    /// output holds no state they recognise.
    /// </summary>
    private Dictionary<ClusterScheduler, Func<string, ClusterJobStatus?>> JobStatusParsers;

    /// <summary>
    /// Initializes a new instance of the ClusterQueue class with job update callback.
    /// Sets up parsers for different cluster schedulers to extract job IDs and status information.
    /// </summary>
    /// <param name="jobUpdateCallback">Callback function to update job state in the data model</param>
    /// <remarks>
    /// This constructor is called in several key contexts:
    /// 
    /// 1. During system initialization in QueueRepository.TryAddRealQueue() to create
    ///    default queues with scheduler-specific templates (e.g., LSF, SLURM)
    /// 2. When loading queue definitions from saved state via QueueRepository.LoadState()
    /// 3. When creating new cluster queues programmatically via QueueRepository.CreateClusterQueue()
    /// 
    /// The constructor initializes the scheduler-specific parsers for different cluster
    /// types, setting up regular expressions and patterns to extract job IDs and status
    /// information from scheduler command output.
    /// </remarks>
    public ClusterQueue(Action<Job, Action<Job>> jobUpdateCallback) : base(jobUpdateCallback)
    {
        #region Scheduler-specific parsers

        JobIdParsers = new()
        {
            {
                ClusterScheduler.Slurm,
                (output) =>
                {
                    var match = Regex.Match(output, @"Submitted batch job (\d+)");
                    return match.Success ? match.Groups[1].Value : null;
                }
            },
            {
                ClusterScheduler.Lsf,
                (output) =>
                {
                    var match = Regex.Match(output, @"Job <(\d+)> is submitted");
                    return match.Success ? match.Groups[1].Value : null;
                }
            },
            {
                ClusterScheduler.Pbs,
                (output) =>
                {
                    var match = Regex.Match(output, @"(\d+)\.(\w+)");
                    return match.Success ? match.Value : null;
                }
            },
            {
                ClusterScheduler.Sge,
                (output) =>
                {
                    var match = Regex.Match(output, @"Your job (\d+)");
                    return match.Success ? match.Groups[1].Value : null;
                }
            },
            {
                ClusterScheduler.Flux,
                (output) =>
                {
                    // flux batch prints the job ID alone on stdout, in whichever encoding the submit
                    // template asked for: F58 ("ƒ2ELdc8V", or "f2ELdc8V" under FLUX_F58_FORCE_ASCII)
                    // or decimal. Rather than enumerate encodings — flux also speaks hex, dothex and
                    // words — accept any single bare token. Requiring exactly one token still rejects
                    // error text, so a misconfigured submit command fails loudly here instead of
                    // handing the daemon a job ID that no status query will ever match.
                    string trimmed = output.Trim();
                    return trimmed.Length > 0 && !trimmed.Any(char.IsWhiteSpace) ? trimmed : null;
                }
            },
            {
                ClusterScheduler.Custom,
                (output) =>
                {
                    if (string.IsNullOrEmpty(JobIdParseRegex))
                        return null;

                    var match = Regex.Match(output, JobIdParseRegex);
                    return match.Success ? match.Groups[1].Value : null;
                }
            }
        };

        JobStatusParsers = new()
        {
            {
                ClusterScheduler.Slurm,
                (output) =>
                {
                    // Check for single-character state codes (default squeue output)
                    if (output.Contains(" PD ") || output.Contains("PENDING")) return ClusterJobStatus.Pending;
                    if (output.Contains(" R ") || output.Contains("RUNNING")) return ClusterJobStatus.Running;
                    if (output.Contains(" CD ") || output.Contains("COMPLETED")) return ClusterJobStatus.Finished;
                    if (output.Contains(" F ") || output.Contains(" CA ") || output.Contains(" TO ") ||
                        output.Contains("FAILED") || output.Contains("CANCELLED") || output.Contains("TIMEOUT")) return ClusterJobStatus.Failed;
                    if (output.Contains(" CG ") || output.Contains("COMPLETING")) return ClusterJobStatus.Running; // Still running

                    // Decline rather than reporting Unknown. Returning a value here used to
                    // short-circuit the try-each-parser loop this dictionary was iterated with,
                    // which made every other parser — including the queue's own custom patterns —
                    // unreachable. Callers turn a declined parse into Unknown themselves.
                    return null;
                }
            },
            {
                ClusterScheduler.Lsf,
                (output) =>
                {
                    if (output.Contains("PEND")) return ClusterJobStatus.Pending;
                    if (output.Contains("RUN")) return ClusterJobStatus.Running;
                    if (output.Contains("EXIT")) return ClusterJobStatus.Failed;
                    if (output.Contains("DONE")) return ClusterJobStatus.Finished;

                    return null;
                }
            },
            {
                ClusterScheduler.Pbs,
                (output) =>
                {
                    if (output.Contains(" Q ")) return ClusterJobStatus.Pending;
                    if (output.Contains(" R ")) return ClusterJobStatus.Running;
                    if (output.Contains(" C ")) return ClusterJobStatus.Failed;

                    return null;
                }
            },
            {
                ClusterScheduler.Sge,
                (output) =>
                {
                    if (output.Contains(" qw ")) return ClusterJobStatus.Pending;
                    if (output.Contains(" r ")) return ClusterJobStatus.Running;
                    if (output.Contains(" Eqw ")) return ClusterJobStatus.Failed;

                    return null;
                }
            },
            {
                ClusterScheduler.Flux,
                (output) =>
                {
                    // flux jobs -no "{status}" prints exactly one token per job; {status_abbrev}
                    // prints its short form. Match the whole token: the abbreviations overlap by
                    // prefix ("C" for CLEANUP is a prefix of both "CD" and "CA"), so a Contains-style
                    // test would read a finished job as still running.
                    return output.Trim() switch
                    {
                        "DEPEND" or "PRIORITY" or "SCHED" or
                        "D" or "P" or "S"
                            => ClusterJobStatus.Pending,

                        // CLEANUP is where a job flushes its output and releases resources. It is
                        // brief but every job passes through it, so a poll can easily land there;
                        // anything other than Running here makes HandleRunningState finalise the
                        // job as Failed on its way to succeeding.
                        "RUN" or "CLEANUP" or
                        "R" or "C"
                            => ClusterJobStatus.Running,

                        "COMPLETED" or "CD"
                            => ClusterJobStatus.Finished,

                        // Flux spells it CANCELED, with one L.
                        "FAILED" or "CANCELED" or "TIMEOUT" or
                        "F" or "CA" or "TO"
                            => ClusterJobStatus.Failed,

                        _ => null
                    };
                }
            },
            {
                ClusterScheduler.Custom,
                (output) =>
                {
                    // Treat empty templates as "not configured" — otherwise string.Contains("")
                    // matches every line and misclassifies all output as Pending.
                    if (string.IsNullOrEmpty(JobStatusParseTemplatePending) ||
                        string.IsNullOrEmpty(JobStatusParseTemplateRunning) ||
                        string.IsNullOrEmpty(JobStatusParseTemplateFailed))
                        return null;

                    if (output.Contains(JobStatusParseTemplatePending)) return ClusterJobStatus.Pending;
                    if (output.Contains(JobStatusParseTemplateRunning)) return ClusterJobStatus.Running;
                    if (output.Contains(JobStatusParseTemplateFailed)) return ClusterJobStatus.Failed;

                    return null;
                }
            }
        };

        #endregion
    }
    
    /// <summary>
    /// Submits a job to the cluster scheduler for execution.
    /// This method prepares the job directory, generates the submission script,
    /// and submits it to the cluster using the configured template.
    /// </summary>
    /// <param name="job">The job to submit for execution</param>
    /// <param name="customValues">Optional dictionary of custom variable values to use in the submission script</param>
    /// <exception cref="Exception">Thrown if the job is already staging or if job ID cannot be parsed from cluster output</exception>
    /// <remarks>
    /// This method is called from QueueRepository when jobs are submitted for execution,
    /// typically under lock (_saveLock). It first calls base.SubmitJob() to perform any 
    /// common job submission operations defined in JobQueue.
    /// 
    /// The same pattern is followed by LocalQueue.SubmitJob, ensuring consistent job 
    /// submission behavior across different queue types. This allows the QueueRepository
    /// to handle jobs uniformly regardless of execution environment.
    /// 
    /// generates and submits a job script to an external scheduler, then parses the 
    /// returned cluster job ID for subsequent status tracking.
    /// </remarks>
    public override void SubmitJob(Job job, Dictionary<string, string> customValues = null)
    {
        base.SubmitJob(job);
        
        lock (Sync)
        {
            if (StagingJobs.ContainsKey(job))
                throw new Exception($"Job {job.Id} is already staging!");
            
            CancellationTokenSource cts = new();
            StagingJobs.Add(job, cts);

            // Deliberately *not* Task.Run(..., cts.Token). Passing the token as the task's
            // scheduling token means a cancel landing between the task being queued and its
            // delegate starting stops the delegate running at all — including its finally. The job
            // then stayed in StagingJobs forever, and every requeue threw "already staging", so an
            // abort in that window made the job permanently unrunnable. Cancellation is observed
            // inside the delegate instead, where the cleanup below always runs.
            Task.Run(async () =>
            {
                try
                {
                    // The window the scheduling token used to swallow: an abort that arrived while
                    // this task sat in the thread pool queue.
                    cts.Token.ThrowIfCancellationRequested();

                    JobUpdateCallback(job, j =>
                    {
                        j.DirectoryName = j.Id.ToString();
                        j.Status = JobStatus.Staging;
                    });

                    // Before the work, not only after it. Script preparation deletes and recreates
                    // the job directory and can throw for reasons of its own, and an abort that
                    // was already pending must not come back as one of those failures.
                    cts.Token.ThrowIfCancellationRequested();

                    string scriptPath = await PrepareAndWriteScript(job, customValues);
                    cts.Token.ThrowIfCancellationRequested();

                    // Managed: there is no scheduler to hand the script to. The script itself is
                    // the same one a cluster queue would submit, minus the #SBATCH/#FLUX header.
                    if (IsManaged)
                    {
                        var process = Executor.Launch(job, scriptPath, job.RunDirectory);
                        await job.WriteToLifecycleLog(
                            $"Launched locally as pid {process.Pid} on GPUs " +
                            $"[{string.Join(",", Executor.GpuIndicesFor(job))}]");

                        JobUpdateCallback(job, j => { j.ClusterJobId = process.Pid.ToString(); });
                        return;
                    }

                    EnterLimbo(job);

                    await job.WriteToLifecycleLog($"Submitting script: {scriptPath}");

                    string rawOutput = null;
                    string jobId = await SubmitScript(scriptPath, o => rawOutput = o);
                    await job.WriteToLifecycleLog(rawOutput);
                    await job.WriteToLifecycleLog($"Parsed cluster job ID: {jobId}");

                    JobUpdateCallback(job, j => { j.ClusterJobId = jobId; });
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // An explicit abort, not a staging failure. Rewritten to Failed by the generic
                    // catch below, a job the user deliberately stopped ended up indistinguishable
                    // from one whose script could not be written, with a stack trace in error.txt.
                    await job.WriteToLifecycleLog(
                        $"Job {job.Id} was aborted before it reached the cluster");
                    JobUpdateCallback(job, j => j.Status = JobStatus.Aborted);
                }
                catch (Exception exc)
                {
                    await job.WriteToErrorLog($"Job {job.Id} cancelled before it went to cluster:\n{exc}");
                    JobUpdateCallback(job, j => j.Status = JobStatus.Failed);
                }
                finally
                {
                    // Reached on every path now, cancellation included. It is the only thing that
                    // lets the job be queued again.
                    SettleStaging(job);
                }
            });
        }
    }

    /// <summary>The script is written and the job is now waiting for the scheduler to name it.</summary>
    internal void EnterLimbo(Job job)
    {
        lock (Sync)
            JobsInLimbo.Add(job);
    }

    internal bool IsInLimbo(Job job)
    {
        lock (Sync)
            return JobsInLimbo.Contains(job);
    }

    /// <summary>
    /// Drops both pieces of per-run staging bookkeeping, on every exit path.
    /// </summary>
    /// <remarks>
    /// The limbo removal is unconditional, and has to be: the guard that used to stand here was
    /// inverted — it removed the job only when the set did <em>not</em> contain it — so limbo was
    /// never cleared for any queue, SLURM and Flux included. <see cref="HashSet{T}.Remove"/> is
    /// already a no-op for a job that never entered limbo, so no guard is wanted at all. A stale
    /// entry is what <see cref="AbortJob"/> then waited on forever.
    /// </remarks>
    internal void SettleStaging(Job job)
    {
        lock (Sync)
        {
            StagingJobs.Remove(job);
            JobsInLimbo.Remove(job);
        }
    }

    /// <summary>
    /// How long an abort waits for a staged job's cluster job id to appear. Generous, because the
    /// id comes back from a submission command that may be an ssh round trip to a loaded
    /// scheduler — but bounded, because <see cref="AbortJob"/> runs on the daemon thread and an
    /// unbounded wait there stops every other job on the host.
    /// </summary>
    internal static readonly TimeSpan LimboJobIdWait = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan LimboPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Blocks until the job leaves limbo or its cluster job id appears, whichever happens first,
    /// and gives up after <paramref name="timeout"/>. True only when there is an id to abort with.
    /// </summary>
    /// <remarks>
    /// A job whose staging failed after it entered limbo never gets an id at all, so the exit on
    /// timeout is not belt-and-braces: it is the only thing that ends the wait if some future path
    /// leaves an entry behind again.
    /// </remarks>
    internal bool WaitForClusterJobId(Job job, TimeSpan timeout)
    {
        long start = Stopwatch.GetTimestamp();

        while (IsInLimbo(job) && string.IsNullOrWhiteSpace(job.ClusterJobId))
        {
            if (Stopwatch.GetElapsedTime(start) >= timeout)
                break;

            Thread.Sleep(LimboPollInterval < timeout ? LimboPollInterval : timeout);
        }

        return !string.IsNullOrWhiteSpace(job.ClusterJobId);
    }

    /// <summary>
    /// Prepares the submission script for a job and writes it to disk.
    /// Returns the absolute path to the written script.
    /// </summary>
    private async Task<string> PrepareAndWriteScript(Job job, Dictionary<string, string> customValues = null)
    {
        job.DirectoryName = job.Id.ToString();

        if (Directory.Exists(job.DirectoryPath) &&
            !string.IsNullOrWhiteSpace(job.DirectoryName) &&
            !Path.GetFullPath(job.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 .Equals(Path.GetFullPath(job.Space.RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                         StringComparison.OrdinalIgnoreCase))
            Directory.Delete(job.DirectoryPath, true);

        Directory.CreateDirectory(job.DirectoryPath);
        Directory.CreateDirectory(job.RelayResultsDirectoryPath);

        job.Stage();

        string scriptPath = Path.Combine(job.DirectoryPath, "submit.sh");

        Dictionary<string, string> arguments = job.ComposeCommandArguments();
        string commandName = job.CommandName;

        StringBuilder jobCommand = new StringBuilder();
        jobCommand.AppendLine($"cd {job.RunDirectory}\n");
        jobCommand.Append(job.CommandPrefix);
        jobCommand.Append($"{commandName} {JobTools.ComposeArgumentString(arguments)}");
        jobCommand.AppendLine(job.CommandSuffix);

        string script = ProcessSubmissionScript(
            SubmissionScriptTemplate
                .ReplaceRegex("{{\\s*command\\s*}}", jobCommand.ToString())
                .ReplaceRegex("{{\\s*job_id\\s*}}", job.Id.ToString()),
            job.GetResourceValues(),
            job.RequiredModules,
            customValues);

        await File.WriteAllTextAsync(scriptPath, script);
        await job.WriteToLifecycleLog($"Written following submission script to {scriptPath}:\n\n{script}\n\n");

        return scriptPath;
    }

    /// <summary>
    /// Submits a pre-written script to the cluster scheduler.
    /// Returns the cluster job ID assigned by the scheduler.
    /// The raw scheduler output is returned via the optional out-style callback for logging.
    /// </summary>
    public async Task<string> SubmitScript(string scriptPath, Action<string> onRawOutput = null)
    {
        string clusterCommand = SubmitJobTemplate.ReplaceRegex("{{\\s*script_path_abs\\s*}}", scriptPath);
        string output = await ExecuteOnCluster(clusterCommand);
        onRawOutput?.Invoke(output);
        return ParseClusterJobId(output);
    }

    /// <summary>
    /// Explicit IPoolQueue implementation. The public SubmitScript carries an optional
    /// onRawOutput callback (Task 1's raw-output logging), so its signature does not
    /// satisfy the parameterless-callback interface contract directly; this forwards to it.
    /// </summary>
    Task<string> IPoolQueue.SubmitScript(string scriptPath) => SubmitScript(scriptPath);

    /// <summary>
    /// Builds and writes a worker submission script without requiring a full Job object.
    /// Used by WorkerPool to prepare a reusable script before any workers are submitted.
    /// Returns the absolute path to the written script file.
    /// </summary>
    public string BuildWorkerScript(
        string command,
        Dictionary<string, string> resourceValues,
        string[] requiredModules,
        string scriptPath)
    {
        // The caller (WorkerPool) supplies every template variable in resourceValues — including
        // job_id, std_out/std_err, n_processes — so no separate {{job_id}} substitution is needed
        // here. Any template variable NOT in resourceValues is stripped to empty by
        // ProcessSubmissionScript's unmatched-tag cleanup, which can yield a malformed directive.
        string script = ProcessSubmissionScript(
            SubmissionScriptTemplate.ReplaceRegex("{{\\s*command\\s*}}", command),
            resourceValues,
            requiredModules);

        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    /// <summary>
    /// Processes a cluster submission script template by replacing placeholders with actual values
    /// and handling conditional blocks based on job requirements.
    /// </summary>
    /// <param name="scriptTemplate">The template for the submission script</param>
    /// <param name="resourceValues">Job resource values used to substitute placeholders</param>
    /// <param name="requiredModules">Modules required by the job, used to resolve conditional blocks</param>
    /// <param name="customValues">Optional dictionary of custom variable values to override defaults</param>
    /// <returns>The processed script with all placeholders replaced and conditional blocks resolved</returns>
    /// <remarks>
    /// The template can include:
    /// - Simple placeholders like {{variable_name}} that are replaced with values
    /// - Module-specific blocks like {{module_name}} content {{/module_name}} that are included only if
    ///   the job requires the specified module
    /// - Custom variables defined in the CustomVariables dictionary
    /// - Job-specific resource values from the job's GetResourceValues method
    /// </remarks>
    protected string ProcessSubmissionScript(
        string scriptTemplate,
        Dictionary<string, string> resourceValues,
        string[] requiredModules,
        Dictionary<string, string> customValues = null)
    {
        string result = scriptTemplate;

        // Replace resource values first
        foreach (var kvp in resourceValues)
        {
            // Create a regex pattern that allows for any number of spaces between braces and name.
            // Replace literally (MatchEvaluator) so "$" in a value isn't treated as a substitution.
            string pattern = $"{{{{\\s*{Regex.Escape(kvp.Key)}\\s*}}}}";
            result = Regex.Replace(result, pattern, _ => kvp.Value);
        }

        // Replace custom variables
        foreach (var kvp in CustomVariables)
        {
            // Create a regex pattern that allows for any number of spaces between braces and name
            string pattern = $"{{{{\\s*{Regex.Escape(kvp.Key)}\\s*}}}}";
            string value = (customValues?.GetValueOrDefault(kvp.Key) ?? kvp.Value.defaultValue) ?? "";

            result = Regex.Replace(result, pattern, _ => value);
        }

        // Process module-dependent blocks
        var moduleBlocks = new List<(int start, int end, string module)>();
        int position = 0;

        while (position < result.Length)
        {
            int blockStart = result.IndexOf("{{", position);
            if (blockStart == -1)
                break;

            int blockEnd = result.IndexOf("}}", blockStart);
            if (blockEnd == -1)
                break;

            string moduleName = result.Substring(blockStart + 2, blockEnd - blockStart - 2).Trim();
            
            // Check if this is a closing tag
            if (moduleName.StartsWith("/"))
            {
                string closingModule = moduleName[1..];
                var openBlock = moduleBlocks.FindLast(b => b.module == closingModule);
                
                if (openBlock.module != null)
                {
                    // If module is required by job, keep the block (without tags)
                    if (requiredModules.Contains(closingModule))
                    {
                        result = result.Remove(blockStart, blockEnd - blockStart + 2);  // Remove closing tag
                        result = result.Remove(openBlock.start, openBlock.end - openBlock.start + 2);  // Remove opening tag
                        position = blockStart - (openBlock.end - openBlock.start + 2);
                    }
                    else // Remove the entire block including content
                    {
                        result = result.Remove(openBlock.start, blockEnd - openBlock.start + 2);
                        position = openBlock.start;
                    }
                    
                    moduleBlocks.RemoveAt(moduleBlocks.Count - 1);
                    continue;
                }
            }
            else
            {
                moduleBlocks.Add((blockStart, blockEnd, moduleName));
            }

            position = blockEnd + 2;
        }
        
        // Finally, get rid of any remaining tags
        foreach (var block in moduleBlocks)
            result = result.Remove(block.start, block.end - block.start + 2);

        return result;
    }

    /// <summary>
    /// Checks the current status of a job on the cluster.
    /// </summary>
    /// <param name="job">The job to check status for</param>
    /// <returns>The current status of the job on the cluster</returns>
    /// <remarks>
    /// This method determines job status by:
    /// 1. Checking if the job is still in staging phase
    /// 2. For jobs submitted to the cluster, querying the cluster scheduler
    /// 3. Parsing the output using the appropriate scheduler-specific parser
    /// 
    /// This method is called extensively by QueueRepository during job monitoring in
    /// handler methods like HandleStagingState, HandleRunningState, and HandleAbortingState.
    /// These handlers use the returned ClusterJobStatus to determine how to update the
    /// job's status in the data model and trigger appropriate state transitions.
    /// 
    /// allowing the QueueRepository to query job status uniformly regardless of the queue type.
    /// </remarks>
    public override async Task<(ClusterJobStatus status, string output)> CheckStatus(Job job)
    {
        lock (Sync)
            if (StagingJobs.ContainsKey(job))
                return (ClusterJobStatus.Pending, "");

        // The executor is the only authority on a managed job. No executor means nothing is
        // running this job and nothing ever will, which is Failed, not Unknown.
        if (IsManaged)
            return (Executor?.GetStatus(job) ?? ClusterJobStatus.Failed, "");

        if (string.IsNullOrWhiteSpace(job.ClusterJobId))
            return (ClusterJobStatus.Unknown, "");
        
        string command = StatusJobTemplate.ReplaceRegex("{{\\s*job_id\\s*}}", job.ClusterJobId);
        string output = await ExecuteOnCluster(command);

        return (ParseClusterJobStatus(output), output);
    }

    /// <summary>
    /// Aborts/cancels a job running on the cluster.
    /// </summary>
    /// <param name="job">The job to abort</param>
    /// <remarks>
    /// This method handles aborting jobs in different states:
    /// 1. For jobs still in staging phase, it cancels the staging task
    /// 2. For jobs in limbo (staged but waiting for cluster job ID), it waits — for a bounded time,
    ///    see <see cref="LimboJobIdWait"/> — for the ID to be assigned
    /// 3. For jobs running on the cluster, it sends an abort command to the cluster scheduler
    /// </remarks>
    public override void AbortJob(Job job)
    {
        base.AbortJob(job);

        // Before the staging/limbo dance: a managed job has no cluster job ID to wait for, and
        // AbortJobTemplate is meaningless for it. Kill is idempotent and safe on an unknown job.
        if (IsManaged)
        {
            lock (Sync)
                if (StagingJobs.TryGetValue(job, out var staging) && !staging.IsCancellationRequested)
                    staging.Cancel();

            Executor?.Kill(job);
            return;
        }

        lock (Sync)
            if (StagingJobs.ContainsKey(job))
            {
                if (!StagingJobs[job].IsCancellationRequested)
                    StagingJobs[job].Cancel();

                if (!JobsInLimbo.Contains(job))
                    return;
            }

        // Bounded, and locked. This runs on the daemon thread, so a wait that cannot end stops
        // every other job on the host, not just this one.
        if (!WaitForClusterJobId(job, LimboJobIdWait))
        {
            // No id ever arrived: staging failed after the job entered limbo, or the submission
            // itself threw. There is nothing on the scheduler to cancel, and substituting an empty
            // id into AbortJobTemplate would run a bare scancel/flux-cancel instead.
            Log.ForContext<ClusterQueue>().Warning(
                "Job {JobId} was aborted before the cluster gave it a job ID; there is nothing to " +
                "cancel on the scheduler.", job.Id);
            return;
        }

        Task.Run(async () =>
        {
            string command = AbortJobTemplate.ReplaceRegex("{{\\s*job_id\\s*}}", job.ClusterJobId);
            await job.WriteToLifecycleLog($"\nAborting job {job.Id} with command: {command}");
            
            string output = await ExecuteOnCluster(command);
            await job.WriteToLifecycleLog(output);
        });
    }
    
    /// <summary>
    /// Returns the currently active cluster jobs (id → status) by executing ListJobsTemplate.
    /// Throws if ListJobsTemplate is not configured.
    /// </summary>
    /// <remarks>
    /// The list command should print one job per line with the job ID as the first
    /// whitespace-separated token and the scheduler state somewhere on the same line, e.g.
    /// SLURM: <c>squeue -u $USER -h -o "%i %T"</c>. The state token is classified with the same
    /// per-scheduler parsers used for single-job status, so the user-configurable status patterns
    /// (JobStatusParseTemplate*) apply here too. A line carrying only an ID (no recognizable
    /// state) maps to <see cref="ClusterJobStatus.Unknown"/>, which callers treat as alive-pending.
    /// </remarks>
    public async Task<Dictionary<string, ClusterJobStatus>> ListActiveJobs()
    {
        if (string.IsNullOrWhiteSpace(ListJobsTemplate))
            throw new InvalidOperationException(
                $"Queue \"{Alias}\" has no ListJobsTemplate configured. " +
                "Add a command that prints one active job per line as \"<id> <state>\" " +
                "(e.g. squeue -u $USER -h -o \"%i,%T\"). Use a space-free format (comma, not " +
                "\"%i %T\") so the column survives a remote shell hop such as ssh, which re-splits " +
                "quoted arguments.");

        string output = await ExecuteOnCluster(ListJobsTemplate);
        var parsed = ParseActiveJobs(output);

        // Boundary diagnostic: the exact command sent and the raw scheduler output, plus the
        // state histogram. If the histogram is all-Unknown while the raw output clearly carries
        // RUNNING/PENDING, parsing is at fault; if the raw output itself has no state column,
        // the configured template (or quote handling in the command pipeline) is at fault.
        Log.ForContext<ClusterQueue>().Debug(
            "ListActiveJobs queue=\"{Alias}\" command={Command} parsed={Count} states={States}\n" +
            "--- raw output ---\n{RawOutput}\n--- end raw output ---",
            Alias, ListJobsTemplate, parsed.Count,
            string.Join(", ", parsed.GroupBy(kv => kv.Value).Select(g => $"{g.Key}={g.Count()}")),
            output);

        return parsed;
    }

    /// <summary>
    /// Parses scheduler stdout (one job per line, ID first, state following) into an id → status map.
    /// Handles both \n and \r\n line endings; blank lines are ignored. Lines with only an ID classify
    /// as Unknown.
    /// </summary>
    internal Dictionary<string, ClusterJobStatus> ParseActiveJobs(string output)
    {
        var result = new Dictionary<string, ClusterJobStatus>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Accept whitespace OR comma between the ID and state. A comma lets the list command use
            // a space-free format (e.g. squeue -o "%i,%T") that survives shell-quote mangling in the
            // command pipeline, where a "%i %T" with an embedded space can lose its state column.
            var tokens = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            string id   = tokens[0];
            string rest = tokens.Length > 1 ? string.Join(' ', tokens.Skip(1)) : "";
            result[id]  = ClassifyState(rest);
        }

        return result;
    }

    /// <summary>
    /// Classifies a scheduler state token (e.g. "RUNNING", "PD", "RUN") using the same parser as
    /// single-job status, so the list and status paths can never disagree about what a state means.
    /// The token is space-padded before matching so short codes like " R " / " PD " still hit the
    /// SLURM parser's word-boundary checks. Returns Unknown when the state isn't recognised.
    /// </summary>
    private ClusterJobStatus ClassifyState(string stateText) => ParseClusterJobStatus($" {stateText} ");

    /// <summary>
    /// Cancels all provided cluster job IDs in a single scheduler call.
    /// Throws if CancelManyJobsTemplate is not configured.
    /// </summary>
    public async Task CancelJobs(IEnumerable<string> jobIds)
    {
        if (string.IsNullOrWhiteSpace(CancelManyJobsTemplate))
            throw new InvalidOperationException(
                $"Queue \"{Alias}\" has no CancelManyJobsTemplate configured. " +
                "Add a command using {{job_ids}} placeholder (e.g. scancel {{job_ids}}).");

        var ids = jobIds.ToList();
        if (ids.Count == 0)
            return;

        string command = CancelManyJobsTemplate.ReplaceRegex(
            "{{\\s*job_ids\\s*}}", string.Join(" ", ids));
        await ExecuteOnCluster(command);
    }

    /// <summary>
    /// Executes a command on the cluster and returns the output.
    /// Uses semaphore throttling to limit concurrent cluster operations.
    /// </summary>
    /// <param name="command">The command to execute on the cluster</param>
    /// <returns>The standard output of the command</returns>
    /// <exception cref="Exception">Thrown when the command execution fails with a non-zero exit code</exception>
    /// <remarks>
    /// This method handles:
    /// 1. Using the proper shell based on the platform or custom configuration
    /// 2. Setting up the process with proper redirections
    /// 3. Capturing and returning the command output
    /// 4. Throwing an exception with error details if the command fails
    /// 5. Throttling concurrent cluster operations to prevent resource exhaustion
    /// </remarks>
    private async Task<string> ExecuteOnCluster(string command)
    {
        // Acquire semaphore to limit concurrent cluster operations
        var acquired = await _clusterCommandSemaphore.WaitAsync(TimeSpan.FromSeconds(30));
        if (!acquired)
        {
            throw new Exception($"Could not acquire cluster command semaphore within 30 seconds for command: {command}");
        }
        
        try
        {
            return await ExecuteClusterCommandInternal(command);
        }
        finally
        {
            _clusterCommandSemaphore.Release();
        }
    }
    
    /// <summary>
    /// Internal method that performs the actual cluster command execution
    /// </summary>
    /// <param name="command">The command to execute</param>
    /// <returns>The command output</returns>
    private async Task<string> ExecuteClusterCommandInternal(string command)
    {
        string fullCommand = SendCommmandTemplate.ReplaceRegex("{{\\s*command\\s*}}", command);
        var attempts = 0;
        var maxAttempts = 20;
        var timeoutMinutes = 2;
        string lastError = null;

        while (attempts < maxAttempts)
        {
            using var process = new Process();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes)); // Timeout per attempt
            
            try
            {
                // How the command reaches the shell:
                //
                // The default Unix path passes ["-c", fullCommand] via ArgumentList, NOT a single
                // Arguments string. ArgumentList hands argv straight to execve with no re-parsing,
                // so fullCommand reaches /bin/bash verbatim and the admin's quoting behaves exactly
                // as in an interactive shell. (The old `-c "{fullCommand}"` string was parsed twice
                // — once by .NET's Arguments→argv parser, once by bash — which silently collapsed
                // embedded double quotes, e.g. turning -o "%i %T" into -o %i with %T dropped.)
                //
                // CustomShell keeps the string-template form: the admin owns that quoting via
                // CustomShellArguments. Windows cmd.exe keeps the string form too (cmd has its own
                // quoting rules that ArgumentList's escaping does not match).
                if (!string.IsNullOrEmpty(CustomShell))
                {
                    process.StartInfo.FileName  = CustomShell;
                    process.StartInfo.Arguments = CustomShellArguments.ReplaceRegex("{{\\s*command\\s*}}", fullCommand);
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    process.StartInfo.FileName  = "cmd.exe";
                    process.StartInfo.Arguments = $"/c {fullCommand}";
                }
                else
                {
                    process.StartInfo.FileName = "/bin/bash";
                    process.StartInfo.ArgumentList.Add("-c");
                    process.StartInfo.ArgumentList.Add(fullCommand);
                }

                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;

                // Prevent the Relay web server's environment from leaking into
                // cluster jobs. Slurm propagates the submitter's CWD and env vars
                // to compute nodes, where paths like ASPNETCORE_CONTENTROOT don't
                // exist and cause worker processes (.NET hosts) to crash.
                process.StartInfo.WorkingDirectory = Path.GetTempPath();
                foreach (var key in process.StartInfo.Environment.Keys
                             .Where(k => k.StartsWith("ASPNETCORE_") || k.StartsWith("Kestrel__"))
                             .ToList())
                    process.StartInfo.Environment.Remove(key);

                process.Start();
                
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                
                await process.WaitForExitAsync(timeoutCts.Token);
                
                string output = await outputTask;
                string error = await errorTask;
                
                if (process.ExitCode == 0)
                    return output;
                    
                lastError = error;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(true); // Kill process tree
                }
                catch { }
                lastError = $"Command timed out after {timeoutMinutes} minutes";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                Console.Error.WriteLine($"Error executing command '{command}': {lastError}, attempt {attempts + 1} of {maxAttempts}");
            }

            attempts++;
            if (attempts < maxAttempts)
                await Task.Delay(2000, CancellationToken.None); // 2 second delay between retries
        }

        throw new Exception($"Command execution failed after {attempts} attempts, last error message: {lastError}");
    }

    /// <summary>
    /// Parses the cluster job ID from the output of the job submission command.
    /// </summary>
    /// <param name="output">The output from the cluster submission command</param>
    /// <returns>The parsed cluster job ID</returns>
    /// <exception cref="Exception">Thrown when the job ID cannot be parsed from the output</exception>
    /// <remarks>
    /// Uses only the parser for this queue's <see cref="SchedulerType"/>. Earlier versions tried
    /// every scheduler's parser in turn, which meant a queue could silently accept a job ID in
    /// another scheduler's format — and masked misconfigured submit commands.
    /// </remarks>
    internal string ParseClusterJobId(string output)
    {
        if (IsManaged)
            throw new InvalidOperationException(
                "A managed queue has no scheduler output to parse; job IDs are process ids " +
                "assigned by ManagedExecutor. Reaching this is a wiring mistake.");

        if (!JobIdParsers.TryGetValue(SchedulerType, out var parser))
            throw new Exception($"No job ID parser for scheduler {SchedulerType}.");

        string result;
        try
        {
            result = parser(output);
        }
        catch (Exception exc)
        {
            throw new Exception(
                $"Couldn't parse job ID from output = \"{output}\" using the {SchedulerType} parser.", exc);
        }

        if (result == null)
            throw new Exception(
                $"Couldn't parse job ID from output = \"{output}\" using the {SchedulerType} parser. " +
                (SchedulerType == ClusterScheduler.Custom
                    ? "Check the queue's job ID parsing regular expression."
                    : $"Check that the queue's scheduler is really {SchedulerType} and that its submit " +
                      "command template is correct."));

        return result;
    }

    /// <summary>
    /// Parses the cluster job status from the output of the status check command.
    /// </summary>
    /// <param name="output">The output from the cluster status check command</param>
    /// <returns>The parsed cluster job status, or Unknown if the output holds no recognisable state</returns>
    /// <remarks>
    /// Uses only the parser for this queue's <see cref="SchedulerType"/>.
    ///
    /// Output the parser doesn't recognise yields Unknown rather than throwing. That is what SLURM
    /// queues already did — their parser returned Unknown as a catch-all — and the daemon's state
    /// handlers are written around it: Unknown leaves a Staging job staged, and finalises a Running
    /// job as Failed. Throwing here would instead escape into the polling loop.
    /// </remarks>
    internal ClusterJobStatus ParseClusterJobStatus(string output)
    {
        // Unknown rather than a throw: unlike ParseClusterJobId this is reachable from
        // ClassifyState, and the daemon's state handlers are written around Unknown.
        if (IsManaged)
            return ClusterJobStatus.Unknown;

        if (!JobStatusParsers.TryGetValue(SchedulerType, out var parser))
            return ClusterJobStatus.Unknown;

        try
        {
            return parser(output) ?? ClusterJobStatus.Unknown;
        }
        catch
        {
            return ClusterJobStatus.Unknown;
        }
    }

    /// <summary>
    /// Whether this queue can start <paramref name="job"/> right now. Queues backed by an external
    /// scheduler always admit — the scheduler does the arbitration.
    /// </summary>
    public override AdmissionResult CanAdmit(Job job)
    {
        if (!IsManaged)
            return AdmissionResult.Admitted;      // the external scheduler arbitrates

        // A duplicate managed queue found at load; see ManagedQueueRules.DisableDuplicateManagedQueues.
        // Reject, not Busy: nothing about waiting fixes a configuration the UI would have refused.
        if (!string.IsNullOrEmpty(ManagedDisabledReason))
            return new AdmissionResult.Reject(ManagedDisabledReason);

        if (Executor == null)
            return new AdmissionResult.Reject(
                $"Queue \"{Alias}\" is managed but has no executor attached. This is a Relay wiring " +
                "fault, not a job problem; refusing to run the job unaccounted for.");

        return Executor.TryAdmit(job, ManagedTotals);
    }

    /// <summary>
    /// Deserializes a ClusterQueue from a JSON node.
    /// </summary>
    /// <param name="reader">The JSON node to read from</param>
    /// <param name="findJob">Function to find job references during deserialization</param>
    /// <remarks>
    /// In addition to base class properties, this handles the CustomVariables dictionary
    /// which contains the descriptions and default values for template variables.
    /// </remarks>
    public override void ReadFromJson(JsonNode reader, Func<int, int, int, Job> findJob)
    {
        base.ReadFromJson(reader, findJob);
        
        if (reader["customVariables"] != null)
            CustomVariables = JsonSerializer.Deserialize<Dictionary<string, (string description, string defaultValue)>>(reader["customVariables"].ToString());
    }

    /// <summary>
    /// Serializes a ClusterQueue to a JSON node.
    /// </summary>
    /// <param name="writer">The JSON node to write to</param>
    /// <remarks>
    /// In addition to base class properties, this handles the CustomVariables dictionary
    /// which contains the descriptions and default values for template variables.
    /// </remarks>
    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);
        
        writer["customVariables"] = JsonSerializer.SerializeToNode(CustomVariables);
    }

    /// <summary>
    /// Gets a read-only wrapper for this ClusterQueue.
    /// </summary>
    /// <returns>A ReadOnlyClusterQueue instance that provides immutable access to this queue</returns>
    /// <remarks>
    /// Uses a ConditionalWeakTable to cache the wrapper and avoid creating
    /// multiple wrapper instances for the same ClusterQueue.
    /// 
    /// This method is called by the QueueRepository to expose queues as read-only
    /// objects to external code, particularly when providing them as properties like
    /// ClusterQueues. It's also used by DataManager.Queue when exposing queue objects 
    /// to clients or when returning the queue being deleted.
    /// 
    /// The ReadOnlyClusterQueue wrapper provides read-only properties that mirror
    /// all the configurable properties of ClusterQueue, allowing clients to access
    /// configuration (like CustomShell, SubmitJobTemplate, etc.) without being able
    /// to modify them.
    /// </remarks>
    public override ReadOnlyJobQueue AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, queue => new ReadOnlyClusterQueue(queue));
    }
}