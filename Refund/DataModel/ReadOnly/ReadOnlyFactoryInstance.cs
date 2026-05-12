using System.Collections.ObjectModel;

namespace Refund.DataModel.ReadOnly;

public sealed class ReadOnlyFactoryInstance : IViewItem
{
    private readonly FactoryInstance _instance;

    internal ReadOnlyFactoryInstance(FactoryInstance instance)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
    }

    public int Id => _instance.Id;
    public string Alias => _instance.Alias;
    public string QualifiedName => _instance.QualifiedName;
    public ItemType ItemType => ItemType.FactoryInstance;

    public string HeroImage => null;
    public string Notes => _instance.Notes;
    public string ColorTag => _instance.ColorTag;

    public DateTime UpdateDate => _instance.UpdateDate;
    public ReadOnlyUser UpdatedBy => _instance.UpdatedBy?.AsReadOnly();

    public int DefinitionId => _instance.DefinitionId;
    public IReadOnlyList<int> SubJobIds => _instance.SubJobIds.AsReadOnly();

    public ReadOnlyFactoryDefinition Definition => _instance.Definition?.AsReadOnly();

    public IReadOnlyList<ReadOnlyJob> SubJobs =>
        _instance.SubJobs.Select(j => j.AsReadOnly()).ToList().AsReadOnly();

    public DiagramLayout? DiagramLayout => _instance.DiagramLayout;

    public JobStatus AggregateStatus => _instance.AggregateStatus;

    public ReadOnlySpace Space => _instance.Space?.AsReadOnly();

    public IReadOnlyList<ReadOnlyJobEvent> GetEvents(EventType? type)
    {
        if (!type.HasValue)
            return _instance.Events
                .OrderBy(e => e.Timestamp)
                .Select(e => e.AsReadOnly())
                .ToList().AsReadOnly();

        return _instance.Events
            .Where(e => e.Type == type.Value)
            .OrderBy(e => e.Timestamp)
            .Select(e => e.AsReadOnly())
            .ToList().AsReadOnly();
    }

    public ReadOnlyJobEvent GetMostRecentEvent(EventType? type = null)
    {
        var events = type.HasValue
            ? _instance.Events.Where(e => e.Type == type.Value)
            : _instance.Events;

        return events
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefault()
            ?.AsReadOnly();
    }
}
