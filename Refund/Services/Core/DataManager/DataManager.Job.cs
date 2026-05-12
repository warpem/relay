using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    #region Public methods for data manipulation

    /// <summary>
    /// Creates a new job in the specified view.
    /// </summary>
    /// <param name="user">The user creating the job</param>
    /// <param name="view">The view in which to create the job</param>
    /// <param name="typeGuid">The type category of the job to create</param>
    /// <param name="template">Optional template job to copy parameters from</param>
    /// <returns>A read-only wrapper of the created job</returns>
    /// <exception cref="Exception">Thrown if user, space, or view cannot be found, or if job creation fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// It creates the job in the specified view, adds it to the space, and raises events
    /// for all affected entities.
    /// </remarks>
    public async Task<ReadOnlyJob> CreateJob(ReadOnlyUser user, ReadOnlyView view, string typeGuid, Job template = null, ReadOnlyFolder targetFolder = null)
    {
        ReadOnlyJob createdJob = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);

                View originalView = originalSpace.FindView(view.Id);
                if (originalView == null)
                    throw new Exception($"View {view.Id} not found");

                Folder folder = null;
                if (targetFolder != null)
                {
                    folder = originalView.FindFolder(targetFolder.Id);
                    if (folder == null)
                        throw new Exception($"Folder {targetFolder.Id} not found");
                }

                Job newJob = _dataRepository.CreateJob(originalUser, originalSpace, originalView, typeGuid, template);

                // If a target folder was specified, move the job into it
                if (folder != null)
                {
                    originalView.MoveJobToFolder(newJob, folder);
                    folder.UpdateLayout(originalSpace);
                    folder.UpdateDiagramLayout(originalSpace);
                }

                originalView.UpdateDiagramLayout(originalSpace);

                createdJob = newJob.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to create job for user {UserId} in view {ViewId}", user.Id, view.Id);
                throw;
            }
        });

        await JobCreated.InvokeHierarchy(createdJob, GroupName.JobHierarchy(view.Space.Project.Id, view.Space.Id, null));
        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
        await SpaceUpdated.InvokeHierarchy(createdJob.Space, GroupName.SpaceHierarchy(createdJob.Space.Project.Id, createdJob.Space.Id));

        return createdJob;
    }

    /// <summary>
    /// Updates an existing job by applying the specified update action.
    /// </summary>
    /// <param name="user">The user updating the job</param>
    /// <param name="job">The job to update</param>
    /// <param name="updateAction">The action to apply to the job</param>
    /// <returns>A task that completes when the update operation is finished</returns>
    /// <exception cref="Exception">Thrown if user or job cannot be found, or if job update fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// The update action is applied to the mutable job object within a lock to ensure consistency.
    /// After the update, events are raised to notify all interested subscribers.
    /// </remarks>
    public async Task UpdateJob(ReadOnlyUser user, ReadOnlyJob job, Action<Job> updateAction)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                // Apply the update action to the job
                _dataRepository.UpdateJob(originalUser, originalJob, updateAction);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to update job {JobId} by user {UserId}", job.Id, user.Id);
                throw;
            }
        });

        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    /// <summary>
    /// Deletes an existing job.
    /// </summary>
    /// <param name="user">The user deleting the job</param>
    /// <param name="job">The job to delete</param>
    /// <returns>A task that completes when the delete operation is finished</returns>
    /// <exception cref="Exception">Thrown if user or job cannot be found, if the job cannot be deleted, or if deletion fails</exception>
    /// <remarks>
    /// This method handles both the logical deletion in the data model and the physical deletion of job files from disk.
    /// The logical deletion occurs within a lock to ensure consistency, but the physical deletion is performed
    /// asynchronously to avoid blocking. After deletion, events are raised to notify all interested subscribers.
    /// </remarks>
    public async Task DeleteJob(ReadOnlyUser user, ReadOnlyJob job)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                // Check if the job is in a state that allows deletion
                if (!originalJob.CanTransitionState(JobStatus.Deleted))
                    throw new Exception("Job cannot be deleted.");

                // Find containing folders before deletion
                Space originalSpace = _dataRepository.FindSpace(job.Space.Project.Id, job.Space.Id);

                // Guard: block deletion if this job is referenced by any factory definition's external edges
                if (originalSpace != null)
                {
                    foreach (var def in originalSpace.FactoryDefinitions)
                    {
                        if (def.ExternalEdges.Any(e => e.ExternalJobId == originalJob.Id))
                            throw new Exception(
                                $"Job cannot be deleted because it is referenced by factory definition '{def.QualifiedName}'. " +
                                "Delete the factory definition first.");
                    }
                }

                // Guard: block direct deletion of factory sub-jobs
                if (originalJob.FactoryInstanceId.HasValue)
                    throw new Exception(
                        "This job belongs to a factory instance and cannot be deleted directly. " +
                        "Delete the factory instance instead.");

                var affectedFolders = new List<Folder>();
                var affectedViews = new HashSet<View>();
                if (originalSpace != null)
                    foreach (var v in originalSpace.Views)
                        foreach (var folder in v.Folders)
                            if (FolderContainsJob(folder, originalJob.Id))
                            {
                                affectedFolders.Add(folder);
                                affectedViews.Add(v);
                            }

                // Also track views that directly contain this job (not in a folder)
                if (originalSpace != null)
                    foreach (var v in originalSpace.Views)
                        if (v.Jobs.Contains(originalJob))
                            affectedViews.Add(v);

                // Delete the job from the data model
                _dataRepository.DeleteJob(originalUser, originalJob);

                if (originalSpace != null)
                {
                    foreach (var folder in affectedFolders)
                    {
                        folder.UpdateLayout(originalSpace);
                        folder.UpdateDiagramLayout(originalSpace);
                    }
                    foreach (var v in affectedViews)
                        v.UpdateDiagramLayout(originalSpace);
                }
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to delete job {JobId} by user {UserId}", job.Id, user.Id);
                throw;
            }
        });

        // Physical deletion can take time, so we don't want to block
        // This runs in a separate task to avoid blocking the UI while files are deleted
        await Task.Run(() =>
        {
            if (!string.IsNullOrWhiteSpace(job.DirectoryName) && Directory.Exists(job.DirectoryPath))
                Directory.Delete(job.DirectoryPath, true);
        });

        await JobDeleted.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
        await SpaceUpdated.InvokeHierarchy(job.Space, GroupName.SpaceHierarchy(job.Space.Project.Id, job.Space.Id));
    }

    /// <summary>
    /// Creates a clone of an existing job in the specified view.
    /// </summary>
    /// <param name="user">The user performing the clone operation</param>
    /// <param name="job">The job to clone</param>
    /// <param name="view">The view in which to create the cloned job</param>
    /// <returns>A read-only wrapper of the cloned job</returns>
    /// <exception cref="Exception">Thrown if user, job, space, or view cannot be found, or if cloning fails</exception>
    /// <remarks>
    /// This method creates a new job with the same parameters as the source job but with a new identity.
    /// The cloned job will be in the Building state regardless of the source job's state.
    /// If the source job has connections to other jobs, those connections will be preserved in the clone.
    /// </remarks>
    public async Task<ReadOnlyJob> CloneJob(ReadOnlyUser user, ReadOnlyJob job, ReadOnlyView view)
    {
        ReadOnlyJob clonedJob = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);

                // Find the original mutable view object by ID
                View originalView = originalSpace.FindView(view.Id);
                if (originalView == null)
                    throw new Exception($"View {view.Id} not found");

                // Clone the job via the repository and return a read-only wrapper
                Job newJob = _dataRepository.CloneJob(originalUser, originalSpace, originalJob, originalView);

                clonedJob = newJob.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to clone job {JobId} by user {UserId} to view {ViewId}", job.Id, user.Id, view.Id);
                throw;
            }
        });

        await JobCreated.InvokeHierarchy(clonedJob, GroupName.JobHierarchy(view.Space.Project.Id, view.Space.Id, null));

        // Every parent got connected to the new job from within DataRepository, thus without raising events
        // We need to manually notify about updates to the parent jobs
        foreach (var parent in clonedJob.GetParents())
        {
            await JobUpdated.InvokeHierarchy(parent, GroupName.JobHierarchy(parent.Space.Project.Id, parent.Space.Id, parent.Id));
        }

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
        await SpaceUpdated.InvokeHierarchy(clonedJob.Space, GroupName.SpaceHierarchy(clonedJob.Space.Project.Id, clonedJob.Space.Id));

        return clonedJob;
    }

    /// <summary>
    /// Clones multiple interconnected jobs as a tree, preserving internal connections
    /// while keeping external connections intact.
    /// </summary>
    /// <param name="user">The user performing the clone operation</param>
    /// <param name="jobs">The jobs to clone as a tree</param>
    /// <param name="view">The view in which to create the cloned jobs</param>
    /// <returns>A task that completes when the clone tree operation is finished</returns>
    /// <exception cref="Exception">Thrown if user, jobs, space, or view cannot be found, or if cloning fails</exception>
    /// <remarks>
    /// This method clones all provided jobs and rewires edges between them so the cloned
    /// subgraph mirrors the original's internal connections. Edges from external (non-cloned)
    /// jobs are preserved as-is. The entire operation is atomic within a single lock.
    /// </remarks>
    public async Task CloneJobTree(ReadOnlyUser user, IEnumerable<ReadOnlyJob> jobs, ReadOnlyView view)
    {
        var clonedReadOnlyJobs = new List<ReadOnlyJob>();
        var clonedJobIds = new HashSet<int>();

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);

                View originalView = originalSpace.FindView(view.Id);
                if (originalView == null)
                    throw new Exception($"View {view.Id} not found");

                // Build set of original job IDs for quick lookup
                var originalJobIds = new HashSet<int>(jobs.Select(j => j.Id));

                // Clone each job and build mapping from original ID to mutable clone
                var cloneMap = new Dictionary<int, Job>();
                foreach (var job in jobs)
                {
                    var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                    Job clone = _dataRepository.CloneJob(originalUser, originalSpace, originalJob, originalView);
                    cloneMap[job.Id] = clone;
                }

                // Rewire internal edges: for each clone, check input edges
                // If an edge's source job was also cloned, replace it with an edge from the clone
                foreach (var (originalId, clone) in cloneMap)
                {
                    foreach (var port in clone.PortsIn)
                    {
                        // Iterate a copy since we'll be modifying the collection
                        var edges = port.Value.Edges.ToList();
                        foreach (var edge in edges)
                        {
                            int sourceJobId = edge.Source.Job.Id;
                            if (originalJobIds.Contains(sourceJobId) && cloneMap.ContainsKey(sourceJobId))
                            {
                                string sourcePortName = edge.Source.Name;
                                string targetPortName = port.Key;

                                _dataRepository.DeleteEdge(edge);
                                _dataRepository.CreateEdge(originalSpace,
                                    cloneMap[sourceJobId].PortsOut[sourcePortName],
                                    clone.PortsIn[targetPortName]);
                            }
                        }
                    }
                }

                // Capture read-only refs for events
                foreach (var clone in cloneMap.Values)
                {
                    clonedReadOnlyJobs.Add(clone.AsReadOnly());
                    clonedJobIds.Add(clone.Id);
                }
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to clone job tree by user {UserId} to view {ViewId}", user.Id, view.Id);
                throw;
            }
        });

        // Fire JobCreated events for each cloned job
        foreach (var clonedJob in clonedReadOnlyJobs)
        {
            await JobCreated.InvokeHierarchy(clonedJob, GroupName.JobHierarchy(view.Space.Project.Id, view.Space.Id, null));
        }

        // Notify external parents about updates (they got new edges to clones)
        // Clone-parents are new and have no existing subscribers, so skip them
        var notifiedParentIds = new HashSet<int>();
        foreach (var clonedJob in clonedReadOnlyJobs)
        {
            foreach (var parent in clonedJob.GetParents())
            {
                if (!clonedJobIds.Contains(parent.Id) && notifiedParentIds.Add(parent.Id))
                {
                    await JobUpdated.InvokeHierarchy(parent, GroupName.JobHierarchy(parent.Space.Project.Id, parent.Space.Id, parent.Id));
                }
            }
        }

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));

        var spaceForEvents = clonedReadOnlyJobs.First().Space;
        await SpaceUpdated.InvokeHierarchy(spaceForEvents, GroupName.SpaceHierarchy(spaceForEvents.Project.Id, spaceForEvents.Id));
    }

    /// <summary>
    /// Clears a job's intermediate and output files, resetting it to the initial state.
    /// </summary>
    /// <param name="user">The user clearing the job</param>
    /// <param name="job">The job to clear</param>
    /// <returns>A task that completes when the clear operation is finished</returns>
    /// <exception cref="Exception">Thrown if the clear operation fails</exception>
    /// <remarks>
    /// This method transitions the job through three states:
    /// 1. Sets the job status to Clearing to indicate clearing is in progress
    /// 2. Performs the actual clearing operation asynchronously
    /// 3. Sets the job status to Building after successful clearing
    ///
    /// The clearing operation deletes all output files while preserving the job's parameters
    /// and configuration, allowing it to be run again from scratch.
    /// </remarks>
    public async Task ClearJob(ReadOnlyUser user, ReadOnlyJob job)
    {
        try
        {
            var originalUser = _userRepository.FindUser(user.Id);

            // First transition the job to the Clearing state
            await UpdateJob(user, job, originalJob =>
            {
                originalJob.AddEvent(EventType.ClearingStarted, originalUser);
                originalJob.Status = JobStatus.Clearing;
            } );
            await Task.Delay(500);

            // Clearing is a potentially long operation, so we don't want to block
            // We run it on a background thread to avoid blocking the UI
            Job originalJob = _dataRepository.FindJob(job.Space.Project.Id, job.Space.Id, job.Id);
            await Task.Run(async () =>
            {
                try
                {
                    originalJob.Clear();

                    // After successful clearing, transition the job back to the Building state
                    await UpdateJob(user, job, originalJob =>
                    {
                        originalJob.AddEvent(EventType.ClearingFinished);
                        originalJob.Status = JobStatus.Building;
                    } );
                }
                catch (Exception ex)
                {
                    Directory.CreateDirectory(job.RelayResultsDirectoryPath);
                    await job.WriteToErrorLog("Failed to clear job:\n" + ex);

                    await UpdateJob(user, job, originalJob =>
                    {
                        originalJob.AddEvent(EventType.Failed);
                        originalJob.Status = JobStatus.Failed;
                    } );
                }
            });
        }
        catch (Exception e)
        {
            Log.ForContext<DataManager>().Error(e, "Failed to clear job {JobId} by user {UserId}", job.Id, user.Id);
            throw;
        }
    }

    /// <summary>
    /// Aborts a running job.
    /// </summary>
    /// <param name="user">The user aborting the job</param>
    /// <param name="job">The job to abort</param>
    /// <returns>A task that completes when the job has been marked for abortion</returns>
    /// <remarks>
    /// This method sets the job status to Aborting, which signals the job queue to
    /// terminate the job's process. The actual abortion is handled by the job queue,
    /// which will eventually transition the job to the Aborted state.
    /// </remarks>
    public async Task AbortJob(ReadOnlyUser user, ReadOnlyJob job)
    {
        var originalUser = _userRepository.FindUser(user.Id);

        await UpdateJob(user, job, originalJob =>
        {
            originalJob.Status = JobStatus.Aborting;
            originalJob.AddEvent(EventType.Aborting, originalUser);
        });
    }

    /// <summary>
    /// Force-aborts a job that is stuck in an active state but not tracked by any queue.
    /// This bypasses normal state transition validation as an emergency recovery mechanism.
    /// </summary>
    public async Task ForceAbortOrphanedJob(ReadOnlyUser user, ReadOnlyJob job)
    {
        var originalUser = _userRepository.FindUser(user.Id);

        await UpdateJob(user, job, originalJob =>
        {
            originalJob.Status = JobStatus.Aborted;
            originalJob.AddEvent(EventType.Aborted, originalUser);
        });
    }

    /// <summary>
    /// Queues a job for execution on the local machine.
    /// </summary>
    /// <param name="user">The user queuing the job</param>
    /// <param name="job">The job to queue</param>
    /// <returns>A task that completes when the job has been queued</returns>
    /// <exception cref="Exception">Thrown if user or job cannot be found, if the job cannot be started, or if queueing fails</exception>
    /// <remarks>
    /// This method performs the following operations:
    /// 1. Verifies the job can transition to the Waiting state
    /// 2. Updates the job status to Waiting and sets the submission date
    /// 3. Adds the job to the local queue for execution
    /// 4. Raises events to notify subscribers about the job status change
    ///
    /// Local execution means the job will run on the same machine where the Relay server is running.
    /// </remarks>
    public async Task QueueLocalJob(ReadOnlyUser user, ReadOnlyJob job)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                // Check if the job can be transitioned to the Waiting state
                if (!originalJob.CanTransitionState(JobStatus.Waiting))
                    throw new Exception("Job cannot be started.");

                // Inherit color from first colored parent if not already set
                if (originalJob.ColorTag == null)
                {
                    var parentColor = originalJob.GetParents()
                        .Select(p => p.ColorTag)
                        .FirstOrDefault(c => c != null);
                    if (parentColor != null)
                        originalJob.ColorTag = parentColor;
                }

                // Update the job status and record which queue it's assigned to
                _dataRepository.UpdateJob(originalUser, originalJob, j =>
                {
                    j.Status = JobStatus.Waiting;
                    j.QueueId = _queueRepository.LocalQueue.Id;
                    j.AddEvent(EventType.WaitingStarted, originalUser);
                });

                // Queue the job for local execution
                _queueRepository.QueueLocalJob(originalJob);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to queue local job {JobId} by user {UserId}", job.Id, user.Id);
                throw;
            }
        });

        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
        await JobQueued.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    /// <summary>
    /// Queues a job for execution on a remote cluster.
    /// </summary>
    /// <param name="user">The user queuing the job</param>
    /// <param name="job">The job to queue</param>
    /// <param name="queue">The cluster queue to submit the job to</param>
    /// <returns>A task that completes when the job has been queued</returns>
    /// <exception cref="Exception">Thrown if user, job, or queue cannot be found, if the job cannot be started, or if queueing fails</exception>
    /// <remarks>
    /// This method performs the following operations:
    /// 1. Verifies the job can transition to the Waiting state
    /// 2. Updates the job status to Waiting and sets the submission date
    /// 3. Adds the job to the specified cluster queue for execution
    /// 4. Raises events to notify subscribers about the job status change
    ///
    /// Cluster execution means the job will run on a remote computing cluster,
    /// which typically offers more computational resources than the local machine.
    /// </remarks>
    public async Task QueueClusterJob(ReadOnlyUser user, ReadOnlyJob job, ReadOnlyJobQueue queue)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);
                var originalQueue = ResolveQueue(queue.Id);

                // Check if the job can be transitioned to the Waiting state
                if (!originalJob.CanTransitionState(JobStatus.Waiting))
                    throw new Exception("Job cannot be started.");

                // Inherit color from first colored parent if not already set
                if (originalJob.ColorTag == null)
                {
                    var parentColor = originalJob.GetParents()
                        .Select(p => p.ColorTag)
                        .FirstOrDefault(c => c != null);
                    if (parentColor != null)
                        originalJob.ColorTag = parentColor;
                }

                // Update the job status and record which queue it's assigned to
                _dataRepository.UpdateJob(originalUser, originalJob, j =>
                {
                    j.Status = JobStatus.Waiting;
                    j.QueueId = originalQueue.Id;
                    j.AddEvent(EventType.WaitingStarted, originalUser);
                });

                // Queue the job for cluster execution
                _queueRepository.QueueClusterJob(originalJob, originalQueue);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to queue cluster job {JobId} by user {UserId} to queue {QueueId}", job.Id, user.Id, queue.Id);
                throw;
            }
        });

        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
        await JobQueued.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    /// <summary>
    /// Finalizes a job, setting it to the Finalizing state and queueing it for local execution.
    /// </summary>
    /// <param name="user">The user finalizing the job</param>
    /// <param name="job">The job to finalize</param>
    /// <returns>A task that completes when the job has been finalized</returns>
    /// <exception cref="Exception">Thrown if user or job cannot be found, if the job cannot be finalized, or if finalizing fails</exception>
    /// <remarks>
    /// This method performs the following operations:
    /// 1. Verifies the job can transition to the Finalizing state
    /// 2. Updates the job status to Finalizing and sets the submission date
    /// 3. Queues the job for local execution
    /// 4. Raises events to notify subscribers about the job status change
    /// </remarks>
    public async Task FinalizeLocalJob(ReadOnlyUser user, ReadOnlyJob job)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                // Check if the job can be transitioned to the Waiting state
                if (!originalJob.CanTransitionState(JobStatus.Finalizing))
                    throw new Exception("Job cannot be finalized");

                // Update the job status and record queue assignment
                _dataRepository.UpdateJob(originalUser, originalJob, j =>
                {
                    j.Status = JobStatus.Finalizing;
                    j.QueueId = _queueRepository.LocalQueue.Id;
                    j.AddEvent(EventType.FinalizingStarted, originalUser);
                });

                // Queue the job for local execution
                _queueRepository.QueueLocalJob(originalJob);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to queue local job {JobId} for finalization by user {UserId}", job.Id, user.Id);
                throw;
            }
        });

        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
        await JobQueued.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    #endregion
}
