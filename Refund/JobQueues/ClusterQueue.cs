using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues.ReadOnly;
using Refund.Utils;

namespace Refund.JobQueues;

/// <summary>
/// Manages job submission and execution on a remote computing cluster.
/// Supports various cluster schedulers including SLURM, LSF, PBS, and SGE,
/// with configurable templates for each phase of job submission and monitoring.
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
    /// Regular expression for extracting the job ID from the cluster scheduler output.
    /// Used when scheduler type is set to 'custom'.
    /// </summary>
    [RelayProperty]
    public string JobIdParseRegex { get; set; } = "";

    /// <summary>
    /// String pattern that indicates a job is pending in the cluster queue.
    /// Used when scheduler type is set to 'custom'.
    /// </summary>
    [RelayProperty]
    public string JobStatusParseTemplatePending { get; set; } = "";

    /// <summary>
    /// String pattern that indicates a job is running on the cluster.
    /// Used when scheduler type is set to 'custom'.
    /// </summary>
    [RelayProperty]
    public string JobStatusParseTemplateRunning { get; set; } = "";

    /// <summary>
    /// String pattern that indicates a job has failed on the cluster.
    /// Used when scheduler type is set to 'custom'.
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
    /// Collection of parser functions for extracting job IDs from cluster output.
    /// Each parser handles a specific scheduler type (slurm, lsf, pbs, sge, or custom).
    /// </summary>
    private Dictionary<string, Func<string, string>> JobIdParsers;
    
    /// <summary>
    /// Collection of parser functions for determining job status from cluster output.
    /// Each parser handles a specific scheduler type (slurm, lsf, pbs, sge, or custom).
    /// </summary>
    private Dictionary<string, Func<string, ClusterJobStatus?>> JobStatusParsers;

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
                "slurm",
                (output) =>
                {
                    var match = Regex.Match(output, @"Submitted batch job (\d+)");
                    return match.Success ? match.Groups[1].Value : null;
                }
            },
            {
                "lsf",
                (output) =>
                {
                    var match = Regex.Match(output, @"Job <(\d+)> is submitted");
                    return match.Success ? match.Groups[1].Value : null;
                }
            },
            {
                "pbs",
                (output) =>
                {
                    var match = Regex.Match(output, @"(\d+)\.(\w+)");
                    return match.Success ? match.Value : null;
                }
            },
            {
                "sge",
                (output) =>
                {
                    var match = Regex.Match(output, @"Your job (\d+)");
                    return match.Success ? match.Groups[1].Value : null;
                }
            },
            {
                "custom",
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
                "slurm",
                (output) =>
                {
                    // Check for single-character state codes (default squeue output)
                    if (output.Contains(" PD ") || output.Contains("PENDING")) return ClusterJobStatus.Pending;
                    if (output.Contains(" R ") || output.Contains("RUNNING")) return ClusterJobStatus.Running;
                    if (output.Contains(" CD ") || output.Contains("COMPLETED")) return ClusterJobStatus.Finished;
                    if (output.Contains(" F ") || output.Contains(" CA ") || output.Contains(" TO ") || 
                        output.Contains("FAILED") || output.Contains("CANCELLED") || output.Contains("TIMEOUT")) return ClusterJobStatus.Failed;
                    if (output.Contains(" CG ") || output.Contains("COMPLETING")) return ClusterJobStatus.Running; // Still running

                    return ClusterJobStatus.Unknown;
                }
            },
            {
                "lsf",
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
                "pbs",
                (output) =>
                {
                    if (output.Contains(" Q ")) return ClusterJobStatus.Pending;
                    if (output.Contains(" R ")) return ClusterJobStatus.Running;
                    if (output.Contains(" C ")) return ClusterJobStatus.Failed;

                    return null;
                }
            },
            {
                "sge",
                (output) =>
                {
                    if (output.Contains(" qw ")) return ClusterJobStatus.Pending;
                    if (output.Contains(" r ")) return ClusterJobStatus.Running;
                    if (output.Contains(" Eqw ")) return ClusterJobStatus.Failed;

                    return null;
                }
            },
            {
                "custom",
                (output) =>
                {
                    if (JobStatusParseTemplatePending == null || JobStatusParseTemplateRunning == null || JobStatusParseTemplateFailed == null)
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

            Task.Run(async () =>
            {
                try
                {
                    JobUpdateCallback(job, j =>
                    {
                        j.DirectoryName = j.Id.ToString();
                        j.Status = JobStatus.Staging;
                    });

                    string scriptPath = await PrepareAndWriteScript(job, customValues);
                    cts.Token.ThrowIfCancellationRequested();

                    lock (Sync)
                        JobsInLimbo.Add(job);

                    await job.WriteToLifecycleLog($"Submitting script: {scriptPath}");

                    string rawOutput = null;
                    string jobId = await SubmitScript(scriptPath, o => rawOutput = o);
                    await job.WriteToLifecycleLog(rawOutput);
                    await job.WriteToLifecycleLog($"Parsed cluster job ID: {jobId}");

                    JobUpdateCallback(job, j => { j.ClusterJobId = jobId; });
                }
                catch (Exception exc)
                {
                    await job.WriteToErrorLog($"Job {job.Id} cancelled before it went to cluster:\n{exc}");
                    JobUpdateCallback(job, j => j.Status = JobStatus.Failed);
                }
                finally
                {
                    lock (Sync)
                    {
                        StagingJobs.Remove(job);
                        if (!JobsInLimbo.Contains(job))
                            JobsInLimbo.Remove(job);
                    }
                }
            }, cts.Token);
        }
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
        jobCommand.Append($"{commandName} {string.Join(" ", arguments.Select(kv => string.IsNullOrWhiteSpace(kv.Value) ?
                                                                                   $"--{kv.Key}" :
                                                                                   $"--{kv.Key} {kv.Value}"))}");
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
        // Unlike the Job-based path, worker scripts have no {{job_id}} to substitute —
        // a pool worker is not a Relay Job. Any {{job_id}} in the template is dropped by
        // ProcessSubmissionScript's unmatched-tag cleanup.
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
            // Create a regex pattern that allows for any number of spaces between braces and name
            string pattern = $"{{{{\\s*{Regex.Escape(kvp.Key)}\\s*}}}}";
            result = Regex.Replace(result, pattern, kvp.Value);
        }

        // Replace custom variables
        foreach (var kvp in CustomVariables)
        {
            // Create a regex pattern that allows for any number of spaces between braces and name
            string pattern = $"{{{{\\s*{Regex.Escape(kvp.Key)}\\s*}}}}";
            string value = (customValues?.GetValueOrDefault(kvp.Key) ?? kvp.Value.defaultValue) ?? "";
            
            result = Regex.Replace(result, pattern, value);
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
    /// 2. For jobs in limbo (staged but waiting for cluster job ID), it waits for the ID to be assigned
    /// 3. For jobs running on the cluster, it sends an abort command to the cluster scheduler
    /// </remarks>
    public override void AbortJob(Job job)
    {
        base.AbortJob(job);
        
        lock (Sync)
            if (StagingJobs.ContainsKey(job))
            {
                if (!StagingJobs[job].IsCancellationRequested)
                    StagingJobs[job].Cancel();

                if (!JobsInLimbo.Contains(job))
                    return;
            }

        while (JobsInLimbo.Contains(job) && string.IsNullOrWhiteSpace(job.ClusterJobId))
            Thread.Sleep(100);

        Task.Run(async () =>
        {
            string command = AbortJobTemplate.ReplaceRegex("{{\\s*job_id\\s*}}", job.ClusterJobId);
            await job.WriteToLifecycleLog($"\nAborting job {job.Id} with command: {command}");
            
            string output = await ExecuteOnCluster(command);
            await job.WriteToLifecycleLog(output);
        });
    }
    
    /// <summary>
    /// Returns the set of currently active cluster job IDs by executing ListJobsTemplate.
    /// Throws if ListJobsTemplate is not configured.
    /// </summary>
    public async Task<HashSet<string>> ListActiveJobIds()
    {
        if (string.IsNullOrWhiteSpace(ListJobsTemplate))
            throw new InvalidOperationException(
                $"Queue \"{Alias}\" has no ListJobsTemplate configured. " +
                "Add a command that prints one active job ID per line (e.g. squeue -u $USER -h -o \"%i\").");

        string output = await ExecuteOnCluster(ListJobsTemplate);
        return ParseJobIds(output);
    }

    /// <summary>
    /// Parses scheduler stdout (one job ID per line) into a set of IDs.
    /// Handles both \n and \r\n line endings; blank lines are ignored.
    /// </summary>
    internal static HashSet<string> ParseJobIds(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
              .ToHashSet();

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
                // Default shells
                string shellPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash";
                string shellArguments = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? $"/c {fullCommand}" : $"-c \"{fullCommand}\"";

                // If a specific shell is provided, use it
                if (!string.IsNullOrEmpty(CustomShell))
                {
                    shellPath = CustomShell;
                    shellArguments = CustomShellArguments.ReplaceRegex("{{\\s*command\\s*}}", fullCommand);
                }

                process.StartInfo.FileName = shellPath;
                process.StartInfo.Arguments = shellArguments;
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
    /// Tries parsers for each supported scheduler type (slurm, lsf, pbs, sge, custom)
    /// until one succeeds or all fail.
    /// </remarks>
    private string ParseClusterJobId(string output)
    {
        string result = null;

        foreach (var parser in JobIdParsers)
        {
            try
            {
                result = parser.Value(output);
            }
            catch { }

            if (result != null) break;
        }

        if (result == null)
            throw new Exception($"Couldn't parse job ID from output = \"{output}\"");

        return result;
    }

    /// <summary>
    /// Parses the cluster job status from the output of the status check command.
    /// </summary>
    /// <param name="output">The output from the cluster status check command</param>
    /// <returns>The parsed cluster job status</returns>
    /// <exception cref="Exception">Thrown when the job status cannot be parsed from the output</exception>
    /// <remarks>
    /// Tries parsers for each supported scheduler type (slurm, lsf, pbs, sge, custom)
    /// until one succeeds or all fail. Parsers look for specific strings that indicate
    /// job states like pending, running, completed, or failed.
    /// </remarks>
    private ClusterJobStatus ParseClusterJobStatus(string output)
    {
        ClusterJobStatus? result = null;

        foreach (var parser in JobStatusParsers)
        {
            try
            {
                result = parser.Value(output);
            }
            catch { }

            if (result != null) break;
        }

        if (result == null)
            throw new Exception($"Couldn't parse job status from output = \"{output}\"");

        return result.Value;
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