using System.Collections.ObjectModel;

namespace Refund.DataModel.ReadOnly;

/// <summary>
/// Interface for objects that have unique identifiers and display names.
/// This is implemented by most model objects like jobs, spaces, projects, etc.
/// </summary>
public interface IIdentifiable
{
    /// <summary>
    /// Gets the unique identifier for this object.
    /// </summary>
    public int Id { get; }
    
    /// <summary>
    /// Gets the user-defined display name for this object.
    /// </summary>
    public string Alias { get; }
    
    /// <summary>
    /// Gets a fully qualified name that combines the ID and alias.
    /// This provides a unique, human-readable identifier for UI display.
    /// </summary>
    public string QualifiedName { get; }
}

/// <summary>
/// Interface for objects that support annotations such as hero images and notes.
/// This is implemented by objects like jobs, spaces, projects, and views.
/// </summary>
public interface IAnnotated
{
    /// <summary>
    /// Gets the path to the hero image for this object.
    /// The hero image is displayed in the UI as a banner or icon.
    /// </summary>
    public string HeroImage { get; }
    
    /// <summary>
    /// Gets the user-provided notes or description of this object.
    /// </summary>
    public string Notes { get; }
}

/// <summary>
/// Interface for objects that track audit information such as creation and update metadata.
/// This is implemented by objects like jobs, spaces, projects, and views.
/// </summary>
public interface IAudited
{
    /// <summary>
    /// Gets the date and time when this object was last updated.
    /// </summary>
    DateTime UpdateDate { get; }
    
    /// <summary>
    /// Gets the user who last updated this object.
    /// </summary>
    ReadOnlyUser UpdatedBy { get; }

    /// <summary>
    /// Gets all events, or events of the specified type.
    /// </summary>
    /// <param name="type">The type of events to find, or null to get all events.</param>
    /// <returns>A collection of events, ordered by timestamp.</returns>
    public IReadOnlyList<ReadOnlyJobEvent> GetEvents(EventType? type);

    /// <summary>
    /// Gets the most recent event, optionally filtered by type.
    /// </summary>
    /// <param name="type">The type of event to find, or null to get the most recent event of any type.</param>
    /// <returns>The most recent event matching the criteria, or null if no matching events exist.</returns>
    ReadOnlyJobEvent GetMostRecentEvent(EventType? type = null);
}

/// <summary>
/// Interface for objects that contain collections of jobs.
/// This is implemented by objects like spaces and views.
/// </summary>
public interface IJobContainer
{
    /// <summary>
    /// Gets a read-only collection of jobs contained by this object.
    /// </summary>
    ReadOnlyCollection<ReadOnlyJob> Jobs { get; }
}