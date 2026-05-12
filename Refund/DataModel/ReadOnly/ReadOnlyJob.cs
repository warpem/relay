using System.Collections.ObjectModel;
using System.Reflection;
using System.Text.Json.Nodes;
using Warp.Tools;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// A read-only decorator for the Job class, providing immutable access to job data.
/// Jobs are the fundamental processing units in the workflow system, each performing
/// a specific data processing task with defined inputs and outputs.
/// </summary>
public abstract class ReadOnlyJob : IIdentifiable, IAudited, IAnnotated, IViewItem
{
    /// <summary>
    /// The wrapped mutable job instance.
    /// </summary>
    protected readonly Job _job;

    /// <summary>
    /// Cached read-only dictionary of input ports.
    /// </summary>
    private ReadOnlyDictionary<string, ReadOnlyPortIn> _portsInCache;

    /// <summary>
    /// Cached read-only dictionary of output ports.
    /// </summary>
    private ReadOnlyDictionary<string, ReadOnlyPortOut> _portsOutCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyJob"/> class.
    /// </summary>
    /// <param name="job">The mutable job to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if the job parameter is null.</exception>
    protected ReadOnlyJob(Job job)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
    }

    /// <summary>
    /// Gets the actual runtime type of the wrapped job.
    /// This allows systems to determine the concrete job type without unwrapping.
    /// </summary>
    /// <returns>The Type of the wrapped job instance.</returns>
    public Type GetOriginalType() => _job.GetType();
    
    /// <summary>
    /// Gets the value of a specified property from the wrapped job.
    /// Provides reflective access to job parameters.
    /// </summary>
    /// <param name="prop">The property info of the property to access.</param>
    /// <returns>The value of the property.</returns>
    public object GetParameterValue(PropertyInfo prop) => prop.GetValue(_job);
    
    /// <summary>
    /// Gets the typed value of a specified property from the wrapped job.
    /// Provides type-safe reflective access to job parameters.
    /// </summary>
    /// <typeparam name="T">The expected type of the property value.</typeparam>
    /// <param name="prop">The property info of the property to access.</param>
    /// <returns>The strongly-typed value of the property.</returns>
    public T GetParameterValue<T>(PropertyInfo prop) => (T)prop.GetValue(_job);

    /// <summary>
    /// Gets the read-only space that contains this job.
    /// </summary>
    public ReadOnlySpace Space => _job.Space?.AsReadOnly();
    
    /// <summary>
    /// Gets the unique identifier for this job.
    /// </summary>
    public int Id => _job.Id;
    
    /// <summary>
    /// Gets the user-defined display name of this job.
    /// </summary>
    public string Alias => _job.Alias;

    /// <summary>
    /// Gets the optional color tag for visual grouping in the UI.
    /// </summary>
    public string? ColorTag => _job.ColorTag;

    /// <summary>
    /// Gets the factory instance ID if this job is a sub-job of a factory instance.
    /// Null for regular jobs.
    /// </summary>
    public int? FactoryInstanceId => _job.FactoryInstanceId;

    /// <summary>
    /// Gets the alias if one is defined, or the ID as a string if no alias is set.
    /// This ensures every job has a meaningful display string.
    /// </summary>
    public string AliasOrId => _job.AliasOrId;
    
    /// <summary>
    /// Gets a fully qualified name that combines the ID and alias.
    /// This provides a unique, human-readable identifier for UI display.
    /// </summary>
    public string QualifiedName => _job.QualifiedName;
    
    /// <summary>
    /// Gets the name of the directory where this job's data is stored.
    /// This is typically just the name of the folder, not the full path.
    /// </summary>
    public string DirectoryName => _job.DirectoryName;
    
    /// <summary>
    /// Gets the full path to the directory where this job's data is stored.
    /// This combines the space's root directory with the job's directory name.
    /// </summary>
    public string DirectoryPath => _job.DirectoryPath;
    
    /// <summary>
    /// Gets the path to the job's directory relative to the space's root directory.
    /// This is useful for creating relative links between jobs.
    /// </summary>
    public string DirectoryPathInSpace => _job.DirectoryPathInSpace;
    
    /// <summary>
    /// Gets the path to the directory where Relay-specific results for this job are stored.
    /// This directory contains visualizations and metadata that Relay generates.
    /// </summary>
    public string RelayResultsDirectoryPath => _job.RelayResultsDirectoryPath;
    
    /// <summary>
    /// Gets the current status of this job in its lifecycle.
    /// The job status transitions through states like Building, Waiting, Running, Finished, etc.
    /// </summary>
    public JobStatus Status => _job.Status;

    /// <summary>
    /// Gets whether an interactive job has finished processing user input.
    /// </summary>
    public bool IsInteractiveFinished => _job.IsInteractiveFinished;
    
    /// <summary>
    /// Gets the date and time when this job was last updated.
    /// </summary>
    public DateTime UpdateDate => _job.UpdateDate;
    
    /// <summary>
    /// Gets the user who last updated this job.
    /// </summary>
    public ReadOnlyUser UpdatedBy => _job.UpdatedBy?.AsReadOnly();

    /// <summary>
    /// Gets the path to the hero image for this job.
    /// The hero image is displayed in the UI as a banner or icon.
    /// For jobs, this is typically empty and derived from job-specific visualizations.
    /// </summary>
    public string HeroImage => string.Empty;
    
    /// <summary>
    /// Gets the user-provided notes or description of this job.
    /// </summary>
    public string Notes => _job.Notes;
    
    /// <summary>
    /// Gets the ID assigned to this job by the cluster scheduler.
    /// This is only set for jobs that run on a compute cluster.
    /// </summary>
    public string ClusterJobId => _job.ClusterJobId;

    /// <summary>
    /// Gets the ID of the queue this job was last assigned to.
    /// -1 indicates the local queue, positive values indicate cluster queues.
    /// Null means the job has never been queued.
    /// </summary>
    public int? QueueId => _job.QueueId;
    
    /// <summary>
    /// Gets the most recent event, optionally filtered by type.
    /// </summary>
    /// <param name="type">The type of event to find, or null to get the most recent event of any type.</param>
    /// <returns>The most recent event matching the criteria, or null if no matching events exist.</returns>
    public ReadOnlyJobEvent GetMostRecentEvent(EventType? type = null) => _job.GetMostRecentEvent(type)?.AsReadOnly();
    
    /// <summary>
    /// Gets all events of the specified type.
    /// </summary>
    /// <param name="type">The type of events to find.</param>
    /// <returns>A collection of events of the specified type, ordered by timestamp.</returns>
    public IReadOnlyList<ReadOnlyJobEvent> GetEvents(EventType? type) => _job.GetEvents(type)
                                                                             .Select(e => e.AsReadOnly())
                                                                             .ToList().AsReadOnly();
    
    /// <summary>
    /// Gets the highest iteration number for which log files are available.
    /// For non-iterative jobs, this is typically 0.
    /// </summary>
    public int LogsAvailableIteration => _job.LogsAvailableIteration;
    
    /// <summary>
    /// Gets the highest iteration number for which visualizations are available.
    /// For non-iterative jobs, this is typically 0.
    /// </summary>
    public int VisAvailableIteration => _job.VisAvailableIteration;
    
    /// <summary>
    /// Determines whether result files exist for a specific iteration.
    /// </summary>
    /// <param name="iter">The iteration number to check.</param>
    /// <returns>True if result files exist for the specified iteration; otherwise, false.</returns>
    public bool HasResultFilesForIteration(int iter) => _job.HasResultFilesForIteration(iter);
    
    /// <summary>
    /// Gets a value indicating whether this job has run to completion.
    /// A job is considered complete if it has a Finished, Failed, or Aborted status.
    /// </summary>
    public bool HasRunToCompletion => _job.HasRunToCompletion;
    
    /// <summary>
    /// Gets a value indicating whether temporary files for this job have been cleaned up.
    /// Job cleaning reduces disk space usage by removing intermediate files.
    /// </summary>
    public bool HasBeenCleaned => _job.HasBeenCleaned;

    /// <summary>
    /// Gets the estimated time remaining for this job to complete.
    /// </summary>
    public TimeSpan? TimeRemaining => _job.TimeRemaining;

    /// <summary>
    /// Gets the number of processes this job requires.
    /// This can be overridden by derived job types with specific process requirements.
    /// </summary>
    public virtual int ProcessCount => _job.ProcessCount;
    
    /// <summary>
    /// Gets the number of CPU cores this job requires.
    /// This can be overridden by derived job types with specific CPU requirements.
    /// </summary>
    public virtual int CoreCount => _job.CoreCount;
    
    /// <summary>
    /// Gets the amount of memory in gigabytes this job requires.
    /// This can be overridden by derived job types with specific memory requirements.
    /// </summary>
    public virtual int MemoryGb => _job.MemoryGb;
    
    public virtual bool CanBeFinalized => _job.CanBeFinalized;
    
    /// <summary>
    /// Gets the directory where the job's executable should run.
    /// This can be overridden by derived job types with specific directory requirements.
    /// </summary>
    public virtual string RunDirectory => _job.RunDirectory;
    
    /// <summary>
    /// Gets the name of the standard output file for this job.
    /// This can be overridden by derived job types with specific output file naming.
    /// </summary>
    public virtual string NameStdOut => _job.NameStdOut;
    
    /// <summary>
    /// Gets the name of the standard error file for this job.
    /// This can be overridden by derived job types with specific error file naming.
    /// </summary>
    public virtual string NameStdErr => _job.NameStdErr;

    /// <summary>
    /// Gets the path indicating successful completion of this job.
    /// This file is created when the job finishes successfully.
    /// </summary>
    public string PathSuccess => _job.PathSuccess;
    
    /// <summary>
    /// Gets the names of environment modules this job supports.
    /// Jobs may support multiple environment module configurations.
    /// </summary>
    public virtual string[] SupportedModules => _job.RequiredModules;
    
    /// <summary>
    /// Gets the names of environment modules this job requires.
    /// These modules must be loaded before the job can run.
    /// </summary>
    public virtual string[] RequiredModules => _job.RequiredModules;
    
    /// <summary>
    /// Gets a dictionary of resource values this job provides.
    /// These are typically environment variables or configuration values for child jobs.
    /// </summary>
    public Dictionary<string, string> GetResourceValues => _job.GetResourceValues();

    /// <summary>
    /// Gets a read-only dictionary of this job's input ports, keyed by port name.
    /// Input ports receive data from other jobs' output ports.
    /// </summary>
    public ReadOnlyDictionary<string, ReadOnlyPortIn> PortsIn
    {
        get
        {
            if (_portsInCache == null)
            {
                _portsInCache = new ReadOnlyDictionary<string, ReadOnlyPortIn>(
                    _job.PortsIn.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.AsReadOnly()));
            }
            return _portsInCache;
        }
    }

    /// <summary>
    /// Gets a read-only dictionary of this job's output ports, keyed by port name.
    /// Output ports provide data to other jobs' input ports.
    /// </summary>
    public ReadOnlyDictionary<string, ReadOnlyPortOut> PortsOut
    {
        get
        {
            if (_portsOutCache == null)
            {
                _portsOutCache = new ReadOnlyDictionary<string, ReadOnlyPortOut>(
                    _job.PortsOut.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.AsReadOnly()));
            }
            return _portsOutCache;
        }
    }
    
    /// <summary>
    /// Gets the dimensions of this job's card in the workflow view.
    /// The dimensions are expressed as a count of grid squares in width and height.
    /// </summary>
    public int2 CardSquareCount => _job.CardSquareCount;

    public ItemType ItemType => ItemType.Job;

    /// <summary>
    /// Gets the unique identifier for this job type.
    /// This is used for serialization and deserialization.
    /// </summary>
    public string TypeGuid => _job.TypeGuid;

    /// <summary>
    /// Gets the category of this job type, such as "Import", "2D", "3D", etc.
    /// The category helps organize job types in the UI.
    /// </summary>
    public string TypeCategory => _job.TypeCategory;
    
    /// <summary>
    /// Gets the full name of this job type.
    /// This is typically the class name with namespace information.
    /// </summary>
    public string TypeName => _job.TypeName;
    
    /// <summary>
    /// Gets the short name of this job type.
    /// This is typically just the class name without namespace information.
    /// </summary>
    public string TypeNameShort => _job.TypeNameShort;
    
    /// <summary>
    /// Gets a human-readable description of this job type.
    /// This is displayed in the UI to help users understand what the job does.
    /// </summary>
    public string TypeDescription => _job.TypeDescription;
    
    /// <summary>
    /// Gets the type of queue this job should be submitted to.
    /// Jobs can be run locally or on various types of compute clusters.
    /// </summary>
    public JobQueueType QueueType => _job.QueueType;
    
    /// <summary>
    /// Gets the type of the expanded view component for this job.
    /// This component is used to display detailed job information in the UI.
    /// </summary>
    public Type ExpandedViewType => _job.ExpandedViewType;
    
    /// <summary>
    /// Gets the type of the card view component for this job.
    /// This component is used to display summarized job information in the workflow view.
    /// </summary>
    public Type CardViewType => _job.CardViewType;
    
    /// <summary>
    /// Gets the path to the card visualization for a specific iteration.
    /// </summary>
    /// <param name="iter">The iteration number.</param>
    /// <returns>The path to the card visualization file.</returns>
    public string VisCard(int iter) => _job.VisCard(iter);
    
    public string VisCardPdf(int iter) => _job.VisCardPdf(iter);
    
    /// <summary>
    /// Validates this job's input parameters and connections.
    /// </summary>
    /// <returns>A dictionary of validation errors, keyed by parameter name. An empty dictionary indicates no errors.</returns>
    public Dictionary<string, string> ValidateInputs() => _job.ValidateInputs();
    
    /// <summary>
    /// Validates this job's port inputs with sophisticated logic beyond simple numerical limits.
    /// Allows checking resource properties, compatibility, etc.
    /// </summary>
    /// <returns>Dictionary of port validation errors, where key is port name and value is list of error messages</returns>
    public Dictionary<string, List<string>> ValidatePortInputs() => _job.ValidatePortInputs();
    
    /// <summary>
    /// Gets the name of the command that this job executes.
    /// This is typically the name of an executable or script.
    /// </summary>
    public string CommandName => _job.CommandName;
    
    /// <summary>
    /// Gets a value indicating whether this job is ready to be staged.
    /// A job is ready to stage when all its required inputs are connected and valid.
    /// </summary>
    public bool IsReadyToStage => _job.IsReadyToStage();
    
    /// <summary>
    /// Gets a value indicating whether this job is ready to run.
    /// A job is ready to run when it has been staged and all its input files are available.
    /// </summary>
    public bool IsReadyToRun => _job.IsReadyToRun;
    
    /// <summary>
    /// Gets a value indicating whether this job is interactive.
    /// Interactive jobs require user input during execution.
    /// </summary>
    public bool IsInteractive => _job.IsInteractive;
    
    /// <summary>
    /// Gets a value indicating whether this job can be resumed if interrupted.
    /// Resumable jobs can continue from where they left off if aborted or failed.
    /// </summary>
    public bool IsResumable => _job.IsResumable;
    
    /// <summary>
    /// Gets a value indicating whether this job processes data in multiple iterations.
    /// Iterative jobs produce intermediate results during execution.
    /// </summary>
    public bool IsIterative => _job.IsIterative;
    
    /// <summary>
    /// Gets the path to the log file for a specific iteration.
    /// </summary>
    /// <param name="iteration">The iteration number.</param>
    /// <returns>The path to the log file.</returns>
    public string LogFilePath(int iteration) => _job.LogFilePath(iteration);
    
    /// <summary>
    /// Gets the path to the error file for this job.
    /// The error file contains any error messages generated during job execution.
    /// </summary>
    public string ErrorFilePath => _job.ErrorFilePath;
    
    /// <summary>
    /// Gets the path to the staging log file for this job.
    /// The staging log file contains a summary of the job's staging process.
    /// </summary>
    public string LifecycleFilePath => _job.LifecycleFilePath;

    /// <summary>
    /// Gets the parent jobs that provide input to this job.
    /// Parent jobs are those whose output ports are connected to this job's input ports.
    /// </summary>
    /// <returns>An enumerable collection of read-only parent jobs.</returns>
    public IEnumerable<ReadOnlyJob> GetParents() => _job.GetParents().Select(j => j.AsReadOnly());
    
    /// <summary>
    /// Gets the child jobs that receive output from this job.
    /// Child jobs are those whose input ports are connected to this job's output ports.
    /// </summary>
    /// <returns>An enumerable collection of read-only child jobs.</returns>
    public IEnumerable<ReadOnlyJob> GetChildren() => _job.GetChildren().Select(j => j.AsReadOnly());

    /// <summary>
    /// Gets the neighbor jobs in a specified direction.
    /// </summary>
    /// <param name="direction">The direction of traversal (Upstream for parents, Downstream for children).</param>
    /// <returns>An enumerable collection of read-only neighbor jobs.</returns>
    public IEnumerable<ReadOnlyJob> GetNeighbors(TraversalDirection direction) =>
        _job.GetNeighbors(direction).Select(j => j.AsReadOnly());

    /// <summary>
    /// Gets the edges connected to this job in a specified direction.
    /// </summary>
    /// <param name="direction">The direction of traversal (Upstream for incoming edges, Downstream for outgoing edges).</param>
    /// <returns>An enumerable collection of read-only edges.</returns>
    public IEnumerable<ReadOnlyEdge> GetEdges(TraversalDirection direction) =>
        _job.GetEdges(direction).Select(e => e.AsReadOnly());

    /// <summary>
    /// Determines whether this job can transition to a specified status.
    /// Job status transitions follow a state machine defined by the workflow system.
    /// </summary>
    /// <param name="to">The target status.</param>
    /// <returns>True if the transition is valid; otherwise, false.</returns>
    public bool CanTransitionState(JobStatus to) => _job.CanTransitionState(to);
    
    /// <summary>
    /// Determines whether this job is memberwise equal to another read-only job.
    /// Two jobs are memberwise equal if all their properties have the same values.
    /// </summary>
    /// <param name="other">The other read-only job to compare with.</param>
    /// <returns>True if the jobs are memberwise equal; otherwise, false.</returns>
    public bool EqualsMemberwise(ReadOnlyJob other) => _job.EqualsMemberwise(other._job);
    
    /// <summary>
    /// Determines whether this job's wrapped job is memberwise equal to another mutable job.
    /// Two jobs are memberwise equal if all their properties have the same values.
    /// </summary>
    /// <param name="other">The other mutable job to compare with.</param>
    /// <returns>True if the jobs are memberwise equal; otherwise, false.</returns>
    public bool EqualsMemberwise(Job other) => _job.EqualsMemberwise(other);

    /// <summary>
    /// Converts this job to a JSON representation.
    /// </summary>
    /// <returns>A JSON node containing the serialized job data.</returns>
    public JsonNode ToJson() => _job.ToJson();
    
    /// <summary>
    /// Converts this job to a JSON string representation.
    /// </summary>
    /// <returns>A JSON string containing the serialized job data.</returns>
    public string ToJsonString() => _job.ToJsonString();
    
    /// <summary>
    /// Writes a message to the job's lifecycle log.
    /// </summary>
    /// <param name="message">The message to write.</param>
    public async Task WriteToLifecycleLog(string message = "") => await _job.WriteToLifecycleLog(message);
    
    /// <summary>
    /// Writes a message to the job's error log.
    /// </summary>
    /// <param name="message">The message to write.</param>
    public async Task WriteToErrorLog(string message = "") => await _job.WriteToErrorLog(message);
}

/// <summary>
/// Read-only wrapper for a job event.
/// </summary>
public class ReadOnlyJobEvent
{
    private readonly JobEvent _jobEvent;

    public ReadOnlyJobEvent(JobEvent jobJobEvent)
    {
        _jobEvent = jobJobEvent;
    }

    /// <summary>
    /// Type of the event.
    /// </summary>
    public EventType Type => _jobEvent.Type;
    
    /// <summary>
    /// Timestamp when the event occurred.
    /// </summary>
    public DateTime Timestamp => _jobEvent.Timestamp;
    
    /// <summary>
    /// User who initiated the event, if applicable.
    /// </summary>
    public ReadOnlyUser Author => _jobEvent.Author?.AsReadOnly();
}