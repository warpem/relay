using Refund.DataModel;

namespace Refund.Utils;

public static partial class DiagramLayoutComputer
{
    private const double ExternalWidth = 180;
    private const double ExternalTop = 16;
    private const double ExternalInset = ExternalWidth + 28;

    private sealed class ExternalPortGroup
    {
        public int Index;
        public int PortIndex;
        public bool IsOutput;
        public string PortName;
        public string ResourceType;
        public SortedSet<int> JobIds = [];
        public double Y;
        public double Height;
    }

    // Satellites reserve space within the owner rectangle. They never participate in
    // dependency ranking, and shared external jobs never couple otherwise unrelated owners.
    private static Dictionary<int, (double width, double height)> ReserveExternalSpace(
        Dictionary<int, (double width, double height)> cards, List<ExternalPortGroup> groups)
    {
        var envelopes = new Dictionary<int, (double width, double height)>(cards);
        foreach (var owner in groups.GroupBy(g => g.Index))
        {
            var card = cards[owner.Key];
            double height = card.height;
            foreach (var side in owner.GroupBy(g => g.IsOutput))
            {
                double bottom = 0;
                foreach (var group in side.OrderBy(g => g.PortIndex))
                {
                    int rows = group.IsOutput && group.JobIds.Count > 3 ? 1 : group.JobIds.Count;
                    group.Height = 26 + rows * 20;
                    group.Y = Math.Max(bottom, 38 + group.PortIndex * 22 - group.Height / 2);
                    bottom = group.Y + group.Height + 8;
                    height = Math.Max(height, bottom - 8);
                }
            }
            envelopes[owner.Key] = (card.width + owner.Select(g => g.IsOutput).Distinct().Count() * ExternalInset, height + ExternalTop);
        }
        return envelopes;
    }

    private static List<(double X, double Y)> RouteAroundExternalConnections(
        DiagramLayout result, int sourceIndex, int targetIndex,
        double sourceX, double sourceY, double targetX, double targetY,
        List<(double X, double Y)> bends, List<ExternalPortGroup> groups)
    {
        var points = new List<(double X, double Y)>();
        // Use the reserved strip above the annotations to take internal edges out
        // of/into the envelope without drawing through the external job links.
        if (groups.Any(g => g.Index == sourceIndex && g.IsOutput))
        {
            var owner = result.Nodes[sourceIndex];
            points.Add((sourceX + 12, sourceY));
            points.Add((sourceX + 12, owner.Y + 4));
            points.Add((owner.X + owner.Width + 12, owner.Y + 4));
        }
        points.AddRange(bends);
        if (groups.Any(g => g.Index == targetIndex && !g.IsOutput))
        {
            var owner = result.Nodes[targetIndex];
            points.Add((owner.X - 12, owner.Y + 4));
            points.Add((targetX - 14, owner.Y + 4));
            points.Add((targetX - 14, targetY));
        }
        return points;
    }

    private static void PlaceExternalConnections(DiagramLayout result,
        Dictionary<int, (double width, double height)> cards, List<ExternalPortGroup> groups)
    {
        for (int i = 0; i < result.Nodes.Count; i++)
        {
            var envelope = result.Nodes[i];
            var owned = groups.Where(g => g.Index == i).ToList();
            var card = cards[i];
            double x = envelope.X + (owned.Any(g => !g.IsOutput) ? ExternalInset : 0);
            result.Nodes[i] = new DiagramLayoutNode
            {
                ItemId = envelope.ItemId, IsFolder = envelope.IsFolder,
                IsFactoryInstance = envelope.IsFactoryInstance,
                X = x, Y = envelope.Y + (owned.Count > 0 ? ExternalTop : 0), Width = card.width, Height = card.height
            };
            foreach (var group in owned)
                result.ExternalConnections.Add(new DiagramExternalConnection
                {
                    IsOutput = group.IsOutput, PortName = group.PortName, ResourceType = group.ResourceType,
                    JobIds = group.JobIds.ToList(),
                    X = group.IsOutput ? x + card.width + 28 : envelope.X,
                    Y = envelope.Y + ExternalTop + group.Y, Width = ExternalWidth, Height = group.Height,
                    PortX = group.IsOutput ? x + card.width : x + 2,
                    PortY = envelope.Y + ExternalTop + 38 + group.PortIndex * 22
                });
        }
    }
}
