namespace Refund.Utils;

/// <summary>
/// Compacts vertical gaps between disconnected multi-node components,
/// then places singleton nodes (no edges) in a band at the top, anchored to temporal context.
/// </summary>
internal static class DisconnectedComponentCompactor
{
    internal struct NodeRect
    {
        public double X, Y, Width, Height;
        public int ItemId;
        public bool IsFolder;
    }

    internal struct EdgeCoords
    {
        public int SourceNodeIndex, TargetNodeIndex;
        public double SourceX, SourceY, TargetX, TargetY;
        public List<(double X, double Y)>? BendPoints;
    }

    internal static void Compact(NodeRect[] nodes, EdgeCoords[] edges, double nodeSpacing, double singletonGap)
    {
        int n = nodes.Length;
        if (n == 0) return;

        // --- Step 1: Union-find to identify connected components ---

        var parent = new int[n];
        var ufRank = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        void Union(int a, int b)
        {
            a = Find(a); b = Find(b);
            if (a == b) return;
            if (ufRank[a] < ufRank[b]) (a, b) = (b, a);
            parent[b] = a;
            if (ufRank[a] == ufRank[b]) ufRank[a]++;
        }

        foreach (var edge in edges)
            Union(edge.SourceNodeIndex, edge.TargetNodeIndex);

        // Group by component root
        var components = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!components.TryGetValue(root, out var list))
                components[root] = list = new List<int>();
            list.Add(i);
        }

        // Pre-build component-root -> edge indices mapping (avoids O(C*E) scans later)
        var componentEdges = new Dictionary<int, List<int>>();
        for (int i = 0; i < edges.Length; i++)
        {
            int root = Find(edges[i].SourceNodeIndex);
            if (!componentEdges.TryGetValue(root, out var list))
                componentEdges[root] = list = new List<int>();
            list.Add(i);
        }

        // Classify: multi-node components vs singletons (nodes with 0 edges)
        var nodeHasEdge = new bool[n];
        foreach (var edge in edges)
        {
            nodeHasEdge[edge.SourceNodeIndex] = true;
            nodeHasEdge[edge.TargetNodeIndex] = true;
        }

        var multiNodeComponents = new List<List<int>>();
        var singletons = new List<int>();

        foreach (var (_, members) in components)
        {
            if (members.Count > 1)
                multiNodeComponents.Add(members);
            else if (nodeHasEdge[members[0]])
                multiNodeComponents.Add(members); // single node with edges
            else
                singletons.Add(members[0]);
        }

        // --- Step 2: Compact multi-node components vertically ---

        if (multiNodeComponents.Count > 1)
        {
            // Compute bounding box for each multi-node component (including edge coordinates)
            var componentBounds = new List<(double minY, double maxY, List<int> members)>();
            foreach (var members in multiNodeComponents)
            {
                double minY = double.MaxValue, maxY = double.MinValue;

                foreach (int idx in members)
                {
                    minY = Math.Min(minY, nodes[idx].Y);
                    maxY = Math.Max(maxY, nodes[idx].Y + nodes[idx].Height);
                }

                // Only iterate this component's edges via the pre-built mapping
                int compRoot = Find(members[0]);
                if (componentEdges.TryGetValue(compRoot, out var edgeIndices))
                {
                    foreach (int ei in edgeIndices)
                    {
                        ref var edge = ref edges[ei];
                        minY = Math.Min(minY, Math.Min(edge.SourceY, edge.TargetY));
                        maxY = Math.Max(maxY, Math.Max(edge.SourceY, edge.TargetY));
                        if (edge.BendPoints is { Count: > 0 })
                            foreach (var bp in edge.BendPoints)
                            {
                                minY = Math.Min(minY, bp.Y);
                                maxY = Math.Max(maxY, bp.Y);
                            }
                    }
                }

                componentBounds.Add((minY, maxY, members));
            }

            // Group components into rows: components whose Y ranges overlap are in the same row
            componentBounds.Sort((a, b) => a.minY.CompareTo(b.minY));

            var rows = new List<(double minY, double maxY, List<List<int>> components)>();
            foreach (var (cMinY, cMaxY, members) in componentBounds)
            {
                bool merged = false;
                for (int r = 0; r < rows.Count; r++)
                {
                    var row = rows[r];
                    if (cMinY < row.maxY && cMaxY > row.minY)
                    {
                        rows[r] = (Math.Min(row.minY, cMinY), Math.Max(row.maxY, cMaxY), row.components);
                        rows[r].components.Add(members);
                        merged = true;
                        break;
                    }
                }
                if (!merged)
                    rows.Add((cMinY, cMaxY, new List<List<int>> { members }));
            }

            rows.Sort((a, b) => a.minY.CompareTo(b.minY));

            // Compact gaps between rows to nodeSpacing
            if (rows.Count > 1)
            {
                double currentBottom = rows[0].maxY;
                for (int r = 1; r < rows.Count; r++)
                {
                    var row = rows[r];
                    double desiredTop = currentBottom + nodeSpacing;
                    double dy = desiredTop - row.minY;

                    if (Math.Abs(dy) > 0.5)
                    {
                        // Gather edge indices for all components in this row
                        var rowEdgeIndices = new List<int>();
                        var rowNodeIndices = new List<int>();
                        foreach (var comp in row.components)
                        {
                            foreach (int idx in comp)
                                rowNodeIndices.Add(idx);
                            int compRoot = Find(comp[0]);
                            if (componentEdges.TryGetValue(compRoot, out var eis))
                                rowEdgeIndices.AddRange(eis);
                        }

                        ShiftByIndices(nodes, edges, rowNodeIndices, rowEdgeIndices, dy);
                        rows[r] = (row.minY + dy, row.maxY + dy, row.components);
                    }

                    currentBottom = rows[r].maxY;
                }
            }
        }

        // --- Step 3: Position singleton nodes at the top ---

        if (singletons.Count == 0)
            return;

        var singletonSet = new HashSet<int>(singletons);

        // Build sorted list of non-singleton, non-folder job IDs for anchoring
        var anchors = new List<(int jobId, int nodeIndex)>();
        for (int i = 0; i < n; i++)
        {
            if (!singletonSet.Contains(i) && !nodes[i].IsFolder)
                anchors.Add((nodes[i].ItemId, i));
        }
        anchors.Sort((a, b) => a.jobId.CompareTo(b.jobId));

        // Compute target X for each singleton
        var targetX = new double[n];
        foreach (int sIdx in singletons)
        {
            if (nodes[sIdx].IsFolder)
            {
                targetX[sIdx] = nodes[sIdx].X;
                continue;
            }

            int sJobId = nodes[sIdx].ItemId;

            // Binary search: largest anchor jobId < sJobId
            int lo = 0, hi = anchors.Count - 1, anchorIdx = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (anchors[mid].jobId < sJobId)
                {
                    anchorIdx = mid;
                    lo = mid + 1;
                }
                else
                    hi = mid - 1;
            }

            if (anchorIdx >= 0)
            {
                var anchor = nodes[anchors[anchorIdx].nodeIndex];
                targetX[sIdx] = anchor.X + anchor.Width / 2 - nodes[sIdx].Width / 2;
            }
            else
                targetX[sIdx] = nodes[sIdx].X;
        }

        // Sort by target X, sweep to de-overlap horizontally
        var sortedSingletons = singletons.OrderBy(idx => targetX[idx]).ToList();
        double sweepRight = double.NegativeInfinity;
        var finalX = new double[n];
        foreach (int sIdx in sortedSingletons)
        {
            double tx = targetX[sIdx];
            if (tx < sweepRight + nodeSpacing)
                tx = sweepRight + nodeSpacing;
            finalX[sIdx] = tx;
            sweepRight = tx + nodes[sIdx].Width;
        }

        // Place singletons at Y = 0
        double singletonBandBottom = 0;
        foreach (int sIdx in singletons)
        {
            nodes[sIdx].X = finalX[sIdx];
            nodes[sIdx].Y = 0;
            singletonBandBottom = Math.Max(singletonBandBottom, nodes[sIdx].Height);
        }

        // Shift all multi-node components down below the singleton band
        if (multiNodeComponents.Count > 0)
        {
            double multiNodeTop = double.MaxValue;
            for (int i = 0; i < n; i++)
                if (!singletonSet.Contains(i))
                    multiNodeTop = Math.Min(multiNodeTop, nodes[i].Y);

            foreach (var edge in edges)
            {
                multiNodeTop = Math.Min(multiNodeTop, Math.Min(edge.SourceY, edge.TargetY));
                if (edge.BendPoints is { Count: > 0 })
                    foreach (var bp in edge.BendPoints)
                        multiNodeTop = Math.Min(multiNodeTop, bp.Y);
            }

            double desiredTop = singletonBandBottom + singletonGap;
            double dy = desiredTop - multiNodeTop;

            if (Math.Abs(dy) > 0.5)
            {
                // Shift all non-singleton nodes and all edges
                var allNodeIndices = new List<int>();
                for (int i = 0; i < n; i++)
                    if (!singletonSet.Contains(i))
                        allNodeIndices.Add(i);

                // All edges belong to multi-node components (singletons have none)
                var allEdgeIndices = new List<int>();
                for (int i = 0; i < edges.Length; i++)
                    allEdgeIndices.Add(i);

                ShiftByIndices(nodes, edges, allNodeIndices, allEdgeIndices, dy);
            }
        }
    }

    private static void ShiftByIndices(
        NodeRect[] nodes, EdgeCoords[] edges,
        List<int> nodeIndices, List<int> edgeIndices,
        double dy)
    {
        foreach (int idx in nodeIndices)
            nodes[idx].Y += dy;

        foreach (int ei in edgeIndices)
        {
            edges[ei].SourceY += dy;
            edges[ei].TargetY += dy;

            if (edges[ei].BendPoints is { Count: > 0 })
                edges[ei].BendPoints = edges[ei].BendPoints.Select(bp => (bp.X, bp.Y + dy)).ToList();
        }
    }
}
