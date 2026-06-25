using System.Collections.ObjectModel;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobQueues;
using Refund.Services.Core.Repositories;

namespace Refund.Services.Core.DataManager;

/// <summary>
/// The DataManager is the central service that manages all data access and manipulation in the Relay system.
/// It serves as the single point of truth for all application state, coordinating access to projects, spaces,
/// jobs, users, and queues. The DataManager handles data persistence, concurrency control, and change notification.
/// 
/// The class is structured as a set of partial class files organized by entity type, with this file containing
/// the core infrastructure and common functionality.
/// </summary>
public partial class DataManager
{
    private readonly DataRepository _dataRepository;
    private readonly UserRepository _userRepository;
    private readonly QueueRepository _queueRepository;
    /// <summary>
    /// Global lock to ensure thread safety when modifying data.
    /// This lock is acquired before any operation that modifies the data model
    /// to prevent race conditions and ensure consistency.
    /// </summary>
    private readonly SemaphoreSlim _globalLock = new(1, 1);

    /// <summary>
    /// Gets a read-only collection of all projects in the system.
    /// Projects are wrapped in read-only wrappers to prevent accidental modification.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyProject> Projects => 
        _dataRepository.Projects.Select(p => p.AsReadOnly()).ToList().AsReadOnly();
    
    /// <summary>
    /// Gets a read-only collection of all users in the system.
    /// Users are wrapped in read-only wrappers to prevent accidental modification.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyUser> Users => 
        _userRepository.Users.Select(u => u.AsReadOnly()).ToList().AsReadOnly();
    
    /// <summary>
    /// Gets the read-only reference to the local job queue.
    /// The local queue is used for executing jobs on the same machine where Relay is running.
    /// </summary>
    public ReadOnlyJobQueue LocalQueue => _queueRepository.LocalQueue.AsReadOnly();
    
    /// <summary>
    /// Gets a read-only collection of all cluster queues configured in the system.
    /// Cluster queues are used for executing jobs on remote computing clusters.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyJobQueue> ClusterQueues => _queueRepository.ClusterQueues
                                                                                 .Select(q => q.AsReadOnly())
                                                                                 .ToList()
                                                                                 .AsReadOnly();

    #region Group events with read-only types
    
    #region User events

    /// <summary>
    /// Raised when a user is created <br/>
    /// The only valid group is "" because there are no types above users in the hierarchy
    /// </summary>
    public GroupEvent<ReadOnlyUser> UserCreated { get; } = new();

    /// <summary>
    /// Raised when a user is updated <br/>
    /// Valid groups are "U*" and "U{userId}"
    /// </summary>
    public GroupEvent<ReadOnlyUser> UserUpdated { get; } = new();

    /// <summary>
    /// Raised when a user is deleted <br/>
    /// Valid groups are "U*" and "U{userId}"
    /// </summary>
    public GroupEvent<ReadOnlyUser> UserDeleted { get; } = new();
    
    #endregion
    
    #region Project events

    /// <summary>
    /// Raised when a project is created <br/>
    /// The only valid group is "" because there are no types above projects in the hierarchy
    /// </summary>
    public GroupEvent<ReadOnlyProject> ProjectCreated { get; } = new();

    /// <summary>
    /// Raised when a project is updated <br/>
    /// Valid groups are "P*" and "P{projectId}"
    /// </summary>
    public GroupEvent<ReadOnlyProject> ProjectUpdated { get; } = new();

    /// <summary>
    /// Raised when a project is deleted <br/>
    /// Valid groups are "P*" and "P{projectId}"
    /// </summary>
    public GroupEvent<ReadOnlyProject> ProjectDeleted { get; } = new();
    
    #endregion
    
    #region Space events

    /// <summary>
    /// Raised when a space is created <br/>
    /// Valid groups are "P*" and "P{projectId}"
    /// </summary>
    public GroupEvent<ReadOnlySpace> SpaceCreated { get; } = new();

    /// <summary>
    /// Raised when a space is updated <br/>
    /// Valid groups are "P*_S*", "P{projectId}_S*", "P{projectId}_S{spaceId}"
    /// </summary>
    public GroupEvent<ReadOnlySpace> SpaceUpdated { get; } = new();

    /// <summary>
    /// Raised when a space is deleted <br/>
    /// Valid groups are "P*_S*", "P{projectId}_S*", "P{projectId}_S{spaceId}"
    /// </summary>
    public GroupEvent<ReadOnlySpace> SpaceDeleted { get; } = new();
    
    #endregion
    
    #region Job events

    /// <summary>
    /// Raised when a job is created <br/>
    /// Valid groups are "P*_S*", "P{projectId}_S*", "P{projectId}_S{spaceId}"
    /// </summary>
    public GroupEvent<ReadOnlyJob> JobCreated { get; } = new();

    /// <summary>
    /// Raised when a job is updated <br/>
    /// Valid groups are "P*_S*_J*", "P{projectId}_S*_J*", "P{projectId}_S{spaceId}_J*", "P{projectId}_S{spaceId}_J{jobId}"
    /// </summary>
    public GroupEvent<ReadOnlyJob> JobUpdated { get; } = new();

    /// <summary>
    /// Raised when a job is deleted <br/>
    /// Valid groups are "P*_S*_J*", "P{projectId}_S*_J*", "P{projectId}_S{spaceId}_J*", "P{projectId}_S{spaceId}_J{jobId}"
    /// </summary>
    public GroupEvent<ReadOnlyJob> JobDeleted { get; } = new();
        
    /// <summary>
    /// Raised when a job is queued, i.e. transitions to JobStatus.Waiting <br/>
    /// Valid groups are "P*_S*_J*", "P{projectId}_S*_J*", "P{projectId}_S{spaceId}_J*", "P{projectId}_S{spaceId}_J{jobId}"
    /// </summary>
    public GroupEvent<ReadOnlyJob> JobQueued { get; } = new();
        
    /// <summary>
    /// Raised when a job is finished <br/>
    /// Valid groups are "P*_S*_J*", "P{projectId}_S*_J*", "P{projectId}_S{spaceId}_J*", "P{projectId}_S{spaceId}_J{jobId}"
    /// </summary>
    public GroupEvent<ReadOnlyJob> JobFinished { get; } = new();
        
    /// <summary>
    /// Raised when a job fails <br/>
    /// Valid groups are "P*_S*_J*", "P{projectId}_S*_J*", "P{projectId}_S{spaceId}_J*", "P{projectId}_S{spaceId}_J{jobId}"
    /// </summary>
    public GroupEvent<ReadOnlyJob> JobFailed { get; } = new();
    
    #endregion
    
    #region Edge events

    /// <summary>
    /// Raised when an edge is created <br/>
    /// Valid groups are "P*_S*", "P{projectId}_S*", "P{projectId}_S{spaceId}"
    /// </summary>
    public GroupEvent<ReadOnlyEdge> EdgeCreated { get; } = new();

    /// <summary>
    /// Raised when an edge is updated <br/>
    /// Valid groups are "P*_S*_E*", "P{projectId}_S*_E*", "P{projectId}_S{spaceId}_E*", "P{projectId}_S{spaceId}_E{edgeId}"
    /// </summary>
    public GroupEvent<ReadOnlyEdge> EdgeUpdated { get; } = new();

    /// <summary>
    /// Raised when an edge is deleted <br/>
    /// Valid groups are "P*_S*_E*", "P{projectId}_S*_E*", "P{projectId}_S{spaceId}_E*", "P{projectId}_S{spaceId}_E{edgeId}"
    /// </summary>
    public GroupEvent<ReadOnlyEdge> EdgeDeleted { get; } = new();
    
    #endregion
    
    #region View events

    /// <summary>
    /// Raised when a view is created <br/>
    /// Valid groups are "P*_S*", "P{projectId}_S*", "P{projectId}_S{spaceId}"
    /// </summary>
    public GroupEvent<ReadOnlyView> ViewCreated { get; } = new();

    /// <summary>
    /// Raised when a view is updated <br/>
    /// Valid groups are "P*_S*_V*", "P{projectId}_S*_V*", "P{projectId}_S{spaceId}_V*", "P{projectId}_S{spaceId}_V{viewId}"
    /// </summary>
    public GroupEvent<ReadOnlyView> ViewUpdated { get; } = new();

    /// <summary>
    /// Raised when a view is deleted <br/>
    /// Valid groups are "P*_S*_V*", "P{projectId}_S*_V*", "P{projectId}_S{spaceId}_V*", "P{projectId}_S{spaceId}_V{viewId}"
    /// </summary>
    public GroupEvent<ReadOnlyView> ViewDeleted { get; } = new();

    #endregion

    #region FactoryDefinition events

    public GroupEvent<ReadOnlyFactoryDefinition> FactoryDefinitionCreated { get; } = new();
    public GroupEvent<ReadOnlyFactoryDefinition> FactoryDefinitionUpdated { get; } = new();
    public GroupEvent<ReadOnlyFactoryDefinition> FactoryDefinitionDeleted { get; } = new();

    #endregion

    #region FactoryInstance events

    public GroupEvent<ReadOnlyFactoryInstance> FactoryInstanceCreated { get; } = new();
    public GroupEvent<ReadOnlyFactoryInstance> FactoryInstanceUpdated { get; } = new();
    public GroupEvent<ReadOnlyFactoryInstance> FactoryInstanceDeleted { get; } = new();

    #endregion

    #region JobQueue events
        
    /// <summary>
    /// Raised when a queue is created
    /// The only valid group is "" because there are no types above queues in the hierarchy
    /// </summary>
    public GroupEvent<ReadOnlyJobQueue> QueueCreated { get; } = new();

    /// <summary>
    /// Raised when a queue is updated
    /// Valid groups are "Q*" and "Q{queueId}"
    /// </summary>
    public GroupEvent<ReadOnlyJobQueue> QueueUpdated { get; } = new();

    /// <summary>
    /// Raised when a queue is deleted
    /// Valid groups are "Q*" and "Q{queueId}"
    /// </summary>
    public GroupEvent<ReadOnlyJobQueue> QueueDeleted { get; } = new();
    
    #endregion

    #endregion

    /// <summary>
    /// Initializes a new instance of the DataManager class.
    /// This constructor sets up all repositories, loads existing data, and initializes background processes
    /// for data persistence and job queue monitoring.
    /// </summary>
    /// <param name="config">The application configuration containing paths to data storage files</param>
    public DataManager(RelayConfiguration config)
    {
        // Initialize and load user repository first, as other entities reference users
        _userRepository = new UserRepository(config.UsersPath);
        _userRepository.LoadUsers();

        // Initialize and load data repository with projects, spaces, and jobs
        _dataRepository = new DataRepository(config.ProjectsPath);
        _dataRepository.LoadAll(_userRepository.Users);

        // Initialize job queue repository with a callback to update job status
        // The callback allows the queue to notify the DataManager when job status changes
        _queueRepository = new QueueRepository(config.QueuesPath, (job, action) =>
        {
            UpdateJob(job.UpdatedBy.AsReadOnly(), job.AsReadOnly(), action).Wait();
        },
        async (job, action) =>
        {
            await UpdateJob(job.UpdatedBy.AsReadOnly(), job.AsReadOnly(), action);
        });
        _queueRepository.LoadQueues(_dataRepository);

        // Handle jobs that were in active states when the application shut down.
        // Jobs already tracked in a queue (via improved persistence that now includes Waiting jobs)
        // will be picked up by the daemon automatically. Jobs NOT in any queue are orphaned and
        // need to be reassigned or marked as failed.
        foreach (var project in _dataRepository.Projects)
            foreach (var space in project.Spaces)
                foreach (var job in space.Jobs)
                {
                    if (job.Status == JobStatus.Clearing)
                    {
                        _dataRepository.UpdateJob(job.UpdatedBy, job, alteredJob =>
                        {
                            alteredJob.Status = JobStatus.Failed;
                            alteredJob.AddEvent(EventType.Failed);
                            Console.WriteLine($"{job.QualifiedName} was clearing at shutdown, marking as failed");
                        });
                    }
                    else if ((job.Status.IsUnsettled() || job.Status == JobStatus.Waiting) &&
                         !_queueRepository.LocalQueue.QueuedJobs.Contains(job) &&
                         !_queueRepository.ClusterQueues.Any(q => q.QueuedJobs.Contains(job)))
                    {
                        // Job is orphaned: in an active state but not tracked by any queue.
                        // Use the persisted QueueId to reassign to the original queue if possible,
                        // otherwise fall back to matching by job's QueueType flags.
                        JobQueue suitableQueue = null;

                        if (job.QueueId.HasValue)
                        {
                            // Try to find the original queue by stored ID
                            suitableQueue = _queueRepository.FindQueue(job.QueueId.Value);
                        }

                        if (suitableQueue == null)
                        {
                            // Fall back to matching by QueueType (for jobs without a stored QueueId,
                            // e.g. jobs created before this feature was added)
                            if (job.QueueType == JobQueueType.Local)
                                suitableQueue = _queueRepository.LocalQueue;
                            else
                                suitableQueue = _queueRepository.ClusterQueues
                                    .FirstOrDefault(q => (q.QueueType & job.QueueType) != 0);
                        }

                        if (suitableQueue != null)
                        {
                            // Update QueueId to reflect the (re)assignment
                            _dataRepository.UpdateJob(job.UpdatedBy, job, j =>
                            {
                                j.QueueId = suitableQueue.Id;
                            });

                            if (suitableQueue == _queueRepository.LocalQueue)
                                suitableQueue.SubmitJob(job);
                            else
                                suitableQueue.Enqueue(job);
                        }
                        else
                        {
                            Console.WriteLine($"Couldn't find queue for orphaned {job.QualifiedName} (QueueId={job.QueueId}, QueueType={job.QueueType})");
                        }
                    }
                }

        // Backfill QueueId for jobs that are already tracked in a queue but have no QueueId
        // (e.g. jobs created before this feature was added)
        foreach (var job in _queueRepository.LocalQueue.QueuedJobs)
            if (!job.QueueId.HasValue)
                job.QueueId = _queueRepository.LocalQueue.Id;

        foreach (var queue in _queueRepository.ClusterQueues)
            foreach (var job in queue.QueuedJobs)
                if (!job.QueueId.HasValue)
                    job.QueueId = queue.Id;

        // Detect and remove any cyclic edges that would cause infinite recursion
        // during resource resolution (e.g. PortIn.GetSingleResource → PortOut.GetResource loop)
        foreach (var project in _dataRepository.Projects)
            foreach (var space in project.Spaces)
            {
                var removedEdges = space.RemoveCyclicEdges();
                foreach (var edge in removedEdges)
                {
                    Console.WriteLine($"WARNING: Removed cyclic edge {edge.Id} in {project.Alias}/{space.Id} " +
                        $"(job {edge.Source.Job.Id} -> job {edge.Target.Job.Id}). " +
                        $"Cycles in the job graph cause infinite recursion.");
                    _dataRepository.MarkSpaceForSave(space);
                }
            }

        // Start background auto-save processes for all repositories
        // These ensure that data changes are persisted to disk at regular intervals
        _userRepository.StartAutoSave(500);
        _dataRepository.StartAutoSave(500);
        _queueRepository.StartAutoSave(500);
        
        // Start the job queue daemon process that monitors and manages job execution
        _queueRepository.StartDaemon(1_000);
    }

    #region Entity resolution helpers

    private User ResolveUser(int userId)
        => _userRepository.FindUser(userId) ?? throw new Exception($"User {userId} not found");

    private Project ResolveProject(int projectId)
        => _dataRepository.FindProject(projectId) ?? throw new Exception($"Project {projectId} not found");

    private Space ResolveSpace(int projectId, int spaceId)
        => _dataRepository.FindSpace(projectId, spaceId) ?? throw new Exception($"Space {spaceId} not found");

    private View ResolveView(int projectId, int spaceId, int viewId)
        => _dataRepository.FindView(projectId, spaceId, viewId) ?? throw new Exception($"View {viewId} not found");

    private Job ResolveJob(int projectId, int spaceId, int jobId)
        => _dataRepository.FindJob(projectId, spaceId, jobId) ?? throw new Exception($"Job {jobId} not found");

    private Edge ResolveEdge(int projectId, int spaceId, int edgeId)
        => _dataRepository.FindEdge(projectId, spaceId, edgeId) ?? throw new Exception($"Edge {edgeId} not found");

    private JobQueue ResolveQueue(int queueId)
        => _queueRepository.FindQueue(queueId) ?? throw new Exception($"Queue {queueId} not found");

    private void TouchAndSave(View view, User user)
    {
        view.UpdateDate = DateTime.Now;
        view.UpdatedBy = user;
        _dataRepository.MarkSpaceForSave(view.Space);
    }

    #endregion

    #region Private helper methods

    /// <summary>
    /// Executes an asynchronous function that returns a value while holding the global lock.
    /// This method ensures thread safety for operations that modify data and return a result.
    /// </summary>
    /// <typeparam name="T">The return type of the function</typeparam>
    /// <param name="action">The asynchronous function to execute while holding the lock</param>
    /// <returns>The result of the action</returns>
    private async Task<T> ExecuteWithLock<T>(Func<Task<T>> action)
    {
        await _globalLock.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _globalLock.Release();
        }
    }

    /// <summary>
    /// Executes an asynchronous function with no return value while holding the global lock.
    /// This method ensures thread safety for operations that modify data but don't return a result.
    /// </summary>
    /// <param name="action">The asynchronous function to execute while holding the lock</param>
    private async Task ExecuteWithLock(Func<Task> action)
    {
        await _globalLock.WaitAsync();

        try
        {
            await action();
        }
        finally
        {
            _globalLock.Release();
        }
    }

    private static bool FolderContainsJob(Folder folder, int jobId)
    {
        foreach (var item in folder.Items)
        {
            if (item is Job j && j.Id == jobId) return true;
            if (item is Folder sub && sub.GetAllJobsRecursive().Any(sj => sj.Id == jobId)) return true;
        }
        return false;
    }

    private static void UpdateFolderLayoutsForEdge(Space space, int sourceJobId, int targetJobId)
    {
        foreach (var view in space.Views)
            foreach (var folder in view.Folders)
            {
                bool hasSource = false, hasTarget = false;
                foreach (var item in folder.Items)
                {
                    if (!hasSource && item is Job j1 && j1.Id == sourceJobId) hasSource = true;
                    else if (!hasSource && item is Folder f1 && f1.GetAllJobsRecursive().Any(sj => sj.Id == sourceJobId)) hasSource = true;
                    if (!hasTarget && item is Job j2 && j2.Id == targetJobId) hasTarget = true;
                    else if (!hasTarget && item is Folder f2 && f2.GetAllJobsRecursive().Any(sj => sj.Id == targetJobId)) hasTarget = true;
                    if (hasSource && hasTarget) break;
                }
                if (hasSource && hasTarget)
                    folder.UpdateLayout(space);
            }
    }

    public async Task ResetDiagramLayout(ReadOnlyView view, ReadOnlyFolder? folder)
    {
        await ExecuteWithLock(async () =>
        {
            View originalView = _dataRepository.FindView(view.Space.Project.Id, view.Space.Id, view.Id);
            Space originalSpace = originalView.Space;

            if (folder != null)
            {
                var originalFolder = originalView.FindFolder(folder.Id);
                originalFolder?.ResetDiagramLayout(originalSpace);
            }
            else
            {
                originalView.ResetDiagramLayout(originalSpace);
            }

            _dataRepository.MarkSpaceForSave(originalSpace);
        });

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    public async Task ResetFactoryInstanceDiagramLayout(ReadOnlyFactoryInstance instance)
    {
        await ExecuteWithLock(async () =>
        {
            var originalSpace = ResolveSpace(instance.Space.Project.Id, instance.Space.Id);
            var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                ?? throw new Exception($"Factory instance {instance.Id} not found");

            originalInst.ResetDiagramLayout(originalSpace);
            _dataRepository.MarkSpaceForSave(originalSpace);
        });

        await FactoryInstanceUpdated.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(instance.Space.Project.Id, instance.Space.Id, instance.Id));
    }

    private static void UpdateDiagramLayoutsForEdge(Space space, int sourceJobId, int targetJobId)
    {
        foreach (var view in space.Views)
        {
            bool viewHasSource = view.Jobs.Any(j => j.Id == sourceJobId);
            bool viewHasTarget = view.Jobs.Any(j => j.Id == targetJobId);
            if (viewHasSource || viewHasTarget)
                view.UpdateDiagramLayout(space);

            foreach (var folder in view.Folders)
            {
                bool hasSource = false, hasTarget = false;
                foreach (var item in folder.Items)
                {
                    if (!hasSource && item is Job j1 && j1.Id == sourceJobId) hasSource = true;
                    else if (!hasSource && item is Folder f1 && f1.GetAllJobsRecursive().Any(sj => sj.Id == sourceJobId)) hasSource = true;
                    if (!hasTarget && item is Job j2 && j2.Id == targetJobId) hasTarget = true;
                    else if (!hasTarget && item is Folder f2 && f2.GetAllJobsRecursive().Any(sj => sj.Id == targetJobId)) hasTarget = true;
                    if (hasSource && hasTarget) break;
                }
                if (hasSource && hasTarget)
                    folder.UpdateDiagramLayout(space);
            }
        }

        // Update factory instance layouts if edge involves their sub-jobs
        foreach (var fi in space.FactoryInstances)
        {
            if (fi.SubJobIds.Contains(sourceJobId) || fi.SubJobIds.Contains(targetJobId))
                fi.UpdateDiagramLayout(space);
        }
    }

    #endregion

    #region Find methods

    /// <summary>
    /// Finds a project by its unique identifier.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project to find</param>
    /// <returns>A read-only wrapper of the found project, or null if no project with the specified ID exists</returns>
    public ReadOnlyProject FindProject(int projectId)
    {
        var project = _dataRepository.FindProject(projectId);
        return project?.AsReadOnly();
    }

    /// <summary>
    /// Finds a user by their unique identifier.
    /// </summary>
    /// <param name="userId">The unique identifier of the user to find</param>
    /// <returns>A read-only wrapper of the found user, or null if no user with the specified ID exists</returns>
    public ReadOnlyUser FindUser(int userId)
    {
        var user = _userRepository.FindUser(userId);
        return user?.AsReadOnly();
    }

    /// <summary>
    /// Finds a user by their username.
    /// </summary>
    /// <param name="username">The username of the user to find</param>
    /// <returns>A read-only wrapper of the found user, or null if no user with the specified username exists</returns>
    public ReadOnlyUser FindUser(string username)
    {
        var user = _userRepository.FindUser(username);
        return user?.AsReadOnly();
    }

    /// <summary>
    /// Finds a space by its project and space identifiers.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project containing the space</param>
    /// <param name="spaceId">The unique identifier of the space to find within the project</param>
    /// <returns>A read-only wrapper of the found space, or null if no space with the specified IDs exists</returns>
    public ReadOnlySpace FindSpace(int projectId, int spaceId)
    {
        var space = _dataRepository.FindSpace(projectId, spaceId);
        return space?.AsReadOnly();
    }

    /// <summary>
    /// Finds a job by its project, space, and job identifiers.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project containing the job</param>
    /// <param name="spaceId">The unique identifier of the space containing the job</param>
    /// <param name="jobId">The unique identifier of the job to find within the space</param>
    /// <returns>A read-only wrapper of the found job, or null if no job with the specified IDs exists</returns>
    public ReadOnlyJob FindJob(int projectId, int spaceId, int jobId)
    {
        var job = _dataRepository.FindJob(projectId, spaceId, jobId);
        return job?.AsReadOnly();
    }

    /// <summary>
    /// Finds an edge by its project, space, and edge identifiers.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project containing the edge</param>
    /// <param name="spaceId">The unique identifier of the space containing the edge</param>
    /// <param name="edgeId">The unique identifier of the edge to find within the space</param>
    /// <returns>A read-only wrapper of the found edge, or null if no edge with the specified IDs exists</returns>
    public ReadOnlyEdge FindEdge(int projectId, int spaceId, int edgeId)
    {
        var edge = _dataRepository.FindEdge(projectId, spaceId, edgeId);
        return edge?.AsReadOnly();
    }

    /// <summary>
    /// Finds a view by its project, space, and view identifiers.
    /// </summary>
    /// <param name="projectId">The unique identifier of the project containing the view</param>
    /// <param name="spaceId">The unique identifier of the space containing the view</param>
    /// <param name="viewId">The unique identifier of the view to find within the space</param>
    /// <returns>A read-only wrapper of the found view, or null if no view with the specified IDs exists</returns>
    public ReadOnlyView FindView(int projectId, int spaceId, int viewId)
    {
        var view = _dataRepository.FindView(projectId, spaceId, viewId);
        return view?.AsReadOnly();
    }

    /// <summary>
    /// Finds a cluster queue by its unique identifier.
    /// </summary>
    /// <param name="queueId">The unique identifier of the cluster queue to find</param>
    /// <returns>A read-only wrapper of the found cluster queue, or null if no queue with the specified ID exists</returns>
    public ReadOnlyJobQueue FindClusterQueue(int queueId)
    {
        var queue = _queueRepository.FindQueue(queueId);
        return queue?.AsReadOnly();
    }

    #endregion
}