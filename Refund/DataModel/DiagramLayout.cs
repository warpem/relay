namespace Refund.DataModel;

public readonly struct DiagramLayoutNode
{
    public int ItemId { get; init; }
    public bool IsFolder { get; init; }
    public bool IsFactoryInstance { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public readonly struct DiagramLayoutEdge
{
    public int SourceJobId { get; init; }
    public string SourcePortName { get; init; }
    public int TargetJobId { get; init; }
    public string TargetPortName { get; init; }
    public string ResourceType { get; init; }
    public double SourceX { get; init; }
    public double SourceY { get; init; }
    public double TargetX { get; init; }
    public double TargetY { get; init; }
    public IReadOnlyList<(double X, double Y)> BendPoints { get; init; }
}

public class DiagramLayout
{
    public double GraphWidth { get; set; }
    public double GraphHeight { get; set; }
    public string ConnectivityHash { get; set; } = "";
    public List<DiagramExternalConnection> ExternalConnections { get; set; } = [];
    public List<DiagramLayoutNode> Nodes { get; set; } = [];
    public List<DiagramLayoutEdge> Edges { get; set; } = [];
}

/// <summary>A local annotation for connections crossing the diagram boundary, not a job node.</summary>
public sealed class DiagramExternalConnection
{
    public bool IsOutput { get; init; }
    public string PortName { get; init; }
    public string ResourceType { get; init; }
    public List<int> JobIds { get; init; } = [];
    public bool IsSummary => IsOutput && JobIds.Count > 3;
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double PortX { get; init; }
    public double PortY { get; init; }
}
