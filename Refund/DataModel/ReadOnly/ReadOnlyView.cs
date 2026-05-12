using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// A read-only decorator for the View class, providing immutable access to view data.
/// Views provide visual representations of jobs in a space, with layout information.
/// </summary>
public sealed class ReadOnlyView : IIdentifiable, IAudited, IAnnotated, IJobContainer
{
    /// <summary>
    /// The wrapped mutable view instance.
    /// </summary>
    private readonly View _view;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyView"/> class.
    /// </summary>
    /// <param name="view">The mutable view to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if the view parameter is null.</exception>
    internal ReadOnlyView(View view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
    }

    /// <summary>
    /// Gets the read-only space that contains this view.
    /// </summary>
    public ReadOnlySpace Space => _view.Space?.AsReadOnly();
    
    /// <summary>
    /// Gets the unique identifier for this view.
    /// </summary>
    public int Id => _view.Id;
    
    /// <summary>
    /// Gets the user-defined display name of this view.
    /// </summary>
    public string Alias => _view.Alias;
    
    /// <summary>
    /// Gets a fully qualified name that combines the ID and alias.
    /// This provides a unique, human-readable identifier for UI display.
    /// </summary>
    public string QualifiedName => _view.QualifiedName;

    public IReadOnlyList<ReadOnlyJobEvent> GetEvents(EventType? type)
    {
        if (!type.HasValue)
            return new List<ReadOnlyJobEvent>().AsReadOnly();

        return type switch
        {
            EventType.Created => new List<ReadOnlyJobEvent>([
                new ReadOnlyJobEvent(new JobEvent(EventType.Created,
                                                  _view.CreationDate,
                                                  _view.CreatedBy))
            ]).AsReadOnly(),
            
            _ => new List<ReadOnlyJobEvent>().AsReadOnly()
        };
    }

    public ReadOnlyJobEvent GetMostRecentEvent(EventType? type = null)
    {
        if (!type.HasValue || type.Value != EventType.Created)
            return null;
        
        return new ReadOnlyJobEvent(new JobEvent(EventType.Created, 
                                                 _view.CreationDate, 
                                                 _view.CreatedBy));
    }
    
    /// <summary>
    /// Gets the date and time when this view was last updated.
    /// </summary>
    public DateTime UpdateDate => _view.UpdateDate;
    
    /// <summary>
    /// Gets the user who last updated this view.
    /// </summary>
    public ReadOnlyUser UpdatedBy => _view.UpdatedBy.AsReadOnly();

    /// <summary>
    /// Gets the path to the hero image for this view.
    /// The hero image is displayed in the UI as a banner or icon.
    /// </summary>
    public string HeroImage => _view.HeroImage;
    
    /// <summary>
    /// Gets the user-provided notes or description of this view.
    /// </summary>
    public string Notes => _view.Notes;

    /// <summary>
    /// Gets the diagram layout for this view, if computed.
    /// </summary>
    public DiagramLayout? DiagramLayout => _view.DiagramLayout;

    /// <summary>
    /// Gets a read-only collection of jobs displayed in this view.
    /// A view can display a subset of jobs from its containing space.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyJob> Jobs =>
        new(_view.Jobs.Select(j => j.AsReadOnly()).ToList());

    /// <summary>
    /// Gets all folders in this view.
    /// </summary>
    public IReadOnlyList<ReadOnlyFolder> Folders =>
        _view.Folders.Select(f => f.AsReadOnly()).ToList().AsReadOnly();

    /// <summary>
    /// Gets root-level items (jobs and folders not inside any folder).
    /// </summary>
    public IReadOnlyList<IViewItem> RootItems =>
        _view.RootItems.Select(i => i.AsReadOnlyViewItem()).Where(x => x != null).ToList().AsReadOnly();

    public IReadOnlyList<ReadOnlyFactoryInstance> FactoryInstances =>
        _view.FactoryInstances.Select(fi => fi.AsReadOnly()).ToList().AsReadOnly();

    public ReadOnlyJob FindJob(int id) => _view.FindJob(id)?.AsReadOnly();

    public ReadOnlyFolder FindFolder(int id) => _view.FindFolder(id)?.AsReadOnly();

    public ReadOnlyFactoryInstance FindFactoryInstance(int id) =>
        _view.FindFactoryInstance(id)?.AsReadOnly();

    /// <summary>
    /// Finds which folder contains the given job, or null if it's at root level.
    /// </summary>
    public ReadOnlyFolder FindFolderContainingJob(int jobId) =>
        _view.FindFolderContainingJob(jobId)?.AsReadOnly();

    public JsonNode ToJson() => _view.ToJson();
}