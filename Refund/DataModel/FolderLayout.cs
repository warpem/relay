namespace Refund.DataModel;

public readonly struct FolderLayoutNode
{
    public int ItemId { get; init; }
    public bool IsFolder { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
}

public readonly struct FolderLayoutEdge
{
    public double SourceX { get; init; }
    public double SourceY { get; init; }
    public double TargetX { get; init; }
    public double TargetY { get; init; }
    public IReadOnlyList<(double X, double Y)> BendPoints { get; init; }
}

public class FolderLayout
{
    public double GraphWidth { get; set; }
    public double GraphHeight { get; set; }
    public string ConnectivityHash { get; set; } = "";
    public List<FolderLayoutNode> Nodes { get; set; } = [];
    public List<FolderLayoutEdge> Edges { get; set; } = [];
}
