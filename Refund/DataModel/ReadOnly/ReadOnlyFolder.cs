using System.Runtime.CompilerServices;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// A read-only decorator for the Folder class, providing immutable access to folder data.
/// </summary>
public sealed class ReadOnlyFolder : IViewItem
{
    private readonly Folder _folder;

    internal ReadOnlyFolder(Folder folder)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
    }

    public int Id => _folder.Id;
    public string Alias => _folder.Alias;
    public string QualifiedName => _folder.QualifiedName;
    public string ColorTag => _folder.ColorTag;
    public string Notes => _folder.Notes;
    public string HeroImage => _folder.HeroImage;
    public DateTime CreationDate => _folder.CreationDate;
    public ReadOnlyUser CreatedBy => _folder.CreatedBy?.AsReadOnly();
    public DateTime UpdateDate => _folder.UpdateDate;
    public ReadOnlyUser UpdatedBy => _folder.UpdatedBy?.AsReadOnly();
    public ItemType ItemType => ItemType.Folder;

    public ReadOnlyView View => _folder.View?.AsReadOnly();
    public ReadOnlyFolder Parent => _folder.ParentFolder?.AsReadOnly();
    public FolderLayout? Layout => _folder.Layout;
    public DiagramLayout? DiagramLayout => _folder.DiagramLayout;

    /// <summary>
    /// Returns the ordered list of children as IViewItem (jobs and folders).
    /// </summary>
    public IReadOnlyList<IViewItem> Items =>
        _folder.Items.Select(i => i.AsReadOnlyViewItem()).Where(x => x != null).ToList().AsReadOnly();

    public IReadOnlyList<ReadOnlyJobEvent> GetEvents(EventType? type)
    {
        if (!type.HasValue)
            return new List<ReadOnlyJobEvent>().AsReadOnly();

        return type switch
        {
            EventType.Created => new List<ReadOnlyJobEvent>([
                new ReadOnlyJobEvent(new JobEvent(EventType.Created,
                                                  _folder.CreationDate,
                                                  _folder.CreatedBy))
            ]).AsReadOnly(),

            _ => new List<ReadOnlyJobEvent>().AsReadOnly()
        };
    }

    /// <summary>
    /// Returns all jobs contained in this folder and all subfolders, recursively.
    /// </summary>
    public IEnumerable<ReadOnlyJob> GetAllJobsRecursive()
    {
        foreach (var item in Items)
        {
            if (item is ReadOnlyJob job)
                yield return job;
            else if (item is ReadOnlyFolder subfolder)
                foreach (var subJob in subfolder.GetAllJobsRecursive())
                    yield return subJob;
        }
    }

    public ReadOnlyJobEvent GetMostRecentEvent(EventType? type = null)
    {
        if (!type.HasValue || type.Value != EventType.Created)
            return null;

        return new ReadOnlyJobEvent(new JobEvent(EventType.Created,
                                                 _folder.CreationDate,
                                                 _folder.CreatedBy));
    }
}
