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
public readonly record struct SelectionKey(ItemType Type, int Id)
{
    public static SelectionKey ForJob(int id) => new(ItemType.Job, id);
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
