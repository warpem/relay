using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// A read-only decorator for the Project class, providing immutable access to project data.
/// Projects are top-level containers for spaces, used to organize related workflows.
/// </summary>
public sealed class ReadOnlyProject : IIdentifiable, IAudited, IAnnotated, IJobContainer
{
    /// <summary>
    /// The wrapped mutable project instance.
    /// </summary>
    private readonly Project _project;
    
    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyProject"/> class.
    /// </summary>
    /// <param name="project">The mutable project to wrap.</param>
    /// <exception cref="ArgumentNullException">Thrown if the project parameter is null.</exception>
    internal ReadOnlyProject(Project project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    /// <summary>
    /// Gets the unique identifier for this project.
    /// </summary>
    public int Id => _project.Id;
    
    /// <summary>
    /// Gets the user-defined display name of this project.
    /// </summary>
    public string Alias => _project.Alias;

    public IReadOnlyList<ReadOnlyJobEvent> GetEvents(EventType? type)
    {
        if (!type.HasValue)
            return new List<ReadOnlyJobEvent>().AsReadOnly();

        return type switch
        {
            EventType.Created => new List<ReadOnlyJobEvent>([
                new ReadOnlyJobEvent(new JobEvent(EventType.Created,
                                                  _project.CreationDate,
                                                  _project.CreatedBy))
            ]).AsReadOnly(),
            
            _ => new List<ReadOnlyJobEvent>().AsReadOnly()
        };
    }

    public ReadOnlyJobEvent GetMostRecentEvent(EventType? type = null)
    {
        if (!type.HasValue || type.Value != EventType.Created)
            return null;
        
        return new ReadOnlyJobEvent(new JobEvent(EventType.Created, 
                                                 _project.CreationDate, 
                                                 _project.CreatedBy));
    }
    
    /// <summary>
    /// Gets the date and time when this project was last updated.
    /// </summary>
    public DateTime UpdateDate => _project.UpdateDate;
    
    /// <summary>
    /// Gets the user who last updated this project.
    /// </summary>
    public ReadOnlyUser UpdatedBy => _project.UpdatedBy.AsReadOnly();

    /// <summary>
    /// Gets the path to the hero image for this project.
    /// The hero image is displayed in the UI as a banner or icon.
    /// </summary>
    public string HeroImage => _project.HeroImage;
    
    /// <summary>
    /// Gets the user-provided notes or description of this project.
    /// </summary>
    public string Notes => _project.Notes;
    
    /// <summary>
    /// Gets a fully qualified name that combines the ID and alias.
    /// This provides a unique, human-readable identifier for UI display.
    /// </summary>
    public string QualifiedName => _project.QualifiedName;

    /// <summary>
    /// Gets the owner of this project.
    /// The owner has full administrative rights to the project.
    /// </summary>
    public ReadOnlyUser Owner => _project.Owner.AsReadOnly();

    /// <summary>
    /// Gets a read-only collection of users who are members of this project.
    /// Members have access to view and potentially modify project contents.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyUser> Members => 
        new(_project.Members.Select(u => u.AsReadOnly()).ToList());
    
    /// <summary>
    /// Gets a read-only collection of spaces contained in this project.
    /// Spaces are containers for workflow graphs consisting of connected jobs.
    /// </summary>
    public ReadOnlyCollection<ReadOnlySpace> Spaces =>
        new(_project.Spaces.Select(s => s.AsReadOnly()).ToList());
    
    /// <summary>
    /// Finds a space within this project by its ID.
    /// </summary>
    /// <param name="id">The ID of the space to find.</param>
    /// <returns>A read-only wrapper of the found space, or null if no space with the given ID exists in the project.</returns>
    public ReadOnlySpace FindSpace(int id) => _project.FindSpace(id)?.AsReadOnly();
    
    /// <summary>
    /// Converts this project to a JSON representation.
    /// </summary>
    /// <returns>A JSON node containing the serialized project data.</returns>
    public JsonNode ToJson() => _project.ToJson();

    /// <summary>
    /// Gets a read-only collection of all jobs contained in all spaces of this project.
    /// This provides a flattened view of all jobs across the project hierarchy.
    /// </summary>
    public ReadOnlyCollection<ReadOnlyJob> Jobs => new(Spaces.SelectMany(s => s.Jobs).ToList());
}