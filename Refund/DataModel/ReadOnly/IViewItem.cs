namespace Refund.DataModel.ReadOnly;

/// <summary>
/// Common interface for items displayed in a view — either jobs or folders.
/// Used by the UI to render mixed lists of jobs and folders uniformly.
/// </summary>
public interface IViewItem : IIdentifiable, IAnnotated, IAudited
{
    ItemType ItemType { get; }
}
