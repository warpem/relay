using System.Collections.ObjectModel;

namespace Refund.DataModel.ReadOnly;

public sealed class ReadOnlyFactoryDefinition
{
    private readonly FactoryDefinition _definition;

    internal ReadOnlyFactoryDefinition(FactoryDefinition definition)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    public int Id => _definition.Id;
    public string Alias => _definition.Alias;
    public string QualifiedName => _definition.QualifiedName;

    public IReadOnlyList<ExposedPort> ExposedPortsIn => _definition.ExposedPortsIn.AsReadOnly();
    public IReadOnlyList<ExposedPort> ExposedPortsOut => _definition.ExposedPortsOut.AsReadOnly();
    public IReadOnlyList<ExposedProperty> ExposedProperties => _definition.ExposedProperties.AsReadOnly();
    public IReadOnlyList<FactoryEdge> InternalEdges => _definition.InternalEdges.AsReadOnly();
    public IReadOnlyList<FactoryExternalEdge> ExternalEdges => _definition.ExternalEdges.AsReadOnly();
    public IReadOnlyDictionary<int, int?> QueueAssignments =>
        new ReadOnlyDictionary<int, int?>(_definition.QueueAssignments);
    public DiagramLayout DiagramLayout => _definition.DiagramLayout;
    public FolderLayout CardLayout
    {
        get
        {
            if (_definition.CardLayout == null && _definition.SubJobs.Count > 0)
                _definition.CardLayout = Utils.FolderLayoutComputer.ComputeCardLayoutForDefinition(this, null);
            return _definition.CardLayout;
        }
    }

    public IReadOnlyList<ReadOnlyJob> SubJobs =>
        _definition.SubJobs.Select(j => j.AsReadOnly()).ToList().AsReadOnly();
}
