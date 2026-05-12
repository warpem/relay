using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    /// <summary>
    /// Creates a new empty factory definition in the specified space.
    /// </summary>
    public async Task<ReadOnlyFactoryDefinition> CreateFactoryDefinition(
        ReadOnlyUser user, ReadOnlySpace space)
    {
        ReadOnlyFactoryDefinition created = null;

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);

                var def = originalSpace.CreateFactoryDefinition();
                def.Alias = $"Factory {def.Id}";

                _dataRepository.MarkSpaceForSave(originalSpace);
                created = def.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to create factory definition in space {SpaceId} by user {UserId}",
                    space.Id, user.Id);
                throw;
            }
        });

        await FactoryDefinitionCreated.InvokeHierarchy(created,
            GroupName.FactoryDefinitionHierarchy(space.Project.Id, space.Id, null));
        await SpaceUpdated.InvokeHierarchy(space,
            GroupName.SpaceHierarchy(space.Project.Id, space.Id));

        return created;
    }

    /// <summary>
    /// Renames a factory definition. Allowed even when instances exist.
    /// </summary>
    public async Task RenameFactoryDefinition(
        ReadOnlyUser user, ReadOnlySpace space, ReadOnlyFactoryDefinition definition, string alias)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);
                var originalDef = originalSpace.FindFactoryDefinition(definition.Id)
                    ?? throw new Exception($"Factory definition {definition.Id} not found");

                originalDef.Alias = alias;
                _dataRepository.MarkSpaceForSave(originalSpace);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to rename factory definition {DefinitionId} by user {UserId}",
                    definition.Id, user.Id);
                throw;
            }
        });

        await FactoryDefinitionUpdated.InvokeHierarchy(definition,
            GroupName.FactoryDefinitionHierarchy(space.Project.Id, space.Id, definition.Id));
    }

    /// <summary>
    /// Updates a factory definition by applying the specified action.
    /// </summary>
    public async Task UpdateFactoryDefinition(
        ReadOnlyUser user, ReadOnlySpace space, ReadOnlyFactoryDefinition definition,
        Action<FactoryDefinition> updateAction)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);
                var originalDef = originalSpace.FindFactoryDefinition(definition.Id)
                    ?? throw new Exception($"Factory definition {definition.Id} not found");

                // Guard: cannot edit if instances exist
                if (originalSpace.FactoryInstances.Any(i => i.DefinitionId == definition.Id))
                    throw new Exception(
                        "Cannot edit a factory definition that has existing instances. Clone the definition to create a modified version.");

                updateAction(originalDef);
                _dataRepository.MarkSpaceForSave(originalSpace);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to update factory definition {DefinitionId} by user {UserId}",
                    definition.Id, user.Id);
                throw;
            }
        });

        await FactoryDefinitionUpdated.InvokeHierarchy(definition,
            GroupName.FactoryDefinitionHierarchy(space.Project.Id, space.Id, definition.Id));
    }

    /// <summary>
    /// Deletes a factory definition. Blocked if any instances reference it.
    /// </summary>
    public async Task DeleteFactoryDefinition(
        ReadOnlyUser user, ReadOnlySpace space, ReadOnlyFactoryDefinition definition)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);
                var originalDef = originalSpace.FindFactoryDefinition(definition.Id)
                    ?? throw new Exception($"Factory definition {definition.Id} not found");

                // Guard: cannot delete if instances exist
                if (originalSpace.FactoryInstances.Any(i => i.DefinitionId == definition.Id))
                    throw new Exception(
                        "Cannot delete a factory definition that has existing instances. Delete instances first.");

                originalSpace.DeleteFactoryDefinition(originalDef);
                _dataRepository.MarkSpaceForSave(originalSpace);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to delete factory definition {DefinitionId} by user {UserId}",
                    definition.Id, user.Id);
                throw;
            }
        });

        await FactoryDefinitionDeleted.InvokeHierarchy(definition,
            GroupName.FactoryDefinitionHierarchy(space.Project.Id, space.Id, definition.Id));
        await SpaceUpdated.InvokeHierarchy(space,
            GroupName.SpaceHierarchy(space.Project.Id, space.Id));
    }

    /// <summary>
    /// Deep-clones a factory definition. The clone is always in Building status.
    /// </summary>
    public async Task<ReadOnlyFactoryDefinition> CloneFactoryDefinition(
        ReadOnlyUser user, ReadOnlySpace space, ReadOnlyFactoryDefinition definition)
    {
        ReadOnlyFactoryDefinition cloned = null;

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);
                var originalDef = originalSpace.FindFactoryDefinition(definition.Id)
                    ?? throw new Exception($"Factory definition {definition.Id} not found");

                // Create new definition and copy scalar state via JSON round-trip
                var newDef = originalSpace.CreateFactoryDefinition();
                var correctId = newDef.Id; // Save ID assigned by CreateFactoryDefinition
                var json = originalDef.ToJson();
                newDef.ReadFromJson(json); // Copies scalar properties but overwrites Id
                newDef.Id = correctId; // Restore the correct new ID

                // Clone sub-job blueprints — use Space.CreateJob then remove from space list
                // since blueprints live only inside the definition, not in Space._Jobs.
                // Preserve the blueprint-local IDs (1, 2, 3...) so that InternalEdges,
                // ExposedPorts, QueueAssignments, and DiagramLayout references stay valid.
                newDef.SubJobs.Clear();
                foreach (var blueprint in originalDef.SubJobs)
                {
                    var clonedBlueprint = originalSpace.CreateJob(blueprint.TypeGuid, blueprint, null);
                    originalSpace.RemoveJobFromList(clonedBlueprint);
                    clonedBlueprint.Id = blueprint.Id; // Preserve blueprint-local ID
                    clonedBlueprint.Status = JobStatus.Building;
                    clonedBlueprint.DirectoryName = "";
                    clonedBlueprint.ClearProperties();
                    newDef.SubJobs.Add(clonedBlueprint);
                }

                // Copy collections
                newDef.InternalEdges = new List<FactoryEdge>(originalDef.InternalEdges);
                newDef.ExternalEdges = new List<FactoryExternalEdge>(originalDef.ExternalEdges);
                newDef.ExposedPortsIn = originalDef.ExposedPortsIn.Select(p =>
                    ExposedPort.FromJson(p.ToJson())).ToList();
                newDef.ExposedPortsOut = originalDef.ExposedPortsOut.Select(p =>
                    ExposedPort.FromJson(p.ToJson())).ToList();
                newDef.ExposedProperties = originalDef.ExposedProperties.Select(p =>
                    ExposedProperty.FromJson(p.ToJson())).ToList();
                newDef.QueueAssignments = new Dictionary<int, int?>(originalDef.QueueAssignments);

                // Deep-copy DiagramLayout via JSON round-trip to avoid sharing the reference
                if (originalDef.DiagramLayout != null)
                    newDef.DiagramLayout = FactoryDefinition.DeserializeDiagramLayout(
                        FactoryDefinition.SerializeDiagramLayout(originalDef.DiagramLayout).AsObject());
                else
                    newDef.DiagramLayout = null;

                newDef.Alias = $"Clone of {originalDef.Alias}";

                _dataRepository.MarkSpaceForSave(originalSpace);
                cloned = newDef.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to clone factory definition {DefinitionId} by user {UserId}",
                    definition.Id, user.Id);
                throw;
            }
        });

        await FactoryDefinitionCreated.InvokeHierarchy(cloned,
            GroupName.FactoryDefinitionHierarchy(space.Project.Id, space.Id, null));
        await SpaceUpdated.InvokeHierarchy(space,
            GroupName.SpaceHierarchy(space.Project.Id, space.Id));

        return cloned;
    }

    /// <summary>
    /// Creates a factory definition by extracting selected jobs and their edges.
    /// Selected jobs become sub-job blueprints. Edges between them become internal edges.
    /// Incoming edges from non-selected jobs become external edges.
    /// </summary>
    public async Task<ReadOnlyFactoryDefinition> CreateFactoryDefinitionFromJobs(
        ReadOnlyUser user, ReadOnlySpace space,
        IEnumerable<ReadOnlyJob> selectedJobs, IEnumerable<ReadOnlyEdge> selectedEdges)
    {
        ReadOnlyFactoryDefinition created = null;

        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalSpace = ResolveSpace(space.Project.Id, space.Id);

                var selectedJobIds = new HashSet<int>(selectedJobs.Select(j => j.Id));

                var def = originalSpace.CreateFactoryDefinition();
                def.Alias = "New Factory";

                // Clone selected jobs as blueprints with local IDs
                var realIdToBlueprintId = new Dictionary<int, int>();
                int blueprintId = 1;
                foreach (var roJob in selectedJobs)
                {
                    var originalJob = originalSpace.FindJob(roJob.Id)
                        ?? throw new Exception($"Job {roJob.Id} not found");

                    // Clone via Space.CreateJob then remove from space's job list
                    // (blueprints live only inside the definition, not in Space._Jobs)
                    var blueprint = originalSpace.CreateJob(originalJob.TypeGuid, originalJob, null);
                    originalSpace.RemoveJobFromList(blueprint);
                    blueprint.Id = blueprintId;
                    blueprint.Status = JobStatus.Building;
                    blueprint.DirectoryName = "";
                    blueprint.ClearProperties();

                    def.SubJobs.Add(blueprint);
                    realIdToBlueprintId[roJob.Id] = blueprintId;
                    blueprintId++;
                }

                // Classify edges
                foreach (var roEdge in selectedEdges)
                {
                    var edge = originalSpace.FindEdge(roEdge.Id);
                    if (edge == null) continue;

                    int sourceJobId = edge.Source.Job.Id;
                    int targetJobId = edge.Target.Job.Id;

                    bool sourceSelected = selectedJobIds.Contains(sourceJobId);
                    bool targetSelected = selectedJobIds.Contains(targetJobId);

                    if (sourceSelected && targetSelected)
                    {
                        // Internal edge
                        def.InternalEdges.Add(new FactoryEdge(
                            $"{realIdToBlueprintId[sourceJobId]}.{edge.Source.Name}",
                            $"{realIdToBlueprintId[targetJobId]}.{edge.Target.Name}"));
                    }
                }

                // Capture incoming external edges (from non-selected parents to selected children)
                foreach (var roJob in selectedJobs)
                {
                    var originalJob = originalSpace.FindJob(roJob.Id);
                    if (originalJob == null) continue;

                    foreach (var portIn in originalJob.PortsIn.Values)
                    {
                        foreach (var edge in portIn.Edges)
                        {
                            if (!selectedJobIds.Contains(edge.Source.Job.Id))
                            {
                                def.ExternalEdges.Add(new FactoryExternalEdge(
                                    realIdToBlueprintId[roJob.Id],
                                    portIn.Name,
                                    edge.Source.Job.Id,
                                    edge.Source.Name));
                            }
                        }
                    }
                }

                _dataRepository.MarkSpaceForSave(originalSpace);
                created = def.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e,
                    "Failed to create factory definition from jobs by user {UserId}", user.Id);
                throw;
            }
        });

        await FactoryDefinitionCreated.InvokeHierarchy(created,
            GroupName.FactoryDefinitionHierarchy(space.Project.Id, space.Id, null));
        await SpaceUpdated.InvokeHierarchy(space,
            GroupName.SpaceHierarchy(space.Project.Id, space.Id));

        return created;
    }
}
