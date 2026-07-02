using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.Components.Jobs;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Refund.UIFields;
using Refund.Utils;
using Serilog;
using Warp.Tools;

namespace Refund.DataModel;

/// <summary>
/// Abstract base class for all job types in the Relay system.
/// Jobs represent processing steps in a scientific workflow, with inputs, outputs, and parameters.
/// They can be connected together to form complex processing pipelines through their ports.
/// 
/// Each job has a lifecycle (Building → Waiting → Staging → Running → Finalizing → Finished)
/// and can be executed on local or cluster resources.
/// </summary>
public abstract class Job : RelayBase, IFolderContent
{
    /// <summary>
    /// Registry of all job types in the system, indexed by their type category string.
    /// Populated during static initialization.
    /// </summary>
    public static readonly Dictionary<string, Type> Types = new();

    /// <summary>
    /// Registry of hierarchic category strings for all job types in the system.
    /// </summary>
    public static readonly Dictionary<string, Type> TypeCategories = new();

    /// <summary>
    /// Mapping from job types to their display names.
    /// Populated during static initialization.
    /// </summary>
    public static readonly Dictionary<Type, string> TypeNames = new();

    /// <summary>
    /// Hierarchical organization of job types, allowing them to be displayed in categorized menus.
    /// The hierarchy is constructed based on the dot-notation in type category strings.
    /// </summary>
    public static readonly JobTypeGroup TypeHierarchy = new("");

    /// <summary>
    /// Collection of parameter properties for each job type.
    /// These are properties decorated with UiFieldBase attributes.
    /// </summary>
    public static readonly Dictionary<Type, HashSet<PropertyInfo>> TypeParameters = new();

    /// <summary>
    /// Collection of advanced parameter properties for each job type.
    /// These are properties with UiFieldBase.IsAdvanced set to true.
    /// </summary>
    public static readonly Dictionary<Type, HashSet<PropertyInfo>> TypeAdvancedParameters = new();

    /// <summary>
    /// Categorized parameter properties for each job type, organized by UiFieldGroup.
    /// Each category contains a list of properties that belong to it.
    /// </summary>
    public static readonly Dictionary<Type, Dictionary<string, List<PropertyInfo>>> TypeParameterCategories = new();

    /// <summary>
    /// Mapping from property to UiFieldBase attribute for each job type.
    /// This allows quick access to field metadata without reflection.
    /// </summary>
    public static readonly Dictionary<Type, Dictionary<PropertyInfo, UiFieldBase>> TypeUiFields = new();

    /// <summary>
    /// Default values for each parameter of each job type.
    /// These values are extracted from newly created instances of each type.
    /// </summary>
    public static readonly Dictionary<Type, Dictionary<string, object>> DefaultValues = new();


    /// <summary>
    /// Pre-computed dependency chains for each property of each job type.
    /// Maps from job type -> property -> list of properties it depends on (in order of dependency chain).
    /// Used for efficient dependency checking in the UI.
    /// </summary>
    public static readonly Dictionary<Type, Dictionary<PropertyInfo, List<PropertyInfo>>> TypeDependencyChains = new();

    /// <summary>
    /// Validation function for each property of each job type.
    /// Returns true if the property should be visible given the current job state.
    /// </summary>
    public static readonly Dictionary<Type, Dictionary<PropertyInfo, Func<object, bool>>> TypeDependencyValidators = new();

    /// <summary>
    /// Set of all modules required by any job type in the system.
    /// Used for determining which modules need to be available on computing resources.
    /// </summary>
    public static readonly HashSet<string> Modules = new();

    /// <summary>
    /// Collection of input ports for each job type.
    /// Populated during static initialization from newly created instances.
    /// </summary>
    public static readonly Dictionary<Type, ReadOnlyDictionary<string, PortIn>> AllTypesPortsIn = new();

    /// <summary>
    /// Collection of output ports for each job type.
    /// Populated during static initialization from newly created instances.
    /// </summary>
    public static readonly Dictionary<Type, ReadOnlyDictionary<string, PortOut>> AllTypesPortsOut = new();

    /// <summary>
    /// Cache of read-only wrappers for jobs, using weak references to avoid memory leaks.
    /// </summary>
    private static readonly ConditionalWeakTable<Job, ReadOnlyJob> ReadOnlyCache = new();

    /// <summary>
    /// Mapping from job types to functions that create read-only wrappers for them.
    /// This allows specialized wrapper types for specific job subclasses.
    /// </summary>
    private static Dictionary<Type, Func<Job, ReadOnlyJob>> ReadOnlyWrappers = new();

    /// <summary>
    /// The space containing this job.
    /// </summary>
    public Space Space { get; set; } = null;

    /// <summary>
    /// Unique identifier for this job within its containing space.
    /// </summary>
    [RelayProperty(Order = -106)]
    public int Id { get; set; } = -1;

    /// <summary>
    /// Name of the directory where job data is stored.
    /// This is typically the job ID or a user-defined name.
    /// </summary>
    [RelayProperty(Order = -105)]
    public string DirectoryName { get; set; } = "";

    /// <summary>
    /// Full path to the job's directory, combining the space root and the job directory name.
    /// </summary>
    public string DirectoryPath => Path.Combine(Space?.RootDirectory ?? "", DirectoryName);

    /// <summary>
    /// Path to the job's directory relative to the space root.
    /// </summary>
    public string DirectoryPathInSpace => DirectoryName;

    /// <summary>
    /// Name of the subdirectory where Relay-specific results are stored.
    /// </summary>
    const string RelayResultsDirectoryName = ".relay";

    /// <summary>
    /// Full path to the Relay-specific results directory.
    /// This directory contains visualizations, logs, and other metadata.
    /// </summary>
    public string RelayResultsDirectoryPath => Path.Combine(DirectoryPath, RelayResultsDirectoryName);

    /// <summary>
    /// User-defined name for the job.
    /// This is a human-readable identifier displayed in the UI.
    /// </summary>
    [RelayProperty(Order = -104)]
    public string Alias { get; set; } = string.Empty;

    /// <summary>
    /// Gets either the job's alias or its ID if the alias is not set.
    /// This provides a user-friendly display name that always has a value.
    /// </summary>
    public string AliasOrId => string.IsNullOrWhiteSpace(Alias) ? Id.ToString() : Alias;

    /// <summary>
    /// Gets a fully qualified name for the job, including its ID and either its type name or alias.
    /// This is used in places where a unique, human-readable identifier is needed.
    /// </summary>
    public string QualifiedName => $"J{Id}: {(string.IsNullOrWhiteSpace(Alias) ? TypeName : Alias)}";

    /// <summary>
    /// Current status of the job in its lifecycle.
    /// Jobs progress through a series of states from Building to Finished (or Failed/Aborted).
    /// </summary>
    [RelayProperty(Order = -103)]
    public JobStatus Status { get; set; } = JobStatus.Building;
    
    [Clearable]
    [RelayProperty]
    public bool IsInteractiveFinished { get; set; } = false;

    /// <summary>
    /// Date and time when this job was last updated.
    /// This property is cleared when the job is reset.
    /// </summary>
    [Clearable]
    [RelayProperty(Order = -101)]
    public DateTime UpdateDate { get; set; }

    /// <summary>
    /// User who last updated this job.
    /// This property is cleared when the job is reset.
    /// </summary>
    [Clearable]
    public User UpdatedBy { get; set; }

    /// <summary>
    /// User-provided notes or comments about this job.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public string Notes { get; set; } = string.Empty;

    /// <summary>
    /// Identifier for the job on the compute cluster.
    /// This is used to track and manage the job on the cluster scheduler.
    /// This property is cleared when the job is reset.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public string ClusterJobId { get; set; } = "";

    /// <summary>
    /// ID of the queue this job was last assigned to.
    /// -1 indicates the local queue, positive values indicate cluster queues.
    /// Null means the job has never been queued.
    /// This property is cleared when the job is reset.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public int? QueueId { get; set; } = null;

    /// <summary>
    /// Optional color tag for visual grouping in the UI.
    /// Stored as a hex color string (e.g. "#F5A8A8") or null for no color.
    /// Inherited from the first colored parent when the job is queued, unless already set.
    /// </summary>
    [RelayProperty]
    public string? ColorTag { get; set; } = null;

    /// <summary>
    /// If set, marks this job as a sub-job of a factory instance.
    /// Null for regular jobs. Used to exclude from root items and identify ownership.
    /// </summary>
    [RelayProperty]
    public int? FactoryInstanceId { get; set; } = null;

    /// <summary>
    /// List of events that have occurred during the job's lifecycle.
    /// Each event has a type, timestamp, and optional author.
    /// This list is never cleared, even when the job is reset.
    /// </summary>
    [RelayProperty(Order = -102)]
    [SkipAdoption]
    public List<JobEvent> Events { get; set; } = new();
    
    /// <summary>
    /// Adds a new event to the job's event list with the current timestamp.
    /// </summary>
    /// <param name="type">The type of event.</param>
    /// <param name="author">The user who initiated the event, if applicable.</param>
    public void AddEvent(EventType type, User author = null)
    {
        Events.Add(new JobEvent(type, DateTime.Now, author));
    }
    
    /// <summary>
    /// Gets the most recent event, optionally filtered by type.
    /// </summary>
    /// <param name="type">The type of event to find, or null to get the most recent event of any type.</param>
    /// <returns>The most recent event matching the criteria, or null if no matching events exist.</returns>
    public JobEvent GetMostRecentEvent(EventType? type = null)
    {
        if (Events.Count == 0)
            return null;
        
        var eventsCopy = Events.ToList(); // Avoid issues if Events is modified during enumeration
            
        var query = type.HasValue 
            ? eventsCopy.Where(e => e.Type == type.Value)
            : eventsCopy;
            
        return query.OrderByDescending(e => e.Timestamp).FirstOrDefault();
    }
    
    /// <summary>
    /// Gets all events of the specified type.
    /// </summary>
    /// <param name="type">The type of events to find.</param>
    /// <returns>A collection of events of the specified type, ordered by timestamp.</returns>
    public List<JobEvent> GetEvents(EventType? type)
    {
        if (!type.HasValue)
            return Events.OrderBy(e => e.Timestamp).ToList();

        return Events.Where(e => e.Type == type)
                     .OrderBy(e => e.Timestamp)
                     .ToList();
    }

    /// <summary>
    /// Highest iteration number for which visualization data is available.
    /// A value of -1 indicates no visualizations are available.
    /// This property is cleared when the job is reset.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public int VisAvailableIteration { get; set; } = -1;

    /// <summary>
    /// Highest iteration number for which log data is available.
    /// A value of -1 indicates no logs are available.
    /// This property is cleared when the job is reset.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public int LogsAvailableIteration { get; set; } = -1;

    /// <summary>
    /// Indicates whether the job has run to completion.
    /// This is set to true when the job reaches its final iteration successfully.
    /// This property is cleared when the job is reset.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public bool HasRunToCompletion { get; set; } = false;

    /// <summary>
    /// Indicates whether the job's temporary files have been cleaned up.
    /// This is set to true after a successful cleanup to save disk space.
    /// This property is cleared when the job is reset.
    /// </summary>
    [Clearable]
    [RelayProperty]
    public bool HasBeenCleaned { get; set; } = false;

    /// <summary>
    /// Number of processes to use when running this job.
    /// Default is 1, but can be overridden by job implementations that support parallelism.
    /// </summary>
    public virtual int ProcessCount => 1;

    /// <summary>
    /// Number of CPU cores to allocate per process when running this job.
    /// Default is 1, but can be overridden by job implementations that need more cores.
    /// </summary>
    public virtual int CoreCount => 1;

    /// <summary>
    /// Amount of memory in gigabytes to allocate when running this job.
    /// Default is 16 GB, but can be overridden by job implementations with higher memory requirements.
    /// </summary>
    public virtual int MemoryGb => 16;

    /// <summary>
    /// Number of GPUs to allocate when running this job.
    /// Default is 0 (no GPUs), but can be overridden by job implementations that use GPU acceleration.
    /// </summary>
    public virtual int GpuCount => 1;

    /// <summary>
    /// Amount of GPU memory in gigabytes required per GPU when running this job.
    /// Default is 0, but can be overridden by job implementations with specific GPU memory requirements.
    /// </summary>
    public virtual int GpuMemoryGb => 12;

    /// <summary>
    /// Indicates whether the job can be finalized.
    /// Default is false, but can be overridden by job implementations that support finalization.
    /// </summary>
    public virtual bool CanBeFinalized => false;

    protected bool _isFinalized = false;

    public TimeSpan? TimeRemaining { get; set; }

    /// <summary>
    /// Base directory where the job should run.
    /// Default is the space's root directory, but can be overridden if needed.
    /// </summary>
    public virtual string RunDirectory => Space.RootDirectory;

    /// <summary>
    /// Name of the file where standard output should be redirected.
    /// Default is "std.out", but can be overridden if needed.
    /// </summary>
    public virtual string NameStdOut => "std.out";

    public string PathStdOut => Path.Combine(DirectoryPath, NameStdOut);

    /// <summary>
    /// Name of the file where standard error should be redirected.
    /// Default is "std.err", but can be overridden if needed.
    /// </summary>
    public virtual string NameStdErr => "std.err";

    protected ProgressiveTextReader StdErrReader;

    public string PathStdErr => Path.Combine(DirectoryPath, NameStdErr);
    
    public virtual string NameSuccess => "SUCCESS";
    
    public string PathSuccess => Path.Combine(DirectoryPath, NameSuccess);

    /// <summary>
    /// Array of module names that this job can use.
    /// Default is ["cpu", "gpu"], but can be overridden to specify other supported modules.
    /// </summary>
    public virtual string[] SupportedModules => ["cpu", "gpu"];

    /// <summary>
    /// Array of module names that this job requires to run.
    /// Default is empty, but can be overridden to specify required modules.
    /// </summary>
    public virtual string[] RequiredModules => Array.Empty<string>();

    /// <summary>
    /// Gets a dictionary of resource requirement values for use in job submission scripts.
    /// This includes information about CPU, memory, GPU, and I/O requirements.
    /// </summary>
    /// <returns>A dictionary of resource requirement name-value pairs</returns>
    public Dictionary<string, string> GetResourceValues()
    {
        return new Dictionary<string, string>
        {
            { "n_processes", ProcessCount.ToString() },
            { "n_cores", CoreCount.ToString() },
            { "memory_gb", MemoryGb.ToString() },
            { "n_gpus", GpuCount.ToString() },
            { "gpu_memory_gb", GpuMemoryGb.ToString() },
            { "run_directory", RunDirectory },
            { "std_out", PathStdOut },
            { "std_err", PathStdErr }
        };
    }

    /// <summary>
    /// Determines whether result files are available for a specific iteration.
    /// This considers both visualization availability and log availability.
    /// </summary>
    /// <param name="iter">The iteration number to check</param>
    /// <returns>True if result files are available for the specified iteration, false otherwise</returns>
    public bool HasResultFilesForIteration(int iter)
    {
        if (PortsOut.Count == 0)
            return false;

        if (!HasBeenCleaned)
            return iter <= VisAvailableIteration || // Visualizations can't be prepared without results
                   iter < LogsAvailableIteration;   // Next iteration's log doesn't start before results for this iteration are available
        else
            return iter == LogsAvailableIteration; // Cleaning only available once finished, and only leaves results for last iteration
    }

    /// <summary>
    /// Number of Display Squares the job's card should have in the X and Y dimensions.
    /// X is used for width and Y for height as number of squares.
    /// This determines the size of the job card in the UI.
    /// </summary>
    public abstract int2 CardSquareCount { get; set; }
    
    /// <summary>
    /// Gets the unique identifier for this job type.
    /// This is used for serialization and deserialization.
    /// </summary>
    public abstract string TypeGuid { get; }

    /// <summary>
    /// Gets the type category of this job.
    /// This is a dot-notation path used to organize job types in menus and hierarchies.
    /// </summary>
    public abstract string TypeCategory { get; }

    /// <summary>
    /// Gets the full display name of this job type.
    /// This is shown in menus and selection dialogs.
    /// </summary>
    public abstract string TypeName { get; }

    /// <summary>
    /// Gets a shortened display name for this job type.
    /// This is used in space-constrained UI elements.
    /// </summary>
    public abstract string TypeNameShort { get; }

    /// <summary>
    /// Gets a description of this job type.
    /// This provides more detailed information about what the job does.
    /// </summary>
    public abstract string TypeDescription { get; }

    /// <summary>
    /// Gets the type of queue this job should run on.
    /// This determines whether the job runs locally or on a cluster.
    /// </summary>
    public abstract JobQueueType QueueType { get; }

    /// <summary>
    /// Gets the type of component to use for the expanded view of this job.
    /// The expanded view shows detailed job information and controls.
    /// </summary>
    public abstract Type ExpandedViewType { get; }

    /// <summary>
    /// Gets the type of component to use for the card view of this job.
    /// The card view shows a compact representation of the job in the workflow graph.
    /// Default is BasicJobCardContent, but can be overridden for specialized views.
    /// </summary>
    public virtual Type CardViewType => typeof(BasicJobCardContent);

    /// <summary>
    /// Gets the path to the visualization card image for a specific iteration.
    /// </summary>
    /// <param name="iter">The iteration number</param>
    /// <returns>Path to the card image file</returns>
    public virtual string VisCard(int iter) => Path.Combine(RelayResultsDirectoryPath, $"card_{iter:D4}.png");
    public virtual string VisCardPdf(int iter) => Path.Combine(RelayResultsDirectoryPath, $"card_{iter:D4}.pdf");

    /// <summary>
    /// Collection of input ports for this job, indexed by port name.
    /// Input ports represent data requirements for the job.
    /// </summary>
    public ReadOnlyDictionary<string, PortIn> PortsIn { get; protected set; }

    /// <summary>
    /// Collection of output ports for this job, indexed by port name.
    /// Output ports represent data produced by the job.
    /// </summary>
    public ReadOnlyDictionary<string, PortOut> PortsOut { get; protected set; }

    /// <summary>
    /// Validates the inputs to this job before running.
    /// This checks for missing or invalid connections and parameters.
    /// </summary>
    /// <returns>A dictionary of validation errors, where the key is the error location and the value is the error message</returns>
    public virtual Dictionary<string, string> ValidateInputs()
    {
        return new();
    }

    /// <summary>
    /// Validates port inputs with sophisticated logic beyond simple numerical limits.
    /// Allows checking resource properties, compatibility, etc.
    /// </summary>
    /// <returns>Dictionary of port validation errors, where key is port name and value is list of error messages</returns>
    public virtual Dictionary<string, List<string>> ValidatePortInputs()
    {
        var errors = new Dictionary<string, List<string>>();
        
        // Validate connection count requirements for all input ports
        foreach (var port in PortsIn.Values)
        {
            // Only validate active ports
            if (!port.IsActive())
                continue;
                
            var connectionCount = port.Edges.Count;
            
            if (connectionCount < port.MinItems)
                AddPortValidationError(errors, port.Name, $"Requires at least {port.MinItems} connection{(port.MinItems == 1 ? "" : "s")}");
            
            if (connectionCount > port.MaxItems)
                AddPortValidationError(errors, port.Name, $"Cannot have more than {port.MaxItems} connection{(port.MaxItems == 1 ? "" : "s")}");
        }
        
        return errors;
    }

    /// <summary>
    /// Helper method to add a port validation error.
    /// </summary>
    /// <param name="errors">The errors dictionary to add to</param>
    /// <param name="portName">The name of the port</param>
    /// <param name="message">The error message to add</param>
    protected void AddPortValidationError(Dictionary<string, List<string>> errors, string portName, string message)
    {
        if (!errors.ContainsKey(portName))
            errors[portName] = new List<string>();
        errors[portName].Add(message);
    }

    /// <summary>
    /// Returns a read-only wrapper for this job.
    /// The read-only wrapper provides a safe view that prevents accidental modification.
    /// The wrapper type is determined based on the concrete job type.
    /// </summary>
    /// <returns>A read-only wrapper for this job</returns>
    public ReadOnlyJob AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, job => ReadOnlyWrappers[this.GetType()](job));
    }

    /// <summary>
    /// Determines whether this job is ready to be staged for execution.
    /// A job is ready to stage when all of its input dependencies have finished successfully.
    /// </summary>
    /// <returns>True if the job is ready to stage, false otherwise</returns>
    public virtual bool IsReadyToStage()
    {
        foreach (PortIn port in PortsIn.Values)
            foreach (Edge edge in port.Edges)
                if (edge.Source != null && edge.Source.Job != null && edge.Source.Job.Status != JobStatus.Finished)
                    return false;

        return true;
    }

    /// <summary>
    /// Prepares the job for execution by setting up working directories, input files, etc.
    /// This is called before the job is submitted to a queue.
    /// </summary>
    public virtual void Stage()
    {
        // Default implementation does nothing, subclasses should override as needed
    }

    /// <summary>
    /// Determines whether this job is ready to run after being staged.
    /// Default is true, but can be overridden to implement additional checks.
    /// </summary>
    public virtual bool IsReadyToRun => true;

    /// <summary>
    /// Indicates whether this job requires interactive user input during execution.
    /// Default is false, but can be overridden for jobs that need user interaction.
    /// </summary>
    public virtual bool IsInteractive => false;

    /// <summary>
    /// Indicates whether this job can be resumed after interruption.
    /// Default is false, but can be overridden for jobs that support checkpointing.
    /// </summary>
    public virtual bool IsResumable => false;

    /// <summary>
    /// Indicates whether this job runs in multiple iterations.
    /// Default is false, but can be overridden for jobs that produce incremental results.
    /// </summary>
    public virtual bool IsIterative => false;

    /// <summary>
    /// Clears the job's state and working directory.
    /// This resets the job to its initial state, allowing it to be run again.
    /// All properties marked with [Clearable] are reset to their default values.
    /// </summary>
    public virtual void Clear()
    {
        // Make super sure we're not deleting the parent space directory
        if (!string.IsNullOrEmpty(DirectoryName) &&
            Directory.Exists(DirectoryPath) &&
            !Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                 .Equals(Path.GetFullPath(Space.RootDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                         StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(DirectoryPath, true);
            Directory.CreateDirectory(DirectoryPath);
        }

        ClearProperties();
    }

    /// <summary>
    /// Resets all properties marked with [Clearable] to their default values
    /// without touching the filesystem.
    /// </summary>
    public void ClearProperties()
    {
        var pristine = Activator.CreateInstance(this.GetType());
        var properties = this.GetType().GetProperties().Where(p => p.IsDefined(typeof(Clearable)) && p.CanWrite);

        foreach (PropertyInfo property in properties)
            property.SetValue(this, property.GetValue(pristine));
    }

    /// <summary>
    /// Performs finalization steps after job execution completes.
    /// This can include post-processing of results, cleanup, etc.
    /// </summary>
    public virtual void FinalizeRun(Action<Job, Action<Job>> updateCallback)
    {
        // Default implementation does nothing, subclasses should override as needed
    }

    /// <summary>
    /// Gets the path to the log file for a specific iteration.
    /// </summary>
    /// <param name="iteration">The iteration number</param>
    /// <returns>Path to the log file</returns>
    public virtual string LogFilePath(int iteration) => Path.Combine(RelayResultsDirectoryPath, $"log_it{iteration:D4}.txt");

    /// <summary>
    /// Gets the path to the error file.
    /// </summary>
    public virtual string ErrorFilePath => Path.Combine(RelayResultsDirectoryPath, "error.txt");

    /// <summary>
    /// Gets the path to the staging (submission script, cluster response etc.) file.
    /// </summary>
    public virtual string LifecycleFilePath => Path.Combine(RelayResultsDirectoryPath, "staging.txt");

    #region Reflection for static initialization

    public static void PopulateStatic()
    {
        PopulateTypes();
        PopulateTypePorts();
        PopulateReadOnlyTypes();
    }

    private static void PopulateTypes()
    {
        #region Find all classes that inherit from Job

        Types.Clear();
        TypeCategories.Clear();
        TypeNames.Clear();
        TypeParameters.Clear();
        TypeAdvancedParameters.Clear();
        TypeParameterCategories.Clear();
        TypeUiFields.Clear();
        TypeDependencyChains.Clear();
        TypeDependencyValidators.Clear();

        foreach (var type in Assembly.GetAssembly(typeof(Job))
                                     .GetTypes()
                                     .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(Job))))
        {
            var instance = (Job)Activator.CreateInstance(type);
            
            if (Types.ContainsKey(instance.TypeGuid))
                throw new Exception($"Duplicate job type:\n" +
                                    $"Tried to add type: {instance.TypeCategory}, {instance.TypeGuid}\n" +
                                    $"Already there: {Types[instance.TypeGuid]}");
            
            Types.Add(instance.TypeGuid, type);
            TypeCategories.Add(instance.TypeCategory, type);
            TypeNames.Add(type, instance.TypeName);
            DefaultValues.Add(type, new Dictionary<string, object>());
            TypeParameters.Add(type, new HashSet<PropertyInfo>());
            TypeAdvancedParameters.Add(type, new HashSet<PropertyInfo>());
            TypeParameterCategories.Add(type, new Dictionary<string, List<PropertyInfo>>());
            TypeUiFields.Add(type, new Dictionary<PropertyInfo, UiFieldBase>());
            TypeDependencyChains.Add(type, new Dictionary<PropertyInfo, List<PropertyInfo>>());
            TypeDependencyValidators.Add(type, new Dictionary<PropertyInfo, Func<object, bool>>());

            foreach (var module in instance.SupportedModules)
                Modules.Add(module);
        }

        #endregion

        #region Build job type hierarchy based on each type's TypeCategory string

        TypeHierarchy.Subgroups.Clear();
        TypeHierarchy.Types.Clear();

        foreach ((string path, Type type) in TypeCategories)
        {
            // If type has HideFromMenu attribute, skip
            if (type.GetCustomAttribute<HideFromMenuAttribute>() is not null)
                continue;
            
            var crumbs = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var currentGroup = TypeHierarchy;

            for (int i = 0; i < crumbs.Length - 1; i++)
            {
                if (!currentGroup.Subgroups.Any(g => g.Name == crumbs[i]))
                    currentGroup.Subgroups.Add(new JobTypeGroup(crumbs[i]));

                currentGroup = currentGroup.Subgroups.First(g => g.Name == crumbs[i]);
            }

            currentGroup.Types.Add((crumbs.Last(), type));
        }

        #endregion

        #region Populate advanced parameters

        string categoryName = string.Empty;

        foreach (var pair in TypeAdvancedParameters)
            foreach (var prop in pair.Key.GetProperties()
                                     .Where(p => p.GetCustomAttributes<UiFieldBase>(false).Any()))
            {
                TypeParameters[pair.Key].Add(prop);

                var uiFieldAttribute = prop.GetCustomAttribute<UiFieldBase>(false);

                if (!string.IsNullOrEmpty(uiFieldAttribute.DataDelegateName))
                    uiFieldAttribute.DataDelegate = pair.Key.GetMethod(uiFieldAttribute.DataDelegateName)
                                                        ?.CreateDelegate<Func<ReadOnlyJob, object>>(null);

                if (uiFieldAttribute.IsAdvanced)
                    pair.Value.Add(prop);

                TypeUiFields[pair.Key][prop] = uiFieldAttribute;

                // Order properties in categories by UIFieldGroup attribute 
                if (prop.GetCustomAttribute<UiFieldGroup>() != null)
                    categoryName = prop.GetCustomAttribute<UiFieldGroup>().Label ?? string.Empty;

                if (!TypeParameterCategories[pair.Key].ContainsKey(categoryName))
                    TypeParameterCategories[pair.Key].Add(categoryName, new List<PropertyInfo>());

                TypeParameterCategories[pair.Key][categoryName].Add(prop);
            }

        #endregion

        #region Populate dependency chains and validators for each type

        foreach (var type in TypeUiFields.Keys)
        {
            try
            {
                PopulateDependencyChains(type);
            }
            catch (Exception ex)
            {
                Log.ForContext<Job>().Error(ex, "Error building dependency chains for job type {JobTypeName}", type.Name);
                // Remove the problematic type from all collections
                Types.Remove(Types.FirstOrDefault(t => t.Value == type).Key);
                TypeCategories.Remove(TypeCategories.FirstOrDefault(t => t.Value == type).Key);
                TypeNames.Remove(type);
                TypeParameters.Remove(type);
                TypeAdvancedParameters.Remove(type);
                TypeParameterCategories.Remove(type);
                TypeUiFields.Remove(type);
                TypeDependencyChains.Remove(type);
                TypeDependencyValidators.Remove(type);
                DefaultValues.Remove(type);
            }
        }

        #endregion

        #region Populate default values for each type

        foreach (var type in DefaultValues.Keys)
        {
            var instance = (Job)Activator.CreateInstance(type);

            if (instance != null)
            {
                var properties = type.GetProperties().Where(p => p.GetCustomAttributes(typeof(UiFieldBase), true).Any());

                foreach (var property in properties)
                {
                    var defaultValue = property.GetValue(instance);
                    DefaultValues[type][property.Name] = defaultValue;
                }
            }
        }

        #endregion
    }

    private static void PopulateTypePorts()
    {
        AllTypesPortsIn.Clear();
        AllTypesPortsOut.Clear();

        foreach (var type in Types.Values)
        {
            var instance = (Job)Activator.CreateInstance(type);

            if (instance?.PortsIn != null)
                AllTypesPortsIn[type] = new ReadOnlyDictionary<string, PortIn>(instance.PortsIn);

            if (instance?.PortsOut != null)
                AllTypesPortsOut[type] = new ReadOnlyDictionary<string, PortOut>(instance.PortsOut);
        }
    }

    private static void PopulateReadOnlyTypes()
    {
        // Find all types with the ReadOnlyFor attribute, and connect each type's static Wrap() method to their attribute's JobType
        foreach (var type in Assembly.GetAssembly(typeof(ReadOnlyJob))
                                     .GetTypes()
                                     .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(ReadOnlyJob))))
        {
            var readOnlyAttr = type.GetCustomAttribute<ReadOnlyForAttribute>();

            if (readOnlyAttr != null)
            {
                var jobType = readOnlyAttr.JobType;

                if (jobType == null)
                    throw new Exception($"Job type {readOnlyAttr.JobType} does not exist");

                var wrapMethod = type.GetMethod("Wrap", BindingFlags.Public|BindingFlags.Static);

                if (wrapMethod != null)
                    ReadOnlyWrappers[jobType] = wrapMethod.CreateDelegate<Func<Job, ReadOnlyJob>>();
            }
        }
    }

    /// <summary>
    /// Populates dependency chains and validators for a specific job type.
    /// Detects circular dependencies and throws an exception if found.
    /// </summary>
    /// <param name="jobType">The job type to analyze</param>
    private static void PopulateDependencyChains(Type jobType)
    {
        var dependencyChains = new Dictionary<PropertyInfo, List<PropertyInfo>>();
        var dependencyValidators = new Dictionary<PropertyInfo, Func<object, bool>>();
        var uiFields = TypeUiFields[jobType];

        foreach (var (property, uiField) in uiFields)
        {
            try
            {
                // Build dependency chain for this property
                var chain = BuildDependencyChain(jobType, property, uiField, new HashSet<PropertyInfo>());
                dependencyChains[property] = chain;

                // Create validator function for this property
                dependencyValidators[property] = CreateDependencyValidator(jobType, uiField, chain);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Circular dependency"))
            {
                throw new Exception($"Circular dependency detected for property {property.Name}: {ex.Message}");
            }
        }

        TypeDependencyChains[jobType] = dependencyChains;
        TypeDependencyValidators[jobType] = dependencyValidators;
    }

    /// <summary>
    /// Builds the dependency chain for a property, detecting circular dependencies.
    /// </summary>
    /// <param name="jobType">The job type</param>
    /// <param name="property">The property to build chain for</param>
    /// <param name="uiField">The UI field attribute</param>
    /// <param name="visited">Properties already visited in this chain (for cycle detection)</param>
    /// <returns>List of properties this property depends on</returns>
    private static List<PropertyInfo> BuildDependencyChain(Type jobType, PropertyInfo property, UiFieldBase uiField, HashSet<PropertyInfo> visited)
    {
        var chain = new List<PropertyInfo>();

        if (string.IsNullOrEmpty(uiField.ConditionalOnField))
            return chain;

        if (visited.Contains(property))
            throw new InvalidOperationException($"Circular dependency detected involving {property.Name}");

        visited.Add(property);

        // Find the property this one depends on
        var dependsOnProperty = jobType.GetProperty(uiField.ConditionalOnField);
        if (dependsOnProperty == null)
            throw new Exception($"Dependency property '{uiField.ConditionalOnField}' not found for {property.Name}");

        chain.Add(dependsOnProperty);

        // Check if the dependency property also has dependencies
        if (TypeUiFields[jobType].TryGetValue(dependsOnProperty, out var dependsOnUiField))
        {
            var nestedChain = BuildDependencyChain(jobType, dependsOnProperty, dependsOnUiField, visited);
            chain.AddRange(nestedChain);
        }

        visited.Remove(property);
        return chain;
    }

    /// <summary>
    /// Creates a validation function for a property that checks its dependencies.
    /// </summary>
    /// <param name="jobType">The job type</param>
    /// <param name="uiField">The UI field attribute</param>
    /// <param name="dependencyChain">The dependency chain for this property</param>
    /// <returns>A validation function that takes a job instance and returns true if the property should be visible</returns>
    private static Func<object, bool> CreateDependencyValidator(Type jobType, UiFieldBase uiField, List<PropertyInfo> dependencyChain)
    {
        // If there's no condition, the property is always visible.
        if (string.IsNullOrEmpty(uiField.ConditionalOnField))
            return _ => true;

        return (jobInstance) =>
        {
            try
            {
                // Helper to get a property's value from a job instance (which could be a Job or ReadOnlyJob).
                object GetParameterValue(PropertyInfo property, object instance)
                {
                    if (instance is ReadOnlyJob readOnlyJob)
                        return readOnlyJob.GetParameterValue(property);
                    
                    return property.GetValue(instance);
                }

                // Helper to check if a single dependency condition is met.
                bool IsConditionMet(PropertyInfo dependentProperty, UiFieldBase dependentUiField, object instance)
                {
                    if (string.IsNullOrEmpty(dependentUiField.ConditionalOnField))
                        return true;

                    var conditionalProperty = jobType.GetProperty(dependentUiField.ConditionalOnField);
                    if (conditionalProperty == null) 
                        return false;

                    var conditionalValue = GetParameterValue(conditionalProperty, instance);

                    if (dependentUiField.ConditionalOnValue == null)
                        return conditionalValue != null;
                    
                    if (conditionalValue == null)
                        return false;

                    // Use Equals for safe comparison, especially with boxed value types.
                    return conditionalValue.GetType() == dependentUiField.ConditionalOnValue.GetType() && 
                           conditionalValue.Equals(dependentUiField.ConditionalOnValue);
                }

                // First, check if the direct condition for the original property is met.
                if (!IsConditionMet(null, uiField, jobInstance))
                    return false;

                // Now, walk the chain. For each property in the chain, it must also be visible.
                // This means its own condition must be met.
                foreach (var dependencyProperty in dependencyChain)
                {
                    if (!TypeUiFields[jobType].TryGetValue(dependencyProperty, out var dependencyUiField))
                        continue; // Should not happen in a valid chain

                    if (!IsConditionMet(dependencyProperty, dependencyUiField, jobInstance))
                        return false;
                }

                return true;
            }
            catch
            {
                // In case of reflection errors or other exceptions, assume not visible.
                return false;
            }
        };
    }

    /// <summary>
    /// Checks if a property should be visible for a given job instance using pre-computed validators.
    /// </summary>
    /// <param name="jobType">The job type</param>
    /// <param name="property">The property to check</param>
    /// <param name="jobInstance">The job instance</param>
    /// <returns>True if the property should be visible</returns>
    public static bool IsPropertyVisible(Type jobType, PropertyInfo property, ReadOnlyJob jobInstance)
    {
        if (!TypeDependencyValidators.TryGetValue(jobType, out var validators))
            return true;

        if (!validators.TryGetValue(property, out var validator))
            return true;

        return validator(jobInstance);
    }

    #endregion

    #region Command composition

    public virtual string CommandName => "";

    public virtual string CommandPrefix => "";

    public virtual string CommandSuffix => "";

    public virtual Dictionary<string, string> ComposeCommandArguments()
    {
        // Using reflection, go through all properties with an attribute derived from UIField
        // and compose a command string based on UIField.CliName and the property's value
        // For UIBool, don't add the value, but only add UIField.CliName in case the value is true, leave out if false
        // For UIRange/UIFloat3, expect a name of form "arg1,arg2..." and add options accordingly

        var properties = this.GetType()
                             .GetProperties()
                             .Where(p => p.GetCustomAttributes(typeof(UiFieldBase), true).Any());

        var result = new Dictionary<string, string>();

        foreach (var property in properties)
        {
            var value = property.GetValue(this);
            var attribute = (UiFieldBase)property.GetCustomAttributes(typeof(UiFieldBase), true).First();

            if (string.IsNullOrWhiteSpace(attribute.CliName) || value == null)
                continue;

            if (value is string stringValue && string.IsNullOrWhiteSpace(stringValue))
                continue;

            if (!IsPropertyVisible(this.GetType(), property, this.AsReadOnly()))
                continue;

            if (attribute is UiBool uiBool)
            {
                bool boolValue = (bool)value;

                if (!uiBool.Reverse && !boolValue)
                    continue;

                if (uiBool.Reverse && boolValue)
                    continue;

                result.Add(attribute.CliName, "");
            }
            else if (attribute is UiRange)
            {
                string[] attributeNames = attribute.CliName.Split(',').ToArray();
                float2 float2Value = (float2)value;

                if (attributeNames.Length < 2)
                    throw new Exception("badly formed attribute name, expected 2 comma separated attribute names");

                result.Add(key: attributeNames[0], value: float2Value.X.ToString(CultureInfo.InvariantCulture));
                result.Add(key: attributeNames[1], value: float2Value.Y.ToString(CultureInfo.InvariantCulture));
            }
            else if (attribute is UiFloat3)
            {
                string[] attributeNames = attribute.CliName.Split(',').ToArray();
                float3 float3Value = (float3)value;

                if (attributeNames.Length < 3)
                    throw new Exception("badly formed attribute name, expected 3 comma separated attribute names");

                result.Add(key: attributeNames[0], value: float3Value.X.ToString(CultureInfo.InvariantCulture));
                result.Add(key: attributeNames[1], value: float3Value.Y.ToString(CultureInfo.InvariantCulture));
                result.Add(key: attributeNames[2], value: float3Value.Z.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                if (value is decimal decimalValue)
                    result.Add(attribute.CliName, decimalValue.ToString(CultureInfo.InvariantCulture));
                else if (value is string)
                    result.Add(attribute.CliName, $"\"{value}\"");
                else
                    result.Add(attribute.CliName, value.ToString());
            }
        }

        return result;
    }

    /// <summary>
    /// If there are arguments defined as a continuous string: split them, remove -- from the names,
    /// and insert them into the result dictionary. Boolean arguments don't have values.
    /// </summary>
    /// <param name="arguments">Continuous string with arguments (preceded by --) and values</param>
    /// <returns></returns>
    protected Dictionary<string, string> ArgumentStringToDictionary(string arguments)
    {
        Dictionary<string, string> result = new();

        string[] args = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < args.Length; i++)
            if (args[i].StartsWith("--"))
            {
                string key = args[i].Substring(2);

                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    result[key] = args[i + 1];
                    i++;
                }
                else
                {
                    result[key] = "";
                }
            }

        return result;
    }

    /// <summary>
    /// Determine whether to include a UIField during command composition.
    /// Logic: the value of ConditionalOnField on this instance is checked to
    /// see whether it equals ConditionalOnValue on the UIField instance.
    /// </summary>
    /// <param name="uiField"></param>
    /// <returns></returns>
    protected bool UiFieldDependencySatisfied(UiFieldBase uiField)
    {
        // If ConditionalOnField is unset, include the field.
        if (string.IsNullOrEmpty(uiField.ConditionalOnField))
            return true;

        // Recursively check dependencies chain
        {
            MemberInfo memberInfo = this.GetType().GetMember(uiField.ConditionalOnField).FirstOrDefault();

            if (memberInfo == null)
                throw new Exception($"Couldn't find member {uiField.ConditionalOnField}");

            if (memberInfo.GetCustomAttributes(typeof(UiFieldBase), true).FirstOrDefault() is UiFieldBase conditionalField)
                if (!UiFieldDependencySatisfied(conditionalField))
                    return false;
        }

        // Get the value of the field on this instance.
        var fieldValue = this.GetMemberValue(uiField.ConditionalOnField);

        // Return true if condition is met, otherwise false.
        // To compare values, we first check types, then use
        // Equals instead of == to avoid issues with boxing.
        if (fieldValue.GetType() == uiField.ConditionalOnValue.GetType())
            return fieldValue.Equals(uiField.ConditionalOnValue);

        return false;
    }

    protected object GetMemberValue(string memberName)
    {
        // Using reflection, access a field or property on this object by name
        // we don't yet know whether the member is a field or a property
        Type type = this.GetType();

        // Try to get a field value
        FieldInfo fieldInfo = type.GetField(memberName, BindingFlags.Public|BindingFlags.Instance);

        if (fieldInfo != null)
            return fieldInfo.GetValue(this);

        // Try to get a property value
        PropertyInfo propertyInfo = type.GetProperty(memberName, BindingFlags.Public|BindingFlags.Instance);

        if (propertyInfo != null)
            return propertyInfo.GetValue(this);

        throw new Exception($"Couldn't access member {memberName}");
    }

    #endregion

    #region Read/write

    public static Job CreateFromPolymorphicJson(JsonNode reader, Space space, ReadOnlyCollection<User> users)
    {
        var typeString = reader["Type"].Deserialize<string>();
        var typeParts = typeString.Split(',', StringSplitOptions.TrimEntries);
        var typeGuid = typeParts[0];

        if (!Types.ContainsKey(typeGuid))
            throw new Exception($"Specified job type does not exist: {typeString}");

        var result = (Job)Activator.CreateInstance(Types[typeGuid]);
        result.Space = space;
        result.ReadFromJson(reader["Job"], users);

        return result;
    }

    public static void WritePolymorphicJson(JsonNode writer, Job job)
    {
        var typeString = $"{job.TypeGuid}, {job.TypeCategory}";
        writer["Type"] = typeString;
        writer["Job"] = job.ToJson();
    }

    public static JsonNode ToPolymorphicJson(Job job)
    {
        JsonNode writer = new JsonObject();
        WritePolymorphicJson(writer, job);

        return writer;
    }

    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);

        writer["UpdatedBy"] = UpdatedBy?.Id;
        
        if (Events != null && Events.Count > 0)
        {
            var eventsArray = new JsonArray();
            foreach (var evt in Events)
            {
                var eventObj = new JsonObject
                {
                    ["Type"] = (int)evt.Type,
                    ["Timestamp"] = evt.Timestamp,
                    ["AuthorId"] = evt.Author?.Id
                };
                eventsArray.Add(eventObj);
            }
            writer["Events"] = eventsArray;
        }
    }

    public void ReadFromJson(JsonNode reader, ReadOnlyCollection<User> users)
    {
        base.ReadFromJson(reader);

        // Handle UpdatedBy
        if (reader["UpdatedBy"] != null)
            UpdatedBy = users.FirstOrDefault(u => u.Id == reader["UpdatedBy"].Deserialize<int>());

        if (UpdatedBy == null)
            UpdatedBy = Space?.Project?.Owner;

        if (UpdatedBy == null)
            UpdatedBy = Space?.Project?.Members.FirstOrDefault();

        if (UpdatedBy == null)
            UpdatedBy = users.FirstOrDefault();
            
        // Handle Events
        if (reader["Events"] != null)
        {
            var eventsArray = reader["Events"].AsArray();
            Events.Clear();
            
            foreach (var eventNode in eventsArray)
            {
                var type = (EventType)eventNode["Type"].Deserialize<int>();
                var timestamp = eventNode["Timestamp"].Deserialize<DateTime>();
                User author = null;
                
                if (eventNode["AuthorId"] != null)
                {
                    int authorId = eventNode["AuthorId"].Deserialize<int>();
                    author = users.FirstOrDefault(u => u.Id == authorId);
                    // If author not found, we still keep the event but with null author
                }
                
                Events.Add(new JobEvent(type, timestamp, author));
            }
        }
    }

    public static string ToPolymorphicJsonString(Job job) => ToPolymorphicJson(job).ToJsonString();

    #endregion

    #region State transition
    
    // @formatter:off
    private static bool[][] TransitionMatrix = new bool[][]
    {
            // To:   Blding  Wting Stging Rnning Fnlzng Fnshed Abrtng Abrted Failed Dlted  Clrng           From:
        new bool[] { false,  true, false, false, false, false, false, false, false,  true, false },     // Building
        new bool[] {  true, false,  true, false, false, false, false, false, false,  true,  true },     // Waiting
        new bool[] { false, false, false,  true, false, false,  true, false,  true, false, false },     // Staging
        new bool[] { false, false, false, false, false, false,  true, false,  true, false, false },     // Running
        new bool[] { false, false, false, false, false,  true, false, false,  true, false, false },     // Finalizing
        new bool[] {  true, false, false, false, false, false, false, false, false,  true,  true },     // Finished
        new bool[] { false, false, false, false, false, false, false,  true, false, false, false },     // Aborting
        new bool[] {  true, false,  true, false,  true, false, false, false, false,  true,  true },     // Aborted
        new bool[] {  true, false, false, false,  true, false, false, false, false,  true,  true },     // Failed
        new bool[] { false, false, false, false, false, false, false, false, false, false, false },     // Deleted
        new bool[] {  true, false, false, false, false, false, false, false, false, false, false }      // Clearing
    };
    // @formatter:on

    private static bool CanTransitionState(JobStatus from, JobStatus to)
    {
        return TransitionMatrix[(int)from][(int)to];
    }

    public bool CanTransitionState(JobStatus to)
    {
        return CanTransitionState(Status, to);
    }

    #endregion

    #region Graph traversal

    /// <summary>
    /// Get all parents of this job.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<Job> GetParents()
    {
        return PortsIn.Values.SelectMany(p => p.Edges)
                      .Where(e => e.Source != null && e.Source.Job != null)
                      .Select(e => e.Source.Job)
                      .Distinct();
    }

    /// <summary>
    /// Get all children of this job.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<Job> GetChildren()
    {
        return PortsOut.Values.SelectMany(p => p.Edges)
                       .Where(e => e.Target != null && e.Target.Job != null)
                       .Select(e => e.Target.Job)
                       .Distinct();
    }

    /// <summary>
    /// Get all neighbors of this job in a specific direction.
    /// </summary>
    /// <param name="direction">Search direction</param>
    /// <returns></returns>
    public IEnumerable<Job> GetNeighbors(TraversalDirection direction)
    {
        switch (direction)
        {
            case TraversalDirection.Both:
                return GetParents().Concat(GetChildren());
            case TraversalDirection.Ancestors:
                return GetParents();
            case TraversalDirection.Descendants:
                return GetChildren();
            default:
                throw new ArgumentException("Invalid direction value.");
        }
    }

    public IEnumerable<Edge> GetEdges(TraversalDirection direction)
    {
        List<Edge> result = new();

        if ((direction&TraversalDirection.Ancestors) != 0)
            foreach (var port in PortsIn.Values)
                result.AddRange(port.Edges);

        if ((direction&TraversalDirection.Descendants) != 0)
            foreach (var port in PortsOut.Values)
                result.AddRange(port.Edges);

        return result;
    }

    private IEnumerable<Job> GetAllRelatives(
        TraversalDirection direction,
        IEnumerable<int> distances = null,
        Func<Job, bool> qualifier = null)
    {
        HashSet<Job> relativesMatching = new();
        HashSet<Job> relativesAll = new();
        Queue<(Job, int)> queue = new Queue<(Job, int)>();
        var maxDistance = distances?.Max() ?? int.MaxValue;

        queue.Enqueue((this, 0));

        while (queue.Count > 0)
        {
            (var current, int depth) = queue.Dequeue();

            if (relativesAll.Contains(current))
                continue;

            relativesAll.Add(current);

            if ((qualifier == null || qualifier(current)) &&
                (distances == null || distances.Contains(depth)) &&
                current != this)
                relativesMatching.Add(current);

            if (depth + 1 <= maxDistance)
                foreach (Job child in current.GetNeighbors(direction))
                    queue.Enqueue((child, depth + 1));
        }

        return relativesMatching;
    }

    /// <summary>
    /// Get all ancestors of this job, optionally filtered by distance from this job and a qualifier function.
    /// </summary>
    /// <param name="distances">One or multiple distances to which to restrict the breadth-first search</param>
    /// <param name="qualifier">Function to select specific jobs</param>
    /// <returns></returns>
    public IEnumerable<Job> GetAncestors(IEnumerable<int> distances = null, Func<Job, bool> qualifier = null) =>
        GetAllRelatives(TraversalDirection.Ancestors, distances, qualifier);

    /// <summary>
    /// Get all descendants of this job, optionally filtered by distance from this job and a qualifier function.
    /// </summary>
    /// <param name="distances">One or multiple distances to which to restrict the breadth-first search</param>
    /// <param name="qualifier">Function to select specific jobs</param>
    /// <returns></returns>
    public IEnumerable<Job> GetDescendants(IEnumerable<int> distances = null, Func<Job, bool> qualifier = null) =>
        GetAllRelatives(TraversalDirection.Descendants, distances, qualifier);

    /// <summary>
    /// Find the shortest path to a job. Because of constant edge cost and lack of cycles, this is a simple breadth-first search.
    /// </summary>
    /// <param name="target">Target job to search for</param>
    /// <param name="direction">Search direction</param>
    /// <returns></returns>
    public IEnumerable<Job> FindShortestPathTo(Job target, TraversalDirection direction)
    {
        if (this == target)
            return new List<Job> { this };

        Queue<Job> queue = new();
        Dictionary<Job, Job> cameFrom = new();

        queue.Enqueue(this);
        cameFrom[this] = null;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            foreach (var next in current.GetNeighbors(direction))
            {
                if (!cameFrom.ContainsKey(next))
                {
                    queue.Enqueue(next);
                    cameFrom[next] = current;

                    if (next == target)
                    {
                        var path = new List<Job>();
                        var temp = next;

                        while (temp != null)
                        {
                            path.Insert(0, temp);
                            temp = cameFrom[temp];
                        }

                        return path;
                    }
                }
            }
        }

        // If the target is not reachable, return null
        return null;
    }

    #endregion

    public virtual bool EqualsMemberwise(Job other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other == null || GetType() != other.GetType()) return false;

        return GetType()
               .GetProperties()
               .All(property =>
               {
                   var thisValue = property.GetValue(this);
                   var otherValue = property.GetValue(other);

                   return Equals(thisValue, otherValue);
               });
    }

    public void RemoveEdges()
    {
        foreach (var port in PortsIn.Values)
            port.Edges.Clear();

        foreach (var port in PortsOut.Values)
            port.Edges.Clear();
    }

    public virtual Action TrackProgressLogs()
    {
        StdErrReader ??= new ProgressiveTextReader(PathStdErr);

        // Ensure results directory exists
        JobTools.EnsureResultsDirectory(RelayResultsDirectoryPath);

        try
        {
            string newErrors = StdErrReader.ReadNewContent();

            if (!string.IsNullOrEmpty(newErrors))
            {
                File.AppendAllText(ErrorFilePath, newErrors);

                return () => { };
            }
        }
        catch (Exception ex)
        {
            Log.ForContext<Job>().Error(ex, "Error tracking progress logs for job {JobId}", Id);
        }

        return null;
    }

    public virtual Action TrackProgressResults() => null;

    public async Task WriteToLifecycleLog(string message = "")
    {
        try
        {
            Directory.CreateDirectory(RelayResultsDirectoryPath);

            await using (var writer = File.AppendText(LifecycleFilePath))
                await writer.WriteLineAsync(message);
        }
        catch (Exception ex)
        {
            Log.ForContext<Job>().Error(ex, "Error writing to lifecycle log for job {JobId}", Id);
        }
    }

    public async Task WriteToErrorLog(string message = "")
    {
        try
        {
            Directory.CreateDirectory(RelayResultsDirectoryPath);

            await using (var writer = File.AppendText(ErrorFilePath))
                await writer.WriteLineAsync(message);
        }
        catch (Exception ex)
        {
            Log.ForContext<Job>().Error(ex, "Error writing to error log for job {JobId}", Id);
        }
    }
}

public interface ILocalJob
{
    public void RunLocal(CancellationToken token);
}

public interface IClusterJob { }

/// <summary>
/// Implemented by jobs that process a set of items and report progress counts. UI (e.g. the job
/// card) shows item progress for any job implementing this — gated on the counts being non-null
/// rather than on a concrete job type. This is a pure read contract (get-only properties), so the
/// ReadOnly source generator mirrors it onto the generated read-only wrappers automatically.
/// </summary>
public interface IItemProgress
{
    /// <summary>Number of items processed so far, or null if the job is not reporting counts.</summary>
    int? NItemsProcessed { get; }

    /// <summary>Total number of items to process, or null if not yet known / not reported.</summary>
    int? NItemsTotal { get; }

    /// <summary>Number of items that failed processing, or null if not applicable.</summary>
    int? NItemsFailed { get; }
}

/// <summary>
/// Implemented by WarpTools GPU jobs that maintain a fleet of short-lived cluster
/// worker jobs alongside the single Manager cluster job.
/// </summary>
public interface IPooledJob
{
    /// <summary>The job's working directory (where pool_state.json, worker_logs/, and the worker script live).</summary>
    string DirectoryPath { get; }

    /// <summary>
    /// ID of the ClusterQueue to use for worker pool submissions.
    /// Valid cluster queue IDs are >= 1; any value &lt; 1 (default -1) means no pool / local mode.
    /// </summary>
    int PoolQueueId { get; }

    /// <summary>Target number of simultaneously running worker jobs.</summary>
    int PoolSize { get; }

    /// <summary>
    /// Maximum total worker submissions across the job's lifetime.
    /// Circuit-breaker against sick-worker replacement spirals.
    /// </summary>
    int PoolSubmissionCap { get; }

    /// <summary>
    /// Submission-script template variables for one pool worker. Derived from the Manager's
    /// Job.GetResourceValues (so a worker can never silently miss a variable the template expects)
    /// with worker-specific overrides (one GPU, per-worker cores/memory, worker job name, log paths).
    /// <paramref name="workerLogDir"/> is where the worker's SLURM stdout/stderr (std_out/std_err) go.
    /// </summary>
    Dictionary<string, string> GetWorkerResourceValues(string workerLogDir);

    /// <summary>Required cluster modules for worker jobs (e.g. ["warp", "gpu"]).</summary>
    string[] WorkerRequiredModules { get; }

    /// <summary>
    /// Full command string for a worker bound to the given GPU device index.
    /// Example: "cd /run && WarpWorker2 --queue-dir /data/1/tasks --device 2 --log-dir /data/1/logs"
    /// </summary>
    string GetWorkerCommand(int deviceIndex);
}

public enum JobStatus
{
    Building = 0,
    Waiting = 1,
    Staging = 2,
    Running = 3,
    Finalizing = 4,
    Finished = 5,
    Aborting = 6,
    Aborted = 7,
    Failed = 8,
    Deleted = 9,
    Clearing = 10
}

public static class JobStatusExtensions
{
    public static bool IsUnsettled(this JobStatus status) => status switch
    {
        JobStatus.Staging => true, JobStatus.Running => true, JobStatus.Finalizing => true, JobStatus.Aborting => true, JobStatus.Clearing => true, _ => false
    };

    public static bool IsOnCluster(this JobStatus status) => status switch
    {
        JobStatus.Staging => true, JobStatus.Running => true, JobStatus.Finalizing => true, JobStatus.Aborting => true, _ => false
    };
}

/// <summary>
/// Types of events that can be recorded during a job's lifecycle.
/// </summary>
public enum EventType
{
    /// <summary>
    /// Job was created.
    /// </summary>
    Created,
    
    /// <summary>
    /// User requested job execution, the first state after that is waiting.
    /// </summary>
    WaitingStarted,
    
    /// <summary>
    /// Job was submitted to a queue.
    /// </summary>
    StagingStarted,
    
    /// <summary>
    /// Job execution started.
    /// </summary>
    RunningStarted,
    
    /// <summary>
    /// Job execution finished successfully.
    /// </summary>
    Finished,
    
    /// <summary>
    /// Job execution failed.
    /// </summary>
    Failed,
    
    /// <summary>
    /// Job abort was requested.
    /// </summary>
    Aborting,
    
    /// <summary>
    /// Job was aborted.
    /// </summary>
    Aborted,
    
    /// <summary>
    /// Finalization started.
    /// </summary>
    FinalizingStarted,
    
    /// <summary>
    /// Job state was cleared.
    /// </summary>
    ClearingStarted,
    
    /// <summary>
    /// Job state was cleared and is now in building state.
    /// </summary>
    ClearingFinished,
    
    /// <summary>
    /// Job was deleted.
    /// </summary>
    Deleted
}

public static class EventTypeExtensions
{
    public static EventType ToEventType(this JobStatus status)
    {
        return status switch
        {
            JobStatus.Building => EventType.Created,
            JobStatus.Waiting => EventType.WaitingStarted,
            JobStatus.Staging => EventType.StagingStarted,
            JobStatus.Running => EventType.RunningStarted,
            JobStatus.Finalizing => EventType.RunningStarted,
            JobStatus.Finished => EventType.Finished,
            JobStatus.Failed => EventType.Failed,
            JobStatus.Aborting => EventType.Aborting,
            JobStatus.Aborted => EventType.Aborted,
            JobStatus.Clearing => EventType.ClearingStarted,
            JobStatus.Deleted => EventType.Deleted,
        };
    }
}

/// <summary>
/// Represents a single event in a job's lifecycle.
/// </summary>
public class JobEvent
{
    /// <summary>
    /// Type of the event.
    /// </summary>
    public EventType Type { get; set; }
    
    /// <summary>
    /// Timestamp when the event occurred.
    /// </summary>
    public DateTime Timestamp { get; set; }
    
    /// <summary>
    /// User who initiated the event, if applicable.
    /// </summary>
    public User Author { get; set; }
    
    /// <summary>
    /// Creates a new event with the specified type, timestamp, and author.
    /// </summary>
    public JobEvent(EventType type, DateTime timestamp, User author = null)
    {
        Type = type;
        Timestamp = timestamp;
        Author = author;
    }
    
    public ReadOnlyJobEvent AsReadOnly() => new(this);
}

public enum TraversalDirection
{
    Both = 3,
    Ancestors = 1 << 0,
    Descendants = 1 << 1
}

public class JobTypeGroup
{
    public string Name;
    public List<JobTypeGroup> Subgroups = new();
    public List<(string Name, Type Type)> Types = new();

    public JobTypeGroup(string name)
    {
        Name = name;
    }

    public bool HasAnyDescendants(Func<(string Name, Type Type), bool> qualifier) =>
        Subgroups.Any(g => g.HasAnyDescendants(qualifier)) || Types.Any(t => qualifier(t));
}

public class HideFromMenuAttribute : Attribute;