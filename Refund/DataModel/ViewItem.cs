using Refund.DataModel.ReadOnly;

namespace Refund.DataModel;

/// <summary>
/// Distinguishes between item types in lists and selection tracking.
/// </summary>
public enum ItemType
{
    Project,
    Space,
    View,
    Job,
    Folder,
    FactoryInstance,
    FactoryDefinition
}

/// <summary>
/// Marker interface for objects that can be placed in a folder's item list.
/// Both <see cref="Job"/> and <see cref="Folder"/> implement this.
/// </summary>
public interface IFolderContent
{
    int Id { get; }
}

/// <summary>
/// Typed key for item selection, avoiding ID collisions between different item types.
/// </summary>
/// <remarks>
/// Job IDs are only unique within a space. Screens that show jobs from a single space (views, diagrams)
/// use unscoped keys that are resolved against the session's space. Screens that mix jobs from several
/// spaces (Home) use scoped keys carrying the project and space IDs. Scoped and unscoped keys for the
/// same job are not equal, so a screen must use one form consistently.
/// </remarks>
public readonly record struct SelectionKey(ItemType Type, int Id, int? ProjectId = null, int? SpaceId = null)
{
    /// <summary>
    /// Whether this key identifies its item across spaces rather than within the session's space.
    /// </summary>
    public bool IsScoped => ProjectId.HasValue && SpaceId.HasValue;

    public static SelectionKey ForJob(int id) => new(ItemType.Job, id);

    /// <summary>
    /// Creates a key that identifies the job by project, space, and job ID.
    /// </summary>
    public static SelectionKey ForJob(int projectId, int spaceId, int id) => new(ItemType.Job, id, projectId, spaceId);

    /// <summary>
    /// Creates a scoped key for a job, or an unscoped key if the job isn't attached to a space.
    /// </summary>
    public static SelectionKey ForJob(ReadOnlyJob job) =>
        job.Space?.Project != null ? ForJob(job.Space.Project.Id, job.Space.Id, job.Id) : ForJob(job.Id);

    public static SelectionKey ForFolder(int id) => new(ItemType.Folder, id);
    public static SelectionKey ForView(int id) => new(ItemType.View, id);
    public static SelectionKey ForSpace(int id) => new(ItemType.Space, id);
    public static SelectionKey ForProject(int id) => new(ItemType.Project, id);
    public static SelectionKey ForFactoryInstance(int id) => new(ItemType.FactoryInstance, id);
    public static SelectionKey ForFactoryDefinition(int id) => new(ItemType.FactoryDefinition, id);
}

public static class FolderContentExtensions
{
    /// <summary>
    /// Converts a mutable IFolderContent to its read-only IViewItem counterpart.
    /// </summary>
    public static IViewItem AsReadOnlyViewItem(this IFolderContent item) => item switch
    {
        Job j => j.AsReadOnly(),
        Folder f => f.AsReadOnly(),
        FactoryInstance fi => fi.AsReadOnly(),
        _ => null
    };
}
