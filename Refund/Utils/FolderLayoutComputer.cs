using System.Security.Cryptography;
using System.Text;
using ElkSharp.Public;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Refund.Utils;

public static class FolderLayoutComputer
{
    public static FolderLayout ComputeLayout(Folder folder, Space space, FolderLayout? previous)
    {
        var hash = ComputeConnectivityHash(folder, space);
        if (previous != null && previous.ConnectivityHash == hash)
            return previous;

        // Collect direct items: jobs and sub-folders (no recursion into sub-folders)
        var directItems = folder.Items.ToList();
        if (directItems.Count == 0)
            return new FolderLayout { ConnectivityHash = hash };

        // Map each direct item to a node key: "J{id}" for jobs, "F{id}" for folders
        // Also map recursive job IDs inside sub-folders back to the sub-folder's node
        var jobToNodeId = new Dictionary<int, int>(); // job.Id -> item index in directItems
        var nodeIsFolder = new Dictionary<int, bool>(); // item index -> isFolder
        var indexToKey = new Dictionary<int, string>(); // item index -> "J5" or "F3"

        for (int i = 0; i < directItems.Count; i++)
        {
            var item = directItems[i];
            if (item is Job job)
            {
                jobToNodeId[job.Id] = i;
                nodeIsFolder[i] = false;
                indexToKey[i] = $"J{job.Id}";
            }
            else if (item is Folder subFolder)
            {
                nodeIsFolder[i] = true;
                indexToKey[i] = $"F{subFolder.Id}";
                // Map all recursive jobs inside this sub-folder to this node
                foreach (var subJob in subFolder.GetAllJobsRecursive())
                    jobToNodeId[subJob.Id] = i;
            }
            else if (item is FactoryInstance fi)
            {
                nodeIsFolder[i] = false;
                indexToKey[i] = $"FI{fi.Id}";
                // Map all sub-job IDs to this node so edges resolve correctly
                foreach (var sjId in fi.SubJobIds)
                    jobToNodeId[sjId] = i;
            }
        }

        // Find internal edges: both source and target jobs map to a direct item
        var edgePairs = new HashSet<(int sourceNode, int targetNode)>();
        foreach (var edge in space.Edges)
        {
            int sourceJobId = edge.Source.Job.Id;
            int targetJobId = edge.Target.Job.Id;

            if (!jobToNodeId.TryGetValue(sourceJobId, out int sourceNode))
                continue;
            if (!jobToNodeId.TryGetValue(targetJobId, out int targetNode))
                continue;
            if (sourceNode == targetNode)
                continue; // skip self-loops

            edgePairs.Add((sourceNode, targetNode));
        }

        // Build ELK layout graph
        var graph = new LayoutGraph();
        graph.Options.EdgeRouting = EdgeRoutingStyle.Polyline;
        graph.Options.NodeSpacing = 8;
        graph.Options.LayerSpacing = 10;
        graph.Options.Padding = 8;
        graph.Options.Thoroughness = 10;

        // Create layout nodes
        var layoutNodes = new Dictionary<int, LayoutNode>(); // item index -> LayoutNode
        var portHelper = new PortHelper();

        for (int i = 0; i < directItems.Count; i++)
        {
            bool isFolder = nodeIsFolder[i];
            double size = isFolder ? 12 : 10;
            layoutNodes[i] = graph.AddNode(size, size);
        }

        // Create layout edges using PortHelper (use list for stable ordering)
        var edgePairList = edgePairs.ToList();
        foreach (var (sourceNode, targetNode) in edgePairList)
            portHelper.AddEdge(graph, layoutNodes[sourceNode], layoutNodes[targetNode]);

        // Incremental layout: use previous positions as hints
        if (previous is { Nodes.Count: > 0 })
        {
            var prevPositions = new Dictionary<string, FolderLayoutNode>();
            foreach (var pn in previous.Nodes)
                prevPositions[pn.IsFolder ? $"F{pn.ItemId}" : $"J{pn.ItemId}"] = pn;

            // Set position hints for all nodes that exist in previous layout.
            // New nodes (not in previous) remain unhinted and are placed by the algorithm.
            bool anyHinted = false;
            for (int i = 0; i < directItems.Count; i++)
            {
                var key = indexToKey[i];
                if (prevPositions.TryGetValue(key, out var prevNode))
                {
                    layoutNodes[i].X = prevNode.X;
                    layoutNodes[i].Y = prevNode.Y;
                    anyHinted = true;
                }
            }

            if (anyHinted)
                graph.Options.Interactive = true;
        }

        // Run layout, then relax strained nodes to fix suboptimal incremental placement
        LayeredLayoutEngine.Layout(graph);
        if (graph.Options.Interactive)
            LayeredLayoutEngine.RelaxLayout(graph, strainThreshold: 3.0, maxIterations: 2);

        // Extract results
        var result = new FolderLayout
        {
            GraphWidth = graph.Width,
            GraphHeight = graph.Height,
            ConnectivityHash = hash
        };

        for (int i = 0; i < directItems.Count; i++)
        {
            var item = directItems[i];
            var node = layoutNodes[i];
            bool isFolder = nodeIsFolder[i];
            int itemId = item is Job j ? j.Id : (item is FactoryInstance fi ? fi.Id : ((Folder)item).Id);

            result.Nodes.Add(new FolderLayoutNode
            {
                ItemId = itemId,
                IsFolder = isFolder,
                X = node.X,
                Y = node.Y,
                Width = node.Width,
                Height = node.Height
            });
        }

        var elkEdges = graph.Edges.ToList();
        for (int i = 0; i < elkEdges.Count; i++)
        {
            var edge = elkEdges[i];
            var src = edge.SourcePoint;
            var tgt = edge.TargetPoint;

            result.Edges.Add(new FolderLayoutEdge
            {
                SourceX = src.X,
                SourceY = src.Y,
                TargetX = tgt.X,
                TargetY = tgt.Y,
                BendPoints = edge.BendPoints.ToList()
            });
        }

        // Post-process: compact disconnected components and reposition singletons
        PostProcessDisconnectedComponents(result, edgePairList);

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

    private static void PostProcessDisconnectedComponents(
        FolderLayout result, List<(int, int)> edgePairList)
    {
        int n = result.Nodes.Count;
        if (n == 0) return;

        var nodes = new DisconnectedComponentCompactor.NodeRect[n];
        for (int i = 0; i < n; i++)
        {
            var nd = result.Nodes[i];
            nodes[i] = new DisconnectedComponentCompactor.NodeRect
            {
                X = nd.X, Y = nd.Y, Width = nd.Width, Height = nd.Height,
                ItemId = nd.ItemId, IsFolder = nd.IsFolder
            };
        }

        // Map result edges to node indices using edgePairList (same insertion order as graph edges)
        var edges = new DisconnectedComponentCompactor.EdgeCoords[result.Edges.Count];
        for (int i = 0; i < result.Edges.Count; i++)
        {
            var e = result.Edges[i];
            var pair = i < edgePairList.Count ? edgePairList[i] : (0, 0);
            edges[i] = new DisconnectedComponentCompactor.EdgeCoords
            {
                SourceNodeIndex = pair.Item1, TargetNodeIndex = pair.Item2,
                SourceX = e.SourceX, SourceY = e.SourceY,
                TargetX = e.TargetX, TargetY = e.TargetY,
                BendPoints = e.BendPoints?.ToList()
            };
        }

        DisconnectedComponentCompactor.Compact(nodes, edges, nodeSpacing: 8, singletonGap: 16);

        // Copy results back
        for (int i = 0; i < n; i++)
        {
            result.Nodes[i] = new FolderLayoutNode
            {
                ItemId = nodes[i].ItemId, IsFolder = nodes[i].IsFolder,
                X = nodes[i].X, Y = nodes[i].Y,
                Width = nodes[i].Width, Height = nodes[i].Height
            };
        }

        for (int i = 0; i < result.Edges.Count; i++)
        {
            result.Edges[i] = new FolderLayoutEdge
            {
                SourceX = edges[i].SourceX, SourceY = edges[i].SourceY,
                TargetX = edges[i].TargetX, TargetY = edges[i].TargetY,
                BendPoints = edges[i].BendPoints
            };
        }
    }

    public static string ComputeConnectivityHash(Folder folder, Space space)
    {
        var directItems = folder.Items.ToList();

        // Build item keys sorted
        var itemKeys = new List<string>();
        var jobToNodeKey = new Dictionary<int, string>();

        foreach (var item in directItems)
        {
            if (item is Job job)
            {
                var key = $"J{job.Id}";
                itemKeys.Add(key);
                jobToNodeKey[job.Id] = key;
            }
            else if (item is Folder subFolder)
            {
                var key = $"F{subFolder.Id}";
                itemKeys.Add(key);
                foreach (var subJob in subFolder.GetAllJobsRecursive())
                    jobToNodeKey[subJob.Id] = key;
            }
            else if (item is FactoryInstance fi)
            {
                var key = $"FI{fi.Id}";
                itemKeys.Add(key);
                foreach (var sjId in fi.SubJobIds)
                    jobToNodeKey[sjId] = key;
            }
        }

        itemKeys.Sort(StringComparer.Ordinal);

        // Build edge pairs
        var edgePairs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var edge in space.Edges)
        {
            if (!jobToNodeKey.TryGetValue(edge.Source.Job.Id, out var srcKey))
                continue;
            if (!jobToNodeKey.TryGetValue(edge.Target.Job.Id, out var tgtKey))
                continue;
            if (srcKey == tgtKey)
                continue;
            edgePairs.Add($"{srcKey}->{tgtKey}");
        }

        var raw = string.Join(",", itemKeys) + "|" + string.Join(",", edgePairs);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Computes a compact minimap layout for a factory definition's sub-jobs and internal edges.
    /// Uses small nodes (10px) and tight spacing, suitable for rendering inside a card.
    /// </summary>
    public static FolderLayout ComputeCardLayoutForDefinition(ReadOnlyFactoryDefinition definition, FolderLayout previousLayout = null)
    {
        var subJobs = definition.SubJobs;
        if (subJobs.Count == 0)
            return new FolderLayout { ConnectivityHash = "" };

        // Build sub-job lookup by actual ID
        var subJobById = new Dictionary<int, ReadOnlyJob>();
        foreach (var subJob in subJobs)
            subJobById[subJob.Id] = subJob;

        // Parse internal edges
        var edgePairs = new List<(int srcId, int tgtId)>();
        foreach (var ie in definition.InternalEdges)
        {
            var srcDot = ie.Source.IndexOf('.');
            var tgtDot = ie.Target.IndexOf('.');
            if (srcDot <= 0 || tgtDot <= 0) continue;
            if (!int.TryParse(ie.Source[..srcDot], out int srcId)) continue;
            if (!int.TryParse(ie.Target[..tgtDot], out int tgtId)) continue;
            if (subJobById.ContainsKey(srcId) && subJobById.ContainsKey(tgtId) && srcId != tgtId)
                edgePairs.Add((srcId, tgtId));
        }

        // Compute connectivity hash
        var itemKeys = subJobById.Keys.OrderBy(k => k).Select(k => $"SJ{k}").ToList();
        var edgeDescs = edgePairs.Select(e => $"SJ{e.srcId}->SJ{e.tgtId}").OrderBy(s => s).ToList();
        var raw = string.Join(",", itemKeys) + "|" + string.Join(",", edgeDescs);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];

        var previous = previousLayout;
        if (previous != null && previous.ConnectivityHash == hash)
            return previous;

        // Build ELK graph with compact settings
        var graph = new LayoutGraph();
        graph.Options.EdgeRouting = EdgeRoutingStyle.Polyline;
        graph.Options.NodeSpacing = 8;
        graph.Options.LayerSpacing = 10;
        graph.Options.Padding = 8;
        graph.Options.Thoroughness = 10;

        var layoutNodes = new Dictionary<int, LayoutNode>();
        var portHelper = new PortHelper();

        foreach (var (id, _) in subJobById)
            layoutNodes[id] = graph.AddNode(10, 10);

        // Deduplicate edges
        var uniqueEdges = new HashSet<(int, int)>(edgePairs);
        var edgeList = uniqueEdges.ToList();
        foreach (var (srcId, tgtId) in edgeList)
            portHelper.AddEdge(graph, layoutNodes[srcId], layoutNodes[tgtId]);

        // Incremental layout hints
        if (previous is { Nodes.Count: > 0 })
        {
            var prevPositions = previous.Nodes.ToDictionary(n => n.ItemId);
            bool anyHinted = false;
            foreach (var (id, _) in subJobById)
            {
                if (prevPositions.TryGetValue(id, out var prevNode))
                {
                    layoutNodes[id].X = prevNode.X;
                    layoutNodes[id].Y = prevNode.Y;
                    anyHinted = true;
                }
            }
            if (anyHinted)
                graph.Options.Interactive = true;
        }

        LayeredLayoutEngine.Layout(graph);
        if (graph.Options.Interactive)
            LayeredLayoutEngine.RelaxLayout(graph, strainThreshold: 3.0, maxIterations: 2);

        // Extract results
        var result = new FolderLayout
        {
            GraphWidth = graph.Width,
            GraphHeight = graph.Height,
            ConnectivityHash = hash
        };

        foreach (var (id, _) in subJobById)
        {
            var node = layoutNodes[id];
            result.Nodes.Add(new FolderLayoutNode
            {
                ItemId = id,
                IsFolder = false,
                X = node.X, Y = node.Y,
                Width = node.Width, Height = node.Height
            });
        }

        var elkEdges = graph.Edges.ToList();
        foreach (var edge in elkEdges)
        {
            result.Edges.Add(new FolderLayoutEdge
            {
                SourceX = edge.SourcePoint.X, SourceY = edge.SourcePoint.Y,
                TargetX = edge.TargetPoint.X, TargetY = edge.TargetPoint.Y,
                BendPoints = edge.BendPoints.ToList()
            });
        }

        // Post-process disconnected components
        var nodeIndexMap = new Dictionary<int, int>();
        for (int i = 0; i < result.Nodes.Count; i++)
            nodeIndexMap[result.Nodes[i].ItemId] = i;

        PostProcessDisconnectedComponents(result, edgeList.Select(e =>
            (nodeIndexMap.GetValueOrDefault(e.Item1), nodeIndexMap.GetValueOrDefault(e.Item2))).ToList());

        // Recompute bounding box
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

    private class PortHelper
    {
        private readonly Dictionary<LayoutNode, LayoutPort> _east = new();
        private readonly Dictionary<LayoutNode, LayoutPort> _west = new();

        private LayoutPort East(LayoutNode node)
        {
            if (!_east.TryGetValue(node, out var port))
                _east[node] = port = node.AddPort(PortSideHint.East);
            return port;
        }

        private LayoutPort West(LayoutNode node)
        {
            if (!_west.TryGetValue(node, out var port))
                _west[node] = port = node.AddPort(PortSideHint.West);
            return port;
        }

        public LayoutEdge AddEdge(LayoutGraph g, LayoutNode source, LayoutNode target)
            => g.AddEdge(East(source), West(target));
    }
}
