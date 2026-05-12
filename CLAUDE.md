# Relay - Developer Guide

## Core Concepts

Relay is a platform for cryo-EM data processing workflows with these key components:

- **Job**: Processing operation with parameters, inputs, and outputs
- **Port**: Connection point on a job (input or output)
- **Edge**: Connection between ports for data flow
- **Resource**: Data flowing between jobs (e.g., images, volumes, coordinates)
- **Space**: Collection of jobs forming a workflow
- **View**: Visual arrangement of jobs in a space
- **Project**: Container for spaces

## Object Model

### Base Classes

#### RelayBase (`/Refund/DataModel/RelayBase.cs`)
- Base for all serializable objects
- Key methods: `WriteToJson()`, `ReadFromJson()`, `AdoptState()`, `ToJson()`
- Handles primitive types, arrays, vectors, and nested objects

#### Job (`/Refund/DataModel/Job.cs`)
- Core properties: `Id`, `DirectoryName`, `Alias`, `Status`, `PortsIn`, `PortsOut`
- Status flow: Building → Waiting → Staging → Running → Finalizing → Finished
- Can fail or be aborted from any active state
- Key methods: `ValidateInputs()`, `Stage()`, `GetParents()/GetChildren()`

#### Port (`/Refund/DataModel/Port.cs`)
- Properties: `Job`, `ResourceType`, `Name`, `Alias`, `Edges`
- Subclasses: `PortIn` (with `MinItems/MaxItems`) and `PortOut` (with `ResourceDelegate`)

#### Edge (`/Refund/DataModel/Edge.cs`)
- Connects `PortOut` to `PortIn`
- Properties: `Id`, `Source`, `Target`, `Space`

#### Space (`/Refund/DataModel/Space.cs`)
- Properties: `Id`, `Project`, `RootDirectory`, `Jobs`, `Edges`, `Views`
- Key methods: `CreateJob()`, `CreateEdge()`, `GetRootJobs()`, `GetLeafJobs()`

#### Project (`/Refund/DataModel/Project.cs`)
- Properties: `Id`, `Alias`, `Owner`, `Members`, `Spaces`
- Key methods: `CreateSpace()`, `LoadSpaces()`, `AddMember()`

### ReadOnly Pattern

- Each model class has a read-only counterpart (`ReadOnlyJob`, `ReadOnlySpace`, etc.)
- Located in `/Refund/DataModel/ReadOnly/`
- Immutable wrappers exposing only getters
- Access through `AsReadOnly()` method on mutable objects
- Created via weak references to avoid memory leaks

### Job Implementation

- Abstract job types:
  - `RelionJob`: Base for RELION-based processing
  - `WarpJob`: Base for Warp-based processing
- Job interfaces:
  - `ILocalJob`: Can run locally (`RunLocal()` method)
  - `IClusterJob`: Can run on cluster (`RunFake()` for testing)
- To implement a new job type:
  1. Create class inheriting from appropriate base
  2. Define input/output ports in constructor
  3. Implement required interface methods
  4. Register job type in `Job.Types` dictionary

## UI Field System

- Attribute-based parameter definition in `/Refund/UIFields/`
- Attach attributes to job properties: `[UiDecimal(label: "Resolution")]`
- Base class: `UiFieldBase` with properties `CliName`, `Label`, `HelpText`, etc.
- Field types: `UiBool`, `UiDecimal`, `UiPath`, `UiEnum`, `UiString`, etc.
- Views automatically generated based on property type

## Service Architecture

### Core Services

#### DataManager (`/Refund/Services/Core/DataManager/DataManager.cs`)
- Central hub for all data operations
- Provides CRUD operations for all model objects
- Emits events when data changes (use `Add()` to subscribe, `Unsubscribe()` to clean up)
- Manages project/user repositories and job queues

#### RelaySession (`/Refund/Services/Core/Session/RelaySession.cs`)
- Tracks current context (user, project, space, view, job)
- Handles navigation between different parts of the application
- Use `NavigateToAsync()` to change context

#### Authentication (`/Refund/Services/AuthenticationService.cs`)
- Handles user login, SSO, and token management
- Uses PKCE for secure OAuth flows

### Job Queue System

- `LocalQueue`: Runs jobs on local machine
- `ClusterQueue`: Submits jobs to HPC cluster
- Job lifecycle:
  1. Building (configuring) → Waiting (queued)
  2. Staging (setup) → Running → Finalizing (cleanup)
  3. Terminal states: Finished, Failed, Aborted, Deleted

## Resource Types

Resources passed between jobs include:
- `Map`: 3D volume data with half-maps, masks, and FSC
- `MicrographSet`: Collection of 2D images
- `ParticleSet`: Extracted particles with coordinates
- `PositionSet`: 3D coordinates
- `TemplateSet`: Reference volumes for template matching
- `DataSetFs`/`DataSetTs`: From frames or tilt series

## Implementation Patterns

### Event System

The system uses a hierarchical event system with `GroupEvent<T>` and pattern-matching group names:

```csharp
// GroupName utility for creating standardized event group names
public static class GroupName
{
    public static string Job(int? projectId, int? spaceId, int? jobId)
    {
        // Format: "P{projectId}_S{spaceId}_J{jobId}" with "*" for wildcards
        return $"P{projectId?.ToString() ?? "*"}_S{spaceId?.ToString() ?? "*"}_J{jobId?.ToString() ?? "*"}";
    }
}
```

### Component Lifecycle

Components should follow this pattern for proper event handling:

```csharp
public partial class JobCard : ComponentBase, IDisposable
{
    [Inject] private DataManager DataManager { get; set; }
    [Parameter] public required ReadOnlyJob Job { get; set; }
    private ReadOnlyJob _job;
    
    // Track subscriptions for cleanup
    private readonly List<GroupEventSubscription> _subscriptions = new();
    
    protected override async Task OnParametersSetAsync()
    {
        if (Job != _job)
        {
            _job = Job;
            
            // Clean up existing subscriptions when job changes
            _subscriptions.UnsubscribeAndClear();
            
            if (_job != null)
            {
                // Subscribe to job-specific events with precise targeting
                _subscriptions.Add(DataManager.JobUpdated.Add(
                    GroupName.Job(_job.Space.Project.Id, _job.Space.Id, _job.Id),
                    async (_) => await InvokeAsync(StateHasChanged)));
                
                _subscriptions.Add(DataManager.JobDeleted.Add(
                    GroupName.Job(_job.Space.Project.Id, _job.Space.Id, _job.Id),
                    async (_) => Dispose()));
            }
        }
    }
    
    // Clean up all subscriptions
    public void Dispose()
    {
        _subscriptions.UnsubscribeAndClear();
    }
}
```

### Creating and Connecting Jobs

Creating jobs follows this multi-layered pattern:

```csharp
// UI Component level (e.g., ViewScreen.razor.cs)
private async Task HandleMenuTypeSelected(Type type)
{
    try
    {
        // Create template job with default state
        Job template = Activator.CreateInstance(type) as Job;
        template.Status = JobStatus.Building;

        // Create job through DataManager
        var newJob = await DataManager.CreateJob(
            Session.User,      // Current user
            Session.View,      // Current view
            template.TypeCategory, // Job type category string
            template           // Template with default values
        );
        
        // Open job editor for parameter configuration
        await JobEditor.SetJob(newJob);
    }
    catch (Exception exc)
    {
        ToastService.ShowError("Couldn't create job:\n" + exc.Message);
    }
}

// Connecting jobs via ports
private async Task HandlePortConnected((ReadOnlyPortOut portOut, ReadOnlyPortIn portIn) args)
{
    try
    {
        if (args.portOut.ResourceType != args.portIn.ResourceType)
            throw new Exception("Can't connect ports of different types");

        await DataManager.CreateEdge(Session.Space, args.portOut, args.portIn);
    }
    catch (Exception exc)
    {
        ToastService.ShowError("Couldn't connect job: " + exc.Message);
    }
}
```

### Queuing a Job for Execution

```csharp
// From DataManager.Job.cs
public async Task QueueLocalJob(ReadOnlyUser user, ReadOnlyJob job)
{
    await ExecuteWithLock(async () =>
    {
        // Find original mutable objects
        User originalUser = _userRepository.FindUser(user.Id);
        Job originalJob = _dataRepository.FindJob(job.Space.Project.Id, job.Space.Id, job.Id);
        
        // Validate transition
        if (!originalJob.CanTransitionState(JobStatus.Waiting))
            throw new Exception("Job cannot be started.");

        // Update job state
        _dataRepository.UpdateJob(originalUser, originalJob, j =>
        {
            j.Status = JobStatus.Waiting;
            j.SubmissionDate = DateTime.Now;
        });

        // Queue the job
        _queueRepository.QueueLocalJob(originalJob);
    });

    // Notify all subscribers about the job update
    var eventArgs = new GroupEventArgs<ReadOnlyJob>(job);
    await JobUpdated.Invoke(GroupName.Job(job.Space.Project.Id, job.Space.Id, job.Id), eventArgs);
    await JobQueued.Invoke(GroupName.Job(job.Space.Project.Id, job.Space.Id, job.Id), eventArgs);
}
```

### Secure File Handling

```csharp
// From FileService.cs
public class FileService
{
    private readonly ConcurrentDictionary<string, string> _pathToHash = new();
    private readonly ConcurrentDictionary<string, string> _hashToPath = new();

    // Create secure URL from actual file path
    public string GetUrl(string filePath)
    {
        if(_pathToHash.TryGetValue(filePath, out var existingHash))
            return $"/api/file/{existingHash}";

        var newHash = GetHash(filePath);
        _pathToHash[filePath] = newHash;
        _hashToPath[newHash] = filePath;

        return $"/api/file/{newHash}";
    }

    // Retrieve file path from secure hash (for API controller)
    public bool TryGetPath(string hash, out string filePath) => 
        _hashToPath.TryGetValue(hash, out filePath);
}
```

## Domain-Specific Terms

| Term | Description |
|------|-------------|
| **CTF** | Contrast Transfer Function - describes microscope image formation |
| **FSC** | Fourier Shell Correlation - measures 3D reconstruction resolution |
| **Fs** | Frame Series - images collected from electron microscope |
| **Ts** | Tilt Series - images at different angles for tomography |
| **SNR** | Signal-to-Noise Ratio - measure of data quality |