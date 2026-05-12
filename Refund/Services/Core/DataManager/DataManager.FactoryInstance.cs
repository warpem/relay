using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    /// <summary>
    /// Creates a factory instance from a completed definition.
    /// Clones all sub-job blueprints as real jobs, creates internal and external edges.
    /// </summary>
    public async Task<ReadOnlyFactoryInstance> CreateFactoryInstance(
        ReadOnlyUser user, ReadOnlyView view, int definitionId,
        ReadOnlyFolder targetFolder = null)
    {
        ReadOnlyFactoryInstance created = null;
        var createdJobReadOnlys = new List<ReadOnlyJob>();

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);
                var originalView = originalSpace.FindView(view.Id)
                    ?? throw new Exception($"View {view.Id} not found");

                var def = originalSpace.FindFactoryDefinition(definitionId)
                    ?? throw new Exception($"Factory definition {definitionId} not found");

                Folder folder = null;
                if (targetFolder != null)
                {
                    folder = originalView.FindFolder(targetFolder.Id)
                        ?? throw new Exception($"Folder {targetFolder.Id} not found");
                }

                // Create the instance
                var inst = originalSpace.CreateFactoryInstance(definitionId);
                inst.AddEvent(EventType.Created, originalUser);
                inst.UpdateDate = DateTime.Now;
                inst.UpdatedBy = originalUser;

                // Clone each sub-job blueprint into a real job
                var blueprintIdToRealJob = new Dictionary<int, Job>();
                foreach (var blueprint in def.SubJobs)
                {
                    var realJob = _dataRepository.CreateJob(
                        originalUser, originalSpace, originalView,
                        blueprint.TypeGuid, blueprint);
                    realJob.FactoryInstanceId = inst.Id;
                    realJob.Status = JobStatus.Building;
                    realJob.Alias = blueprint.Alias;

                    blueprintIdToRealJob[blueprint.Id] = realJob;
                    inst.SubJobIds.Add(realJob.Id);
                    createdJobReadOnlys.Add(realJob.AsReadOnly());
                }

                // Create internal edges
                foreach (var ie in def.InternalEdges)
                {
                    var (sourceId, sourcePort) = ParseEdgeRef(ie.Source);
                    var (targetId, targetPort) = ParseEdgeRef(ie.Target);

                    if (blueprintIdToRealJob.TryGetValue(sourceId, out var sourceJob) &&
                        blueprintIdToRealJob.TryGetValue(targetId, out var targetJob))
                    {
                        if (sourceJob.PortsOut.TryGetValue(sourcePort, out var fromPort) &&
                            targetJob.PortsIn.TryGetValue(targetPort, out var toPort))
                        {
                            _dataRepository.CreateEdge(originalSpace, fromPort, toPort);
                        }
                    }
                }

                // Create external edges
                foreach (var ext in def.ExternalEdges)
                {
                    var externalJob = originalSpace.FindJob(ext.ExternalJobId);
                    if (externalJob == null) continue;

                    if (blueprintIdToRealJob.TryGetValue(ext.SubJobId, out var subJob))
                    {
                        if (subJob.PortsIn.TryGetValue(ext.SubJobPort, out var subPortIn) &&
                            externalJob.PortsOut.TryGetValue(ext.ExternalPort, out var extPortOut))
                        {
                            _dataRepository.CreateEdge(originalSpace, extPortOut, subPortIn);
                        }
                        else if (subJob.PortsOut.TryGetValue(ext.SubJobPort, out var subPortOut) &&
                                 externalJob.PortsIn.TryGetValue(ext.ExternalPort, out var extPortIn))
                        {
                            _dataRepository.CreateEdge(originalSpace, subPortOut, extPortIn);
                        }
                    }
                }

                // Add instance to view (AddFactoryInstance handles folder placement)
                originalView.AddFactoryInstance(inst, folder);

                if (folder != null)
                {
                    folder.UpdateLayout(originalSpace);
                    folder.UpdateDiagramLayout(originalSpace);
                }

                originalView.UpdateDiagramLayout(originalSpace);

                // Sub-jobs should NOT be in _RootItems (they're hidden inside the factory)
                foreach (var realJob in blueprintIdToRealJob.Values)
                {
                    originalView.RemoveJobFromRootItems(realJob);
                }

                // Compute sub-job diagram layout for the new instance
                inst.UpdateDiagramLayout(originalSpace);

                _dataRepository.MarkSpaceForSave(originalSpace);
                created = inst.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to create factory instance for definition {DefinitionId} by user {UserId}",
                    definitionId, user.Id);
                throw;
            }
        });

        await FactoryInstanceCreated.InvokeHierarchy(created,
            GroupName.FactoryInstanceHierarchy(view.Space.Project.Id, view.Space.Id, null));

        foreach (var job in createdJobReadOnlys)
            await JobCreated.InvokeHierarchy(job,
                GroupName.JobHierarchy(view.Space.Project.Id, view.Space.Id, null));

        await ViewUpdated.InvokeHierarchy(view,
            GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
        await SpaceUpdated.InvokeHierarchy(view.Space,
            GroupName.SpaceHierarchy(view.Space.Project.Id, view.Space.Id));

        return created;
    }

    /// <summary>
    /// Updates a factory instance by applying the given mutator action.
    /// </summary>
    public async Task UpdateFactoryInstance(ReadOnlyUser user, ReadOnlyFactoryInstance instance, Action<FactoryInstance> updateAction)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(instance.Space.Project.Id, instance.Space.Id);
                var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                    ?? throw new Exception($"Factory instance {instance.Id} not found");

                updateAction?.Invoke(originalInst);
                originalInst.UpdateDate = DateTime.Now;
                originalInst.UpdatedBy = originalUser;

                _dataRepository.MarkSpaceForSave(originalSpace);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to update factory instance {InstanceId} by user {UserId}",
                    instance.Id, user.Id);
                throw;
            }
        });

        await FactoryInstanceUpdated.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(instance.Space.Project.Id, instance.Space.Id, instance.Id));
    }

    /// <summary>
    /// Deletes a factory instance and all its sub-jobs.
    /// </summary>
    public async Task DeleteFactoryInstance(
        ReadOnlyUser user, ReadOnlySpace space, ReadOnlyFactoryInstance instance)
    {
        var deletedJobSnapshots = new List<ReadOnlyJob>();

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);
                var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                    ?? throw new Exception($"Factory instance {instance.Id} not found");

                foreach (var jobId in originalInst.SubJobIds)
                {
                    var job = originalSpace.FindJob(jobId);
                    if (job != null && !job.CanTransitionState(JobStatus.Deleted))
                        throw new Exception(
                            $"Sub-job {job.QualifiedName} cannot be deleted (status: {job.Status}). " +
                            "Abort running sub-jobs before deleting the factory instance.");
                }

                var subJobs = originalInst.SubJobIds
                    .Select(id => originalSpace.FindJob(id))
                    .Where(j => j != null)
                    .ToList();

                foreach (var job in subJobs)
                    deletedJobSnapshots.Add(job.AsReadOnly());

                // Clear FactoryInstanceId before deleting so deletion guard doesn't block
                foreach (var job in subJobs)
                    job.FactoryInstanceId = null;

                foreach (var job in subJobs)
                    _dataRepository.DeleteJob(originalUser, job);

                foreach (var v in originalSpace.Views)
                    v.RemoveFactoryInstance(originalInst);

                originalSpace.DeleteFactoryInstance(originalInst);
                _dataRepository.MarkSpaceForSave(originalSpace);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to delete factory instance {InstanceId} by user {UserId}",
                    instance.Id, user.Id);
                throw;
            }
        });

        foreach (var job in deletedJobSnapshots)
            await JobDeleted.InvokeHierarchy(job,
                GroupName.JobHierarchy(space.Project.Id, space.Id, job.Id));

        await FactoryInstanceDeleted.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(space.Project.Id, space.Id, instance.Id));
        await SpaceUpdated.InvokeHierarchy(space,
            GroupName.SpaceHierarchy(space.Project.Id, space.Id));
    }

    /// <summary>
    /// Converts a factory instance into a regular folder.
    /// Sub-jobs are detached from the factory and placed in the new folder.
    /// </summary>
    public async Task<int> ConvertFactoryInstanceToFolder(
        ReadOnlyUser user, ReadOnlyView view, ReadOnlyFactoryInstance instance)
    {
        int folderId = 0;

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);
                var originalView = originalSpace.FindView(view.Id)
                    ?? throw new Exception($"View {view.Id} not found");
                var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                    ?? throw new Exception($"Factory instance {instance.Id} not found");

                // Create folder using the same pattern as DataManager.Folder.cs
                var folderAlias = string.IsNullOrWhiteSpace(originalInst.Alias)
                    ? $"FI{originalInst.Id}"
                    : originalInst.Alias;
                var folder = new Folder
                {
                    Id = originalView.GetNextFolderId(),
                    Alias = folderAlias,
                    CreationDate = DateTime.Now,
                    CreatedBy = originalUser,
                    UpdateDate = DateTime.Now,
                    UpdatedBy = originalUser
                };
                originalView.AddFolder(folder);
                folderId = folder.Id;

                // Detach sub-jobs from the factory and move them into the new folder
                foreach (var jobId in originalInst.SubJobIds)
                {
                    var job = originalSpace.FindJob(jobId);
                    if (job != null)
                    {
                        job.FactoryInstanceId = null;
                        originalView.MoveJobToFolder(job, folder);
                    }
                }

                // Remove the factory instance from ALL views in the space that contain it
                foreach (var v in originalSpace.Views)
                    v.RemoveFactoryInstance(originalInst);

                originalSpace.DeleteFactoryInstance(originalInst);

                folder.UpdateLayout(originalSpace);
                folder.UpdateDiagramLayout(originalSpace);
                originalView.UpdateDiagramLayout(originalSpace);

                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to convert factory instance {InstanceId} to folder by user {UserId}",
                    instance.Id, user.Id);
                throw;
            }
        });

        await FactoryInstanceDeleted.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(view.Space.Project.Id, view.Space.Id, instance.Id));
        await ViewUpdated.InvokeHierarchy(view,
            GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));

        return folderId;
    }

    /// <summary>
    /// Queues all Building sub-jobs of a factory instance for execution.
    /// Each sub-job is assigned to a queue via queueAssignments (jobId -> queueId).
    /// Jobs assigned to queue -1 go to the local queue; others go to the matching cluster queue.
    /// </summary>
    public async Task RunFactoryInstance(
        ReadOnlyUser user, ReadOnlyFactoryInstance instance,
        Dictionary<int, int> queueAssignments)
    {
        var queuedJobSnapshots = new List<ReadOnlyJob>();

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(instance.Space.Project.Id, instance.Space.Id);
                var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                    ?? throw new Exception($"Factory instance {instance.Id} not found");

                foreach (var jobId in originalInst.SubJobIds)
                {
                    var job = originalSpace.FindJob(jobId);
                    if (job == null || job.Status != JobStatus.Building)
                        continue;

                    if (!job.CanTransitionState(JobStatus.Waiting))
                        continue;

                    if (!queueAssignments.TryGetValue(jobId, out var queueId))
                        queueId = _queueRepository.LocalQueue.Id; // default to local

                    var queue = ResolveQueue(queueId);

                    _dataRepository.UpdateJob(originalUser, job, j =>
                    {
                        j.Status = JobStatus.Waiting;
                        j.QueueId = queueId;
                        j.AddEvent(EventType.WaitingStarted, originalUser);
                    });

                    if (queueId == _queueRepository.LocalQueue.Id)
                        _queueRepository.QueueLocalJob(job);
                    else
                        _queueRepository.QueueClusterJob(job, queue);

                    queuedJobSnapshots.Add(job.AsReadOnly());
                }

                originalInst.AddEvent(EventType.WaitingStarted, originalUser);
                originalInst.UpdateDate = DateTime.Now;
                originalInst.UpdatedBy = originalUser;
                _dataRepository.MarkSpaceForSave(originalSpace);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to run factory instance {InstanceId} by user {UserId}",
                    instance.Id, user.Id);
                throw;
            }
        });

        foreach (var job in queuedJobSnapshots)
        {
            await JobUpdated.InvokeHierarchy(job,
                GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
            await JobQueued.InvokeHierarchy(job,
                GroupName.JobHierarchy(job.Space.Project.Id, job.Space.Id, job.Id));
        }

        await FactoryInstanceUpdated.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(instance.Space.Project.Id, instance.Space.Id, instance.Id));
    }

    /// <summary>
    /// Aborts all active (unsettled) sub-jobs of a factory instance.
    /// Delegates to AbortJob for each sub-job whose status is unsettled.
    /// </summary>
    public async Task AbortFactoryInstance(ReadOnlyUser user, ReadOnlyFactoryInstance instance)
    {
        // Collect sub-jobs that need aborting while we still have the read-only snapshot
        var jobsToAbort = instance.SubJobs
            .Where(j => j.Status.IsUnsettled() || j.Status == JobStatus.Waiting)
            .ToList();

        foreach (var job in jobsToAbort)
            await AbortJob(user, job);

        await FactoryInstanceUpdated.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(instance.Space.Project.Id, instance.Space.Id, instance.Id));
    }

    /// <summary>
    /// Clears only Failed/Aborted sub-jobs of a factory instance back to Building.
    /// Delegates to ClearJob for each qualifying sub-job.
    /// </summary>
    public async Task ClearFailedFactoryInstance(ReadOnlyUser user, ReadOnlyFactoryInstance instance)
    {
        var jobsToClear = instance.SubJobs
            .Where(j => j.Status == JobStatus.Failed || j.Status == JobStatus.Aborted)
            .ToList();

        foreach (var job in jobsToClear)
            await ClearJob(user, job);

        await FactoryInstanceUpdated.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(instance.Space.Project.Id, instance.Space.Id, instance.Id));
    }

    /// <summary>
    /// Clears all non-Building sub-jobs of a factory instance back to Building.
    /// Delegates to ClearJob for each qualifying sub-job.
    /// </summary>
    public async Task ClearFactoryInstance(ReadOnlyUser user, ReadOnlyFactoryInstance instance)
    {
        var jobsToClear = instance.SubJobs
            .Where(j => j.Status != JobStatus.Building)
            .ToList();

        // Abort any active jobs first before clearing
        var activeJobs = jobsToClear
            .Where(j => j.Status.IsUnsettled() || j.Status == JobStatus.Waiting)
            .ToList();
        foreach (var job in activeJobs)
            await AbortJob(user, job);

        // Now clear all non-Building jobs (re-read statuses since abort may have changed them)
        var refreshedInstance = instance; // instance is a live read-only wrapper, reflects current state
        var refreshedJobsToClear = refreshedInstance.SubJobs
            .Where(j => j.Status != JobStatus.Building)
            .ToList();

        foreach (var job in refreshedJobsToClear)
            await ClearJob(user, job);

        await FactoryInstanceUpdated.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(instance.Space.Project.Id, instance.Space.Id, instance.Id));
    }

    /// <summary>
    /// Clones a factory instance: creates a new instance from the same definition,
    /// clones sub-jobs with their current parameters, and rewires internal edges.
    /// </summary>
    public async Task<ReadOnlyFactoryInstance> CloneFactoryInstance(
        ReadOnlyUser user, ReadOnlyView view, ReadOnlyFactoryInstance instance)
    {
        ReadOnlyFactoryInstance cloned = null;
        var clonedJobReadOnlys = new List<ReadOnlyJob>();

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);
                var originalView = originalSpace.FindView(view.Id)
                    ?? throw new Exception($"View {view.Id} not found");
                var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                    ?? throw new Exception($"Factory instance {instance.Id} not found");

                // Create new instance from the same definition
                var newInst = originalSpace.CreateFactoryInstance(originalInst.DefinitionId);
                newInst.Alias = originalInst.Alias;
                newInst.ColorTag = originalInst.ColorTag;
                newInst.Notes = originalInst.Notes;
                newInst.AddEvent(EventType.Created, originalUser);
                newInst.UpdateDate = DateTime.Now;
                newInst.UpdatedBy = originalUser;

                // Clone each sub-job and build mapping from old job ID to new job
                var oldIdToNewJob = new Dictionary<int, Job>();
                foreach (var oldJobId in originalInst.SubJobIds)
                {
                    var oldJob = originalSpace.FindJob(oldJobId);
                    if (oldJob == null) continue;

                    var newJob = _dataRepository.CloneJob(originalUser, originalSpace, oldJob, originalView);
                    newJob.FactoryInstanceId = newInst.Id;
                    newInst.SubJobIds.Add(newJob.Id);
                    oldIdToNewJob[oldJobId] = newJob;
                    clonedJobReadOnlys.Add(newJob.AsReadOnly());
                }

                // Remove cloned sub-jobs from root items (they're inside the factory)
                foreach (var newJob in oldIdToNewJob.Values)
                    originalView.RemoveJobFromRootItems(newJob);

                // Rewire internal edges: for each cloned job, check input edges.
                // If an edge's source job was also cloned (i.e. it's an internal edge),
                // replace it with an edge from the cloned source.
                var clonedOldIds = new HashSet<int>(oldIdToNewJob.Keys);
                foreach (var (oldId, newJob) in oldIdToNewJob)
                {
                    foreach (var port in newJob.PortsIn)
                    {
                        var edges = port.Value.Edges.ToList();
                        foreach (var edge in edges)
                        {
                            int sourceJobId = edge.Source.Job.Id;

                            // Check if the source was one of the OLD jobs that got cloned.
                            // CloneJob copies input edges from the original, so the edge
                            // currently points from the OLD source to the NEW target.
                            // We need to check if the source was one of the old sub-jobs.
                            if (clonedOldIds.Contains(sourceJobId) && oldIdToNewJob.ContainsKey(sourceJobId))
                            {
                                string sourcePortName = edge.Source.Name;
                                string targetPortName = port.Key;

                                _dataRepository.DeleteEdge(edge);
                                _dataRepository.CreateEdge(originalSpace,
                                    oldIdToNewJob[sourceJobId].PortsOut[sourcePortName],
                                    newJob.PortsIn[targetPortName]);
                            }
                            // Also remove edges pointing to the original instance's sub-jobs
                            // (the clone should only connect to other clones internally)
                            else if (originalInst.SubJobIds.Contains(sourceJobId))
                            {
                                _dataRepository.DeleteEdge(edge);
                            }
                        }
                    }
                }

                // Add instance to view
                originalView.AddFactoryInstance(newInst);
                originalView.UpdateDiagramLayout(originalSpace);

                _dataRepository.MarkSpaceForSave(originalSpace);
                cloned = newInst.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to clone factory instance {InstanceId} by user {UserId}",
                    instance.Id, user.Id);
                throw;
            }
        });

        await FactoryInstanceCreated.InvokeHierarchy(cloned,
            GroupName.FactoryInstanceHierarchy(view.Space.Project.Id, view.Space.Id, null));

        foreach (var job in clonedJobReadOnlys)
            await JobCreated.InvokeHierarchy(job,
                GroupName.JobHierarchy(view.Space.Project.Id, view.Space.Id, null));

        await ViewUpdated.InvokeHierarchy(view,
            GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));

        return cloned;
    }

    /// <summary>
    /// Adds a factory instance to a view (makes it visible in that view).
    /// </summary>
    public async Task AddFactoryInstanceToView(ReadOnlyUser user, ReadOnlyView view, ReadOnlyFactoryInstance instance)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);
                var originalView = originalSpace.FindView(view.Id)
                    ?? throw new Exception($"View {view.Id} not found");
                var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                    ?? throw new Exception($"Factory instance {instance.Id} not found");

                originalView.AddFactoryInstance(originalInst);
                originalView.UpdateDiagramLayout(originalSpace);

                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to add factory instance {InstanceId} to view {ViewId} by user {UserId}",
                    instance.Id, view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view,
            GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
        await FactoryInstanceUpdated.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(view.Space.Project.Id, view.Space.Id, instance.Id));
    }

    /// <summary>
    /// Removes a factory instance from a view (hides it in that view).
    /// </summary>
    public async Task RemoveFactoryInstanceFromView(ReadOnlyUser user, ReadOnlyView view, ReadOnlyFactoryInstance instance)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);
                var originalView = originalSpace.FindView(view.Id)
                    ?? throw new Exception($"View {view.Id} not found");
                var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                    ?? throw new Exception($"Factory instance {instance.Id} not found");

                originalView.RemoveFactoryInstance(originalInst);
                originalView.UpdateDiagramLayout(originalSpace);

                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to remove factory instance {InstanceId} from view {ViewId} by user {UserId}",
                    instance.Id, view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view,
            GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
        await FactoryInstanceUpdated.InvokeHierarchy(instance,
            GroupName.FactoryInstanceHierarchy(view.Space.Project.Id, view.Space.Id, instance.Id));
    }

    /// <summary>
    /// Moves a factory instance to a target folder (or root level if targetFolder is null) within a view.
    /// </summary>
    public async Task MoveFactoryInstanceToFolder(ReadOnlyUser user, ReadOnlyView view, ReadOnlyFactoryInstance instance, ReadOnlyFolder targetFolder)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(view.Space.Project.Id, view.Space.Id);
                var originalView = originalSpace.FindView(view.Id)
                    ?? throw new Exception($"View {view.Id} not found");
                var originalInst = originalSpace.FindFactoryInstance(instance.Id)
                    ?? throw new Exception($"Factory instance {instance.Id} not found");

                Folder target = null;
                if (targetFolder != null)
                {
                    target = originalView.FindFolder(targetFolder.Id);
                    if (target == null)
                        throw new Exception($"Target folder {targetFolder.Id} not found");
                }

                Folder sourceFolder = originalView.Folders.FirstOrDefault(f => f.Items.Contains(originalInst));

                originalView.MoveFactoryInstanceToFolder(originalInst, target);

                sourceFolder?.UpdateLayout(originalSpace);
                target?.UpdateLayout(originalSpace);
                sourceFolder?.UpdateDiagramLayout(originalSpace);
                target?.UpdateDiagramLayout(originalSpace);
                originalView.UpdateDiagramLayout(originalSpace);

                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to move factory instance {InstanceId} to folder in view {ViewId} by user {UserId}",
                    instance.Id, view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view,
            GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    /// <summary>
    /// Parses an edge reference string like "1.PortName" into (subJobId, portName).
    /// </summary>
    private static (int SubJobId, string PortName) ParseEdgeRef(string edgeRef)
    {
        var dotIndex = edgeRef.IndexOf('.');
        if (dotIndex < 0) throw new FormatException($"Invalid edge reference: {edgeRef}");
        return (int.Parse(edgeRef[..dotIndex]), edgeRef[(dotIndex + 1)..]);
    }
}
