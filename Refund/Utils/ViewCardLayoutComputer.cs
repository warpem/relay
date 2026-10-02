using System.Security.Cryptography;
using System.Text;
using ElkSharp.Public;
using Refund.DataModel;

namespace Refund.Utils;

/// <summary>
/// Computes the compact minimap shown on view cards. Unlike the view's DiagramLayout, folders and
/// factory instances are not collapsed: every job in the view becomes a node, and only job-to-job
/// edges with both ends in the view are drawn. Uses the same conventions as folder card layouts.
/// </summary>
public static class ViewCardLayoutComputer
{
    public static FolderLayout ComputeLayout(View view, Space space, FolderLayout? previous)
    {
        var (jobIds, edgePairs) = CollectGraph(view, space);
        if (jobIds.Count == 0)
            return previous is { ConnectivityHash: "", Nodes.Count: 0, Edges.Count: 0 }
                ? previous
                : new FolderLayout();

        var hash = ComputeConnectivityHash(jobIds, edgePairs);
        if (previous != null && previous.ConnectivityHash == hash)
            return previous;

        var graph = new LayoutGraph();
        graph.Options.EdgeRouting = EdgeRoutingStyle.Polyline;
        graph.Options.NodeSpacing = 8;
        graph.Options.LayerSpacing = 10;
        graph.Options.Padding = 8;
        graph.Options.Thoroughness = 10;

        var layoutNodes = new List<LayoutNode>(jobIds.Count);
        var nodeIndex = new Dictionary<int, int>(); // job.Id -> node index
        foreach (var id in jobIds)
        {
            nodeIndex[id] = layoutNodes.Count;
            layoutNodes.Add(graph.AddNode(10, 10));
        }

        var east = new Dictionary<LayoutNode, LayoutPort>();
        var west = new Dictionary<LayoutNode, LayoutPort>();
        var edgeIndexPairs = new List<(int, int)>(edgePairs.Count);
        foreach (var (sourceId, targetId) in edgePairs)
        {
            var source = layoutNodes[nodeIndex[sourceId]];
            var target = layoutNodes[nodeIndex[targetId]];
            if (!east.TryGetValue(source, out var sourcePort))
                east[source] = sourcePort = source.AddPort(PortSideHint.East);
            if (!west.TryGetValue(target, out var targetPort))
                west[target] = targetPort = target.AddPort(PortSideHint.West);
            graph.AddEdge(sourcePort, targetPort);
            edgeIndexPairs.Add((nodeIndex[sourceId], nodeIndex[targetId]));
        }

        LayeredLayoutEngine.Layout(graph);

        // Compact disconnected components the same way folder cards do
        var nodes = new DisconnectedComponentCompactor.NodeRect[layoutNodes.Count];
        for (int i = 0; i < layoutNodes.Count; i++)
        {
            var node = layoutNodes[i];
            nodes[i] = new DisconnectedComponentCompactor.NodeRect
            {
                X = node.X, Y = node.Y, Width = node.Width, Height = node.Height,
                ItemId = jobIds[i], IsFolder = false
            };
        }

        var elkEdges = graph.Edges.ToList();
        var edges = new DisconnectedComponentCompactor.EdgeCoords[elkEdges.Count];
        for (int i = 0; i < elkEdges.Count; i++)
        {
            var edge = elkEdges[i];
            edges[i] = new DisconnectedComponentCompactor.EdgeCoords
            {
                SourceNodeIndex = edgeIndexPairs[i].Item1, TargetNodeIndex = edgeIndexPairs[i].Item2,
                SourceX = edge.SourcePoint.X, SourceY = edge.SourcePoint.Y,
                TargetX = edge.TargetPoint.X, TargetY = edge.TargetPoint.Y,
                BendPoints = edge.BendPoints.ToList()
            };
        }

        DisconnectedComponentCompactor.Compact(nodes, edges, nodeSpacing: 8, singletonGap: 16);

        var result = new FolderLayout { ConnectivityHash = hash };
        foreach (var node in nodes)
            result.Nodes.Add(new FolderLayoutNode
            {
                ItemId = node.ItemId, IsFolder = false,
                X = node.X, Y = node.Y,
                Width = node.Width, Height = node.Height
            });
        foreach (var edge in edges)
            result.Edges.Add(new FolderLayoutEdge
            {
                SourceX = edge.SourceX, SourceY = edge.SourceY,
                TargetX = edge.TargetX, TargetY = edge.TargetY,
                BendPoints = edge.BendPoints
            });

        // ELK's reported graph size may not encompass all nodes/edges — compute actual bounding box
        double bbMaxX = 0, bbMaxY = 0;
        foreach (var node in result.Nodes)
        {
            bbMaxX = Math.Max(bbMaxX, node.X + node.Width);
            bbMaxY = Math.Max(bbMaxY, node.Y + node.Height);
        }
        foreach (var edge in result.Edges)
        {
            bbMaxX = Math.Max(bbMaxX, Math.Max(edge.SourceX, edge.TargetX));
            bbMaxY = Math.Max(bbMaxY, Math.Max(edge.SourceY, edge.TargetY));
            if (edge.BendPoints != null)
                foreach (var bp in edge.BendPoints)
                {
                    bbMaxX = Math.Max(bbMaxX, bp.X);
                    bbMaxY = Math.Max(bbMaxY, bp.Y);
                }
        }
        result.GraphWidth = bbMaxX + 2;
        result.GraphHeight = bbMaxY + 2;

        return result;
    }

    public static string ComputeConnectivityHash(View view, Space space)
    {
        var (jobIds, edgePairs) = CollectGraph(view, space);
        return ComputeConnectivityHash(jobIds, edgePairs);
    }

    /// <summary>
    /// Returns the view's distinct job IDs and deduplicated internal job-to-job edges, both sorted
    /// so the layout depends only on connectivity, not on view or space ordering.
    /// </summary>
    private static (List<int> JobIds, List<(int Source, int Target)> EdgePairs) CollectGraph(View view, Space space)
    {
        var jobIdSet = view.Jobs.Select(job => job.Id).ToHashSet();
        var jobIds = jobIdSet.Order().ToList();

        var edgePairs = new SortedSet<(int, int)>();
        foreach (var edge in space.Edges)
        {
            int sourceId = edge.Source.Job.Id;
            int targetId = edge.Target.Job.Id;
            if (sourceId != targetId && jobIdSet.Contains(sourceId) && jobIdSet.Contains(targetId))
                edgePairs.Add((sourceId, targetId));
        }

        return (jobIds, edgePairs.ToList());
    }

    private static string ComputeConnectivityHash(List<int> jobIds, List<(int Source, int Target)> edgePairs)
    {
        var raw = "view-card-v1|" + string.Join(",", jobIds.Select(id => $"J{id}")) + "|" +
                  string.Join(",", edgePairs.Select(e => $"J{e.Source}->J{e.Target}"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }
}
