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
        await ExecuteSpaceChange(view.Space, async () =>
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
                    originalView.MoveJobToFolder(newJob, folder);

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
        await ExecuteSpaceChange(job.Space, async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

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

    /// <summary>Changes execution parameters only while no execution depends on this definition.</summary>
    public Task UpdateJobParameters(ReadOnlyUser user, ReadOnlyJob job, Action<Job> updateAction) =>
        UpdateJob(user, job, mutable =>
        {
            EnsureNoPendingExecutions([mutable], $"Job {mutable.QualifiedName}");
            updateAction(mutable);
        });

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
        Job deletedJob = null;
        await ExecuteSpaceChange(job.Space, async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);
                deletedJob = originalJob;

                EnsureNoPendingExecutions([originalJob], $"Job {originalJob.QualifiedName}");

                // Check if the job is in a state that allows deletion
                if (!originalJob.CanTransitionState(JobStatus.Deleted))
                    throw new Exception("Job cannot be deleted.");

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

                // Delete the job from the data model
                _dataRepository.DeleteJob(originalUser, originalJob);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to delete job {JobId} by user {UserId}", job.Id, user.Id);
                throw;
            }
        });

        // Physical deletion can take time, so we don't want to block
        // This runs in a separate task to avoid blocking the UI while files are deleted
        await Task.Run(deletedJob.DeleteWorkingDirectory);

        await JobDeleted.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
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
        await ExecuteSpaceChange(view.Space, async () =>
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

        await ExecuteSpaceChange(view.Space, async () =>
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
            Job originalJob = null;

            // First transition the job to the Clearing state
            await UpdateJob(user, job, resolvedJob =>
            {
                EnsureNoPendingExecutions(
                    [resolvedJob],
                    $"Job {resolvedJob.QualifiedName}");
                resolvedJob.AddEvent(EventType.ClearingStarted, originalUser);
                resolvedJob.Status = JobStatus.Clearing;
                originalJob = resolvedJob;
            } );

            try
            {
                // Directory deletion is synchronous and can be slow, so keep it off the request context.
                await Task.Run(originalJob.Clear);

                // After successful clearing, transition the job back to the Building state
                await UpdateJob(user, job, resolvedJob =>
                {
                    resolvedJob.AddEvent(EventType.ClearingFinished);
                    resolvedJob.Status = JobStatus.Building;
                } );
            }
            catch (Exception ex)
            {
                Log.ForContext<DataManager>().Error(ex, "Failed to clear files for job {JobId}", job.Id);
                await originalJob.WriteToErrorLog("Failed to clear job:\n" + ex);

                await UpdateJob(user, job, resolvedJob =>
                {
                    resolvedJob.AddEvent(EventType.Failed);
                    resolvedJob.Status = JobStatus.Failed;
                } );
            }
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
    /// <summary>
    /// Throws with a human-readable message if a job has unmet parameter or port-connection
    /// requirements. Mirrors the GUI's pre-queue validation (<see cref="Job.ValidateInputs"/> +
    /// <see cref="Job.ValidatePortInputs"/>) so non-GUI callers (e.g. MCP) can't queue a job the
    /// GUI would have blocked. The GUI keeps its own copy of these checks for live button state.
    /// </summary>
    private static void EnsureJobInputsValid(Job job)
    {
        var inputErrors = job.ValidateInputs();
        var portErrors = job.ValidatePortInputs();
        if (inputErrors.Count == 0 && portErrors.Count == 0)
            return;

        var messages = new List<string>();
        foreach (var (field, message) in inputErrors)
            messages.Add($"{field}: {message}");
        foreach (var (port, portMessages) in portErrors)
            foreach (var message in portMessages)
                messages.Add($"{port}: {message}");

        throw new Exception(
            "Job cannot be queued because its inputs are incomplete — " +
            string.Join("; ", messages) + ".");
    }

    public async Task AbortJob(ReadOnlyUser user, ReadOnlyJob job)
    {
        await ExecuteWithLock(async () =>
        {
            var originalUser = ResolveUser(user.Id);
            var mutableJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);
            if (!mutableJob.CanTransitionState(JobStatus.Aborting))
                throw new Exception($"Job cannot be aborted from its current state ({mutableJob.Status}).");

            _dataRepository.UpdateJob(originalUser, mutableJob, _ => { });
            await _queueRepository.CancelJobAsync(mutableJob);
        });
    }

    /// <summary>
    /// Changes a running job's worker target. Excess workers are canceled immediately.
    /// This affects the current attempt only, leaving future-run parameters unchanged.
    /// </summary>
    public async Task ResizeJobPool(ReadOnlyUser user, ReadOnlyJob job, int desiredSize)
    {
        await ExecuteWithLock(async () =>
        {
            var originalUser = ResolveUser(user.Id);
            var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);
            await _queueRepository.ResizeWorkerGroupAsync(originalJob, desiredSize);
            _dataRepository.UpdateJob(originalUser, originalJob, _ => { });
        });

        await JobUpdated.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
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
    public Task QueueLocalJob(ReadOnlyUser user, ReadOnlyJob job) =>
        QueueJob(user, job, _queueRepository.LocalQueue.AsReadOnly());

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
    public Task QueueClusterJob(ReadOnlyUser user, ReadOnlyJob job, ReadOnlyJobQueue queue) =>
        QueueJob(user, job, queue);

    private async Task QueueJob(ReadOnlyUser user, ReadOnlyJob job, ReadOnlyJobQueue queue)
    {
        try
        {
            await ExecuteWithLock(async () =>
            {
                var originalUser = ResolveUser(user.Id);
                var mutableJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);
                var mutableQueue = ResolveQueue(queue.Id);

                if (_queueRepository.HasExecutionAttempt(mutableJob) ||
                    !mutableJob.CanTransitionState(JobStatus.Waiting))
                    throw new Exception("Job cannot be started.");

                EnsureJobInputsValid(mutableJob);

                if (mutableJob.ColorTag == null)
                {
                    var parentColor = mutableJob.GetParents()
                        .Select(p => p.ColorTag)
                        .FirstOrDefault(c => c != null);
                    if (parentColor != null)
                        mutableJob.ColorTag = parentColor;
                }

                _dataRepository.UpdateJob(
                    originalUser,
                    mutableJob,
                    queued => queued.QueueId = mutableQueue.Id);
                await _queueRepository.QueueJobAsync(mutableJob, mutableQueue);
            });
        }
        catch (Exception e)
        {
            Log.ForContext<DataManager>().Error(e, "Failed to queue job {JobId} by user {UserId} to queue {QueueId}", job.Id, user.Id, queue.Id);
            throw;
        }

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
        try
        {
            await ExecuteWithLock(async () =>
            {
                var originalUser = ResolveUser(user.Id);
                var mutableJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                if (_queueRepository.HasExecutionAttempt(mutableJob) ||
                    !mutableJob.CanTransitionState(JobStatus.Finalizing))
                    throw new Exception("Job cannot be finalized");

                _dataRepository.UpdateJob(
                    originalUser,
                    mutableJob,
                    queued => queued.QueueId = _queueRepository.LocalQueue.Id);
                await _queueRepository.FinalizeJobAsync(mutableJob);
            });
        }
        catch (Exception e)
        {
            Log.ForContext<DataManager>().Error(e, "Failed to queue local job {JobId} for finalization by user {UserId}", job.Id, user.Id);
            throw;
        }

        await JobQueued.InvokeHierarchy(job, GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
    }

    #endregion
}
