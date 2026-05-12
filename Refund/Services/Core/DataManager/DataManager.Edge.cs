using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    #region Public methods for data manipulation

    /// <summary>
    /// Creates a new edge connecting two ports in a space.
    /// </summary>
    /// <param name="space">The space in which to create the edge</param>
    /// <param name="from">The source (output) port</param>
    /// <param name="to">The target (input) port</param>
    /// <returns>A read-only wrapper of the created edge</returns>
    /// <exception cref="Exception">Thrown if the space, jobs, or ports cannot be found, or if edge creation fails</exception>
    /// <remarks>
    /// This method establishes a connection between two job ports, representing the flow of data
    /// from the output of one job to the input of another. The edge creation process includes:
    ///
    /// 1. Validating that both source and target jobs exist in the specified space
    /// 2. Validating that the specified ports exist on their respective jobs
    /// 3. Creating the edge in the data model
    /// 4. Raising events for the edge creation and for updates to both connected jobs
    ///
    /// The notification events are hierarchical, allowing subscribers to listen for
    /// changes at various levels of specificity, from specific objects to wildcard patterns.
    /// </remarks>
    public async Task<ReadOnlyEdge> CreateEdge(ReadOnlySpace space, ReadOnlyPort from, ReadOnlyPort to)
    {
        ReadOnlyEdge createdEdge = null;
        ReadOnlyJob sourceJob = null;
        ReadOnlyJob targetJob = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);

                Job fromJob = originalSpace.FindJob(from.Job.Id);
                if (fromJob == null)
                    throw new Exception($"Source Job {from.Job.Id} not found.");

                Job toJob = originalSpace.FindJob(to.Job.Id);
                if (toJob == null)
                    throw new Exception($"Target Job {to.Job.Id} not found.");

                if (!fromJob.PortsOut.ContainsKey(from.Name))
                    throw new Exception($"Source Job {from.Job.Id} doesn't have an output port named {from.Name}.");
                PortOut fromPort = fromJob.PortsOut[from.Name];

                if (!toJob.PortsIn.ContainsKey(to.Name))
                    throw new Exception($"Target Job {to.Job.Id} doesn't have an input port named {to.Name}.");
                PortIn toPort = toJob.PortsIn[to.Name];

                Edge newEdge = _dataRepository.CreateEdge(originalSpace, fromPort, toPort);

                UpdateFolderLayoutsForEdge(originalSpace, fromJob.Id, toJob.Id);
                UpdateDiagramLayoutsForEdge(originalSpace, fromJob.Id, toJob.Id);

                createdEdge = newEdge.AsReadOnly();
                sourceJob = from.Job;
                targetJob = to.Job;
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to create edge from {SourceJobId}:{SourcePortName} to {TargetJobId}:{TargetPortName}",
                    from.Job.Id, from.Name, to.Job.Id, to.Name);
                throw;
            }
        });

        // Raise events outside of lock
        await EdgeCreated.InvokeHierarchy(createdEdge, GroupName.EdgeHierarchy(space.Project.Id, space.Id, null));
        await JobUpdated.InvokeHierarchy(sourceJob, GroupName.JobHierarchy(sourceJob.Space.Project.Id, sourceJob.Space.Id, sourceJob.Id));
        await JobUpdated.InvokeHierarchy(targetJob, GroupName.JobHierarchy(targetJob.Space.Project.Id, targetJob.Space.Id, targetJob.Id));

        return createdEdge;
    }

    /// <summary>
    /// Updates an existing edge by applying the specified update action.
    /// </summary>
    /// <param name="edge">The edge to update</param>
    /// <param name="updateAction">The action to apply to the edge</param>
    /// <returns>A read-only wrapper of the updated edge</returns>
    /// <exception cref="Exception">Thrown if the edge cannot be found or if update fails</exception>
    /// <remarks>
    /// This method handles both the data operation and dispatching the appropriate events.
    /// The update action is applied to the mutable edge object within a lock to ensure consistency.
    /// After the update, events are raised to notify all interested subscribers.
    ///
    /// Edge updates are uncommon since edges have few mutable properties, but this method
    /// provides the mechanism for those rare cases when edge properties need to be modified.
    /// The most common use case is annotating an edge with metadata.
    /// </remarks>
    public async Task<ReadOnlyEdge> UpdateEdge(ReadOnlyEdge edge, Action<Edge> updateAction)
    {
        ReadOnlyEdge updatedEdge = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalEdge = ResolveEdge(edge.Space.Project.Id, edge.Space.Id, edge.Id);

                _dataRepository.UpdateEdge(originalEdge, updateAction);
                updatedEdge = originalEdge.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to update edge {EdgeId} in space {SpaceId}",
                    edge.Id, edge.Space.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await EdgeUpdated.InvokeHierarchy(updatedEdge, GroupName.EdgeHierarchy(edge.Space.Project.Id, edge.Space.Id, edge.Id));

        return updatedEdge;
    }

    /// <summary>
    /// Deletes an existing edge connection between jobs.
    /// </summary>
    /// <param name="edge">The edge to delete</param>
    /// <returns>A task that completes when the delete operation is finished</returns>
    /// <exception cref="Exception">Thrown if the edge cannot be found or if deletion fails</exception>
    /// <remarks>
    /// This method handles both the deletion in the data model and dispatching the appropriate events.
    /// The deletion occurs within a lock to ensure consistency. After deletion, events are raised:
    ///
    /// 1. Edge deletion events notify subscribers that the edge has been removed
    /// 2. Job update events notify subscribers that both the source and target jobs have been modified
    ///
    /// Deleting an edge affects the connected jobs by removing the connection between their ports,
    /// potentially changing their execution behavior since inputs may no longer be satisfied.
    /// Jobs connected to the deleted edge may need to be reconfigured or reconnected.
    /// </remarks>
    public async Task DeleteEdge(ReadOnlyEdge edge)
    {
        ReadOnlyEdge deletedEdge = null;
        ReadOnlyJob sourceJob = null;
        ReadOnlyJob targetJob = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalEdge = ResolveEdge(edge.Space.Project.Id, edge.Space.Id, edge.Id);

                // Store the read-only version before deletion
                deletedEdge = originalEdge.AsReadOnly();
                sourceJob = deletedEdge.Source.Job;
                targetJob = deletedEdge.Target.Job;

                // Capture affected folders and views before deletion
                Space originalSpace = _dataRepository.FindSpace(edge.Space.Project.Id, edge.Space.Id);
                var affectedFolders = new List<Folder>();
                var affectedViews = new HashSet<View>();
                int sourceJobId = originalEdge.Source.Job.Id;
                int targetJobId = originalEdge.Target.Job.Id;
                if (originalSpace != null)
                    foreach (var v in originalSpace.Views)
                    {
                        bool viewHasSource = v.Jobs.Any(j => j.Id == sourceJobId);
                        bool viewHasTarget = v.Jobs.Any(j => j.Id == targetJobId);
                        if (viewHasSource || viewHasTarget)
                            affectedViews.Add(v);

                        foreach (var folder in v.Folders)
                            if (FolderContainsJob(folder, sourceJobId) &&
                                FolderContainsJob(folder, targetJobId))
                                affectedFolders.Add(folder);
                    }

                _dataRepository.DeleteEdge(originalEdge);

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
                Log.ForContext<DataManager>().Error(e, "Failed to delete edge {EdgeId} in space {SpaceId}",
                    edge.Id, edge.Space.Id);
                throw;
            }
        });

        // Raise events outside of lock
        await EdgeDeleted.InvokeHierarchy(deletedEdge, GroupName.EdgeHierarchy(edge.Space.Project.Id, edge.Space.Id, edge.Id));
        await JobUpdated.InvokeHierarchy(sourceJob, GroupName.JobHierarchy(sourceJob.Space.Project.Id, sourceJob.Space.Id, sourceJob.Id));
        await JobUpdated.InvokeHierarchy(targetJob, GroupName.JobHierarchy(targetJob.Space.Project.Id, targetJob.Space.Id, targetJob.Id));
    }

    #endregion
}
