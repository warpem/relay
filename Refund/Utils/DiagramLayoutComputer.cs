using System.Security.Cryptography;
using System.Text;
using ElkSharp.Public;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Refund.Utils;

public static class DiagramLayoutComputer
{
    public static DiagramLayout ComputeLayout(View view, Space space, DiagramLayout? previous)
    {
        return ComputeLayoutCore(view.RootItems.ToList(), space, previous);
    }

    public static DiagramLayout ComputeLayout(Folder folder, Space space, DiagramLayout? previous)
    {
        return ComputeLayoutCore(folder.Items.ToList(), space, previous);
    }

    public static DiagramLayout ComputeLayout(FactoryInstance instance, Space space, DiagramLayout? previous)
    {
        return ComputeLayoutCore(instance.SubJobs.Cast<IFolderContent>().ToList(), space, previous);
    }

    private static DiagramLayout ComputeLayoutCore(
        List<IFolderContent> directItems,
        Space space,
        DiagramLayout? previous)
    {
        if (directItems.Count == 0)
            return new DiagramLayout { ConnectivityHash = "" };

        // 1. Separate items into jobs, folders, and factory instances; build lookup maps
        var jobs = new List<(int index, Job job)>();
        var folders = new List<(int index, Folder folder)>();
        var factoryInstances = new List<(int index, FactoryInstance fi)>();
        var directJobIds = new HashSet<int>(); // only direct jobs, not inside sub-folders
        var subJobToFactory = new Dictionary<int, (int index, FactoryInstance fi)>();
        var indexToKey = new Dictionary<int, string>();

        for (int i = 0; i < directItems.Count; i++)
        {
            var item = directItems[i];
            if (item is Job job)
            {
                jobs.Add((i, job));
                directJobIds.Add(job.Id);
                indexToKey[i] = $"J{job.Id}";
            }
            else if (item is Folder folder)
            {
                folders.Add((i, folder));
                indexToKey[i] = $"F{folder.Id}";
            }
            else if (item is FactoryInstance fi)
            {
                factoryInstances.Add((i, fi));
                indexToKey[i] = $"FI{fi.Id}";
                foreach (var sjId in fi.SubJobIds)
                    subJobToFactory[sjId] = (i, fi);
            }
        }

        // 2. Collect edges, remapping sub-job ports to factory instance exposed ports
        // Track remapping info for each relevant edge so we can compute port positions later
        var relevantEdges = new List<Edge>();

        // For remapped edges, track whether source/target is a factory and which exposed port index
        var edgeSourceRemap = new List<(bool isFactory, int fiId, int portIndex)?>(); // parallel to relevantEdges
        var edgeTargetRemap = new List<(bool isFactory, int fiId, int portIndex)?>(); // parallel to relevantEdges

        foreach (var edge in space.Edges)
        {
            int srcJobId = edge.Source.Job.Id;
            int tgtJobId = edge.Target.Job.Id;

            bool srcIsDirect = directJobIds.Contains(srcJobId);
            bool tgtIsDirect = directJobIds.Contains(tgtJobId);
            bool srcIsSubJob = subJobToFactory.ContainsKey(srcJobId);
            bool tgtIsSubJob = subJobToFactory.ContainsKey(tgtJobId);

            // Case 1: direct job -> direct job (normal)
            if (srcIsDirect && tgtIsDirect)
            {
                relevantEdges.Add(edge);
                edgeSourceRemap.Add(null);
                edgeTargetRemap.Add(null);
                continue;
            }

            // Case 2: sub-job -> sub-job (internal or cross-factory, skip both)
            if (srcIsSubJob && tgtIsSubJob)
                continue;

            // Case 3: direct job -> sub-job of factory (remap target)
            if (srcIsDirect && tgtIsSubJob)
            {
                var (fiIndex, fi) = subJobToFactory[tgtJobId];
                int portIndex = FindExposedInputPortIndex(fi, tgtJobId, edge.Target.Name);
                if (portIndex < 0) continue; // no matching exposed port, skip

                relevantEdges.Add(edge);
                edgeSourceRemap.Add(null);
                edgeTargetRemap.Add((true, fi.Id, portIndex));
                continue;
            }

            // Case 4: sub-job of factory -> direct job (remap source)
            if (srcIsSubJob && tgtIsDirect)
            {
                var (fiIndex, fi) = subJobToFactory[srcJobId];
                int portIndex = FindExposedOutputPortIndex(fi, srcJobId, edge.Source.Name);
                if (portIndex < 0) continue; // no matching exposed port, skip

                relevantEdges.Add(edge);
                edgeSourceRemap.Add((true, fi.Id, portIndex));
                edgeTargetRemap.Add(null);
                continue;
            }

            // Case 5: sub-job of factory -> sub-job of different factory (remap both)
            // This shouldn't normally exist, but handle gracefully by skipping
        }

        // 3. Compute connectivity hash
        var hash = ComputeConnectivityHash(directItems, jobs, folders, factoryInstances, relevantEdges);
        if (previous != null && previous.ConnectivityHash == hash)
            return previous;

        // 4. Compute node dimensions
        double maxWidth = VisualProvider.GetWidth(2); // 2x1 card = 308px
        double folderWidth = VisualProvider.GetWidth(1);
        double folderHeight = VisualProvider.GetHeight(1);

        var nodeDimensions = new Dictionary<int, (double width, double height)>();
        foreach (var (index, job) in jobs)
        {
            double originalWidth = VisualProvider.GetWidth(job.CardSquareCount.X);
            double originalHeight = VisualProvider.GetHeight(job.CardSquareCount.Y);

            if (originalWidth > maxWidth)
            {
                double scaleFactor = maxWidth / originalWidth;
                double contentHeight = VisualProvider.JabCardContentSquareSideLength * job.CardSquareCount.Y;
                double diagramHeight = VisualProvider.JobCardHeaderHeight +
                                       (contentHeight * scaleFactor) +
                                       VisualProvider.JobCardFooterHeight;
                nodeDimensions[index] = (maxWidth, diagramHeight);
            }
            else
            {
                nodeDimensions[index] = (originalWidth, originalHeight);
            }
        }

        foreach (var (index, _) in folders)
        {
            nodeDimensions[index] = (folderWidth, folderHeight);
        }

        foreach (var (index, fi) in factoryInstances)
        {
            var def = fi.Definition;
            int portsIn = def?.ExposedPortsIn.Count ?? 0;
            int portsOut = def?.ExposedPortsOut.Count ?? 0;
            int maxPorts = Math.Max(portsIn, portsOut);
            int heightSquares = maxPorts <= 6 ? 1 : (int)Math.Ceiling(maxPorts / 6.0);
            nodeDimensions[index] = (VisualProvider.GetWidth(1), VisualProvider.GetHeight(heightSquares));
        }

        // 5. Build ELK graph
        var graph = new LayoutGraph();
        graph.Options.Direction = LayoutDirection.Right;
        graph.Options.EdgeRouting = EdgeRoutingStyle.Polyline;
        graph.Options.NodeSpacing = 35;
        graph.Options.LayerSpacing = 60;
        graph.Options.Padding = 25;
        graph.Options.Thoroughness = 10;

        var layoutNodes = new Dictionary<int, LayoutNode>();

        // Create nodes
        for (int i = 0; i < directItems.Count; i++)
        {
            var (w, h) = nodeDimensions[i];
            layoutNodes[i] = graph.AddNode(w, h);
        }

        // Create ports for jobs (folders get no ports)
        // Map: (id, direction, portName) -> LayoutPort for edge wiring
        // For jobs: id = job.Id; for factory instances: id = -fi.Id (negated to avoid collision)
        // Direction is needed because input and output ports can share the same name
        var portMap = new Dictionary<(int id, bool isOutput, string portName), LayoutPort>();

        foreach (var (index, job) in jobs)
        {
            var node = layoutNodes[index];

            // Input ports (West side)
            foreach (var portIn in job.PortsIn.Values)
            {
                var elkPort = node.AddPort(PortSideHint.West);
                portMap[(job.Id, false, portIn.Name)] = elkPort;
            }

            // Output ports (East side)
            foreach (var portOut in job.PortsOut.Values)
            {
                var elkPort = node.AddPort(PortSideHint.East);
                portMap[(job.Id, true, portOut.Name)] = elkPort;
            }
        }

        // Create ports for factory instances using exposed ports
        foreach (var (index, fi) in factoryInstances)
        {
            var node = layoutNodes[index];
            var def = fi.Definition;

            if (def != null)
            {
                // Input ports (West side)
                for (int portIndex = 0; portIndex < def.ExposedPortsIn.Count; portIndex++)
                {
                    var elkPort = node.AddPort(PortSideHint.West);
                    portMap[(-fi.Id, false, $"FI_IN_{portIndex}")] = elkPort;
                }

                // Output ports (East side)
                for (int portIndex = 0; portIndex < def.ExposedPortsOut.Count; portIndex++)
                {
                    var elkPort = node.AddPort(PortSideHint.East);
                    portMap[(-fi.Id, true, $"FI_OUT_{portIndex}")] = elkPort;
                }
            }
        }

        // Create edges connecting specific ELK ports
        for (int i = 0; i < relevantEdges.Count; i++)
        {
            var edge = relevantEdges[i];
            var srcRemap = edgeSourceRemap[i];
            var tgtRemap = edgeTargetRemap[i];

            // Determine source port key
            (int id, bool isOutput, string portName) sourceKey;
            if (srcRemap != null)
                sourceKey = (-srcRemap.Value.fiId, true, $"FI_OUT_{srcRemap.Value.portIndex}");
            else
                sourceKey = (edge.Source.Job.Id, true, edge.Source.Name);

            // Determine target port key
            (int id, bool isOutput, string portName) targetKey;
            if (tgtRemap != null)
                targetKey = (-tgtRemap.Value.fiId, false, $"FI_IN_{tgtRemap.Value.portIndex}");
            else
                targetKey = (edge.Target.Job.Id, false, edge.Target.Name);

            if (portMap.TryGetValue(sourceKey, out var sourcePort) &&
                portMap.TryGetValue(targetKey, out var targetPort))
            {
                graph.AddEdge(sourcePort, targetPort);
            }
        }

        // 6. Incremental layout: use previous positions as hints
        if (previous is { Nodes.Count: > 0 })
        {
            var prevPositions = new Dictionary<string, DiagramLayoutNode>();
            foreach (var pn in previous.Nodes)
            {
                var key = pn.IsFactoryInstance ? $"FI{pn.ItemId}" : (pn.IsFolder ? $"F{pn.ItemId}" : $"J{pn.ItemId}");
                prevPositions[key] = pn;
            }

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

        // 7. Run layout
        LayeredLayoutEngine.Layout(graph);
        if (graph.Options.Interactive)
            LayeredLayoutEngine.RelaxLayout(graph, strainThreshold: 3.0, maxIterations: 2);

        // 8. Extract results
        var result = new DiagramLayout
        {
            GraphWidth = graph.Width,
            GraphHeight = graph.Height,
            ConnectivityHash = hash
        };

        // Build nodes
        for (int i = 0; i < directItems.Count; i++)
        {
            var item = directItems[i];
            var node = layoutNodes[i];
            bool isFolder = item is Folder;
            bool isFactoryInstance = item is FactoryInstance;

            int itemId;
            if (item is Job j) itemId = j.Id;
            else if (item is Folder f) itemId = f.Id;
            else if (item is FactoryInstance fiItem) itemId = fiItem.Id;
            else continue;

            result.Nodes.Add(new DiagramLayoutNode
            {
                ItemId = itemId,
                IsFolder = isFolder,
                IsFactoryInstance = isFactoryInstance,
                X = node.X,
                Y = node.Y,
                Width = node.Width,
                Height = node.Height
            });
        }

        // Build edges with port metadata
        // Use actual rendered port positions (matching CSS layout) instead of ELK-computed positions,
        // because ELK port placement doesn't match our card rendering.
        // Build lookup: jobId -> (node, job, nodeDimensions)
        var jobLookup = new Dictionary<int, (LayoutNode node, Job job, double width, double height)>();
        foreach (var (index, job) in jobs)
            jobLookup[job.Id] = (layoutNodes[index], job, nodeDimensions[index].width, nodeDimensions[index].height);

        var fiLookup = new Dictionary<int, (LayoutNode node, FactoryInstance fi, double width, double height)>();
        foreach (var (index, fi) in factoryInstances)
            fiLookup[fi.Id] = (layoutNodes[index], fi, nodeDimensions[index].width, nodeDimensions[index].height);

        var elkEdges = graph.Edges.ToList();
        for (int i = 0; i < relevantEdges.Count && i < elkEdges.Count; i++)
        {
            var originalEdge = relevantEdges[i];
            var elkEdge = elkEdges[i];
            var srcRemap = edgeSourceRemap[i];
            var tgtRemap = edgeTargetRemap[i];

            // Compute source position
            double srcX, srcY;
            if (srcRemap != null)
            {
                var srcFiInfo = fiLookup[srcRemap.Value.fiId];
                var srcPos = GetFactoryPortPosition(srcFiInfo.node, srcRemap.Value.portIndex, isOutput: true);
                srcX = srcPos.X;
                srcY = srcPos.Y;
            }
            else
            {
                var srcInfo = jobLookup[originalEdge.Source.Job.Id];
                var srcPos = GetOutputPortPosition(srcInfo.node, srcInfo.job, originalEdge.Source.Name);
                srcX = srcPos.X;
                srcY = srcPos.Y;
            }

            // Compute target position
            double tgtX, tgtY;
            if (tgtRemap != null)
            {
                var tgtFiInfo = fiLookup[tgtRemap.Value.fiId];
                var tgtPos = GetFactoryPortPosition(tgtFiInfo.node, tgtRemap.Value.portIndex, isOutput: false);
                tgtX = tgtPos.X;
                tgtY = tgtPos.Y;
            }
            else
            {
                var tgtInfo = jobLookup[originalEdge.Target.Job.Id];
                var tgtPos = GetInputPortPosition(tgtInfo.node, tgtInfo.height, tgtInfo.job, originalEdge.Target.Name);
                tgtX = tgtPos.X;
                tgtY = tgtPos.Y;
            }

            // Determine display IDs and port names for the edge
            // For remapped edges, use the factory instance ID (negated) and exposed port name
            int sourceDisplayId = srcRemap != null ? -srcRemap.Value.fiId : originalEdge.Source.Job.Id;
            string sourcePortName = srcRemap != null ? $"FI_OUT_{srcRemap.Value.portIndex}" : originalEdge.Source.Name;
            int targetDisplayId = tgtRemap != null ? -tgtRemap.Value.fiId : originalEdge.Target.Job.Id;
            string targetPortName = tgtRemap != null ? $"FI_IN_{tgtRemap.Value.portIndex}" : originalEdge.Target.Name;

            result.Edges.Add(new DiagramLayoutEdge
            {
                SourceJobId = sourceDisplayId,
                SourcePortName = sourcePortName,
                TargetJobId = targetDisplayId,
                TargetPortName = targetPortName,
                ResourceType = originalEdge.Source.ResourceType.Name,
                SourceX = srcX,
                SourceY = srcY,
                TargetX = tgtX,
                TargetY = tgtY,
                BendPoints = elkEdge.BendPoints.ToList()
            });
        }

        // Post-process: compact disconnected components and reposition singletons
        PostProcessDisconnectedComponents(result, directItems);

        // Compute actual bounding box
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
            if (edge.BendPoints is { Count: > 0 })
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

    /// <summary>
    /// Computes the rendered position of an output port (East/right side of card).
    /// Matches CSS: .port-dots-container { right: -8px; top: 30px; gap: 6px; } with 16px port dots.
    /// </summary>
    private static (double X, double Y) GetOutputPortPosition(LayoutNode node, Job job, string portName)
    {
        int portIndex = 0;
        foreach (var port in job.PortsOut.Values)
        {
            if (port.Name == portName)
                break;
            portIndex++;
        }

        // Port dot center X: straddles the card's right edge (center at card right edge)
        double x = node.X + node.Width;
        // Port dot center Y: container starts at 30px from top, each dot is 16px with 6px gap
        double y = node.Y + 38 + portIndex * 22;
        return (x, y);
    }

    /// <summary>
    /// Computes the rendered position of an input port (West/left side of card, diagram mode).
    /// Matches CSS: left: -6px; top: 30px; gap: 6px; with 16px port dots (same as output ports).
    /// </summary>
    private static (double X, double Y) GetInputPortPosition(LayoutNode node, double nodeHeight, Job job, string portName)
    {
        int portIndex = 0;
        foreach (var port in job.PortsIn.Values)
        {
            if (port.Name == portName)
                break;
            portIndex++;
        }

        // Port dot center X: container at left: -6px, dots are 16px wide, center at 2px from card left
        double x = node.X + 2;
        // Same layout as output ports: top: 30px, 16px dots with 6px gap = 22px per slot
        double y = node.Y + 38 + portIndex * 22;
        return (x, y);
    }

    /// <summary>
    /// Computes the rendered position of a factory instance exposed port.
    /// Uses the same 38px top offset + 22px per slot formula as job ports.
    /// </summary>
    private static (double X, double Y) GetFactoryPortPosition(LayoutNode node, int portIndex, bool isOutput)
    {
        double x = isOutput ? node.X + node.Width : node.X + 2;
        double y = node.Y + 38 + portIndex * 22;
        return (x, y);
    }

    /// <summary>
    /// Finds the blueprint ID (1-based) for a real sub-job ID within a factory instance.
    /// Returns -1 if the real job ID is not found in the factory's SubJobIds.
    /// </summary>
    private static int FindBlueprintId(FactoryInstance fi, int realJobId)
    {
        int index = fi.SubJobIds.IndexOf(realJobId);
        return index >= 0 ? index + 1 : -1;
    }

    /// <summary>
    /// Finds the index of an exposed input port matching a sub-job's port.
    /// Returns -1 if no matching exposed port is found.
    /// </summary>
    private static int FindExposedInputPortIndex(FactoryInstance fi, int realJobId, string portName)
    {
        var def = fi.Definition;
        if (def == null) return -1;

        int blueprintId = FindBlueprintId(fi, realJobId);
        if (blueprintId < 0) return -1;

        for (int i = 0; i < def.ExposedPortsIn.Count; i++)
        {
            var ep = def.ExposedPortsIn[i];
            if (ep.SubJobId == blueprintId && ep.PortName == portName)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Finds the index of an exposed output port matching a sub-job's port.
    /// Returns -1 if no matching exposed port is found.
    /// </summary>
    private static int FindExposedOutputPortIndex(FactoryInstance fi, int realJobId, string portName)
    {
        var def = fi.Definition;
        if (def == null) return -1;

        int blueprintId = FindBlueprintId(fi, realJobId);
        if (blueprintId < 0) return -1;

        for (int i = 0; i < def.ExposedPortsOut.Count; i++)
        {
            var ep = def.ExposedPortsOut[i];
            if (ep.SubJobId == blueprintId && ep.PortName == portName)
                return i;
        }
        return -1;
    }

    private static void PostProcessDisconnectedComponents(DiagramLayout result, List<IFolderContent> directItems)
    {
        int n = result.Nodes.Count;
        if (n == 0) return;

        // Build jobId -> node index lookup (only for direct jobs, not folders or factory instances)
        var jobIdToIndex = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            var node = result.Nodes[i];
            if (!node.IsFolder && !node.IsFactoryInstance)
                jobIdToIndex[node.ItemId] = i;
        }

        // For factory instance edges, map negated factory ID to node index
        var fiIdToIndex = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            var node = result.Nodes[i];
            if (node.IsFactoryInstance)
                fiIdToIndex[-node.ItemId] = i; // negated to match edge SourceJobId/TargetJobId convention
        }

        // Convert to mutable arrays for the shared compactor
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

        var edgeList = new List<DisconnectedComponentCompactor.EdgeCoords>();
        var edgeIndices = new List<int>(); // track which result.Edges indices are included
        for (int i = 0; i < result.Edges.Count; i++)
        {
            var e = result.Edges[i];

            // Resolve node index for source: try job lookup first, then factory instance lookup
            int srcIdx = -1;
            if (!jobIdToIndex.TryGetValue(e.SourceJobId, out srcIdx))
                if (!fiIdToIndex.TryGetValue(e.SourceJobId, out srcIdx))
                    srcIdx = -1;

            // Resolve node index for target
            int tgtIdx = -1;
            if (!jobIdToIndex.TryGetValue(e.TargetJobId, out tgtIdx))
                if (!fiIdToIndex.TryGetValue(e.TargetJobId, out tgtIdx))
                    tgtIdx = -1;

            if (srcIdx < 0 || tgtIdx < 0) continue; // skip unresolvable edges

            edgeIndices.Add(i);
            edgeList.Add(new DisconnectedComponentCompactor.EdgeCoords
            {
                SourceNodeIndex = srcIdx, TargetNodeIndex = tgtIdx,
                SourceX = e.SourceX, SourceY = e.SourceY,
                TargetX = e.TargetX, TargetY = e.TargetY,
                BendPoints = e.BendPoints?.ToList()
            });
        }
        var edges = edgeList.ToArray();

        DisconnectedComponentCompactor.Compact(nodes, edges, nodeSpacing: 35, singletonGap: 70);

        // Copy results back, preserving IsFactoryInstance
        for (int i = 0; i < n; i++)
        {
            var origNode = result.Nodes[i];
            result.Nodes[i] = new DiagramLayoutNode
            {
                ItemId = nodes[i].ItemId, IsFolder = nodes[i].IsFolder,
                IsFactoryInstance = origNode.IsFactoryInstance,
                X = nodes[i].X, Y = nodes[i].Y,
                Width = nodes[i].Width, Height = nodes[i].Height
            };
        }

        for (int ei = 0; ei < edgeIndices.Count; ei++)
        {
            int ri = edgeIndices[ei];
            var orig = result.Edges[ri];
            result.Edges[ri] = new DiagramLayoutEdge
            {
                SourceJobId = orig.SourceJobId, SourcePortName = orig.SourcePortName,
                TargetJobId = orig.TargetJobId, TargetPortName = orig.TargetPortName,
                ResourceType = orig.ResourceType,
                SourceX = edges[ei].SourceX, SourceY = edges[ei].SourceY,
                TargetX = edges[ei].TargetX, TargetY = edges[ei].TargetY,
                BendPoints = edges[ei].BendPoints
            };
        }
    }

    private static string ComputeConnectivityHash(
        List<IFolderContent> directItems,
        List<(int index, Job job)> jobs,
        List<(int index, Folder folder)> folders,
        List<(int index, FactoryInstance fi)> factoryInstances,
        List<Edge> edges)
    {
        // Build sorted item keys
        var itemKeys = new List<string>();
        foreach (var (_, job) in jobs)
            itemKeys.Add($"J{job.Id}");
        foreach (var (_, folder) in folders)
            itemKeys.Add($"F{folder.Id}");
        foreach (var (_, fi) in factoryInstances)
            itemKeys.Add($"FI{fi.Id}");
        itemKeys.Sort(StringComparer.Ordinal);

        // Build sorted edge descriptions (including port names)
        var edgeDescs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var edge in edges)
        {
            edgeDescs.Add($"J{edge.Source.Job.Id}.{edge.Source.Name}->J{edge.Target.Job.Id}.{edge.Target.Name}");
        }

        // Include card dimensions for each job and factory instance
        // (changes in dimensions should trigger re-layout)
        var dimDescs = new List<string>();
        foreach (var (_, job) in jobs)
        {
            dimDescs.Add($"J{job.Id}:{job.CardSquareCount.X}x{job.CardSquareCount.Y}");
        }
        foreach (var (_, fi) in factoryInstances)
        {
            var def = fi.Definition;
            int portsIn = def?.ExposedPortsIn.Count ?? 0;
            int portsOut = def?.ExposedPortsOut.Count ?? 0;
            dimDescs.Add($"FI{fi.Id}:{portsIn}x{portsOut}");
        }
        dimDescs.Sort(StringComparer.Ordinal);

        var raw = string.Join(",", itemKeys) +
                  "|" + string.Join(",", edgeDescs) +
                  "|" + string.Join(",", dimDescs);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }

    /// <summary>
    /// Parses a factory edge endpoint string (format: "subJobId.portName") into its components.
    /// </summary>
    /// <param name="endpoint">Endpoint string, e.g. "1.Frames" or "2.Particles".</param>
    /// <returns>A tuple of (subJobId, portName).</returns>
    /// <exception cref="FormatException">Thrown when the endpoint string is not in the expected format.</exception>
    public static (int SubJobId, string PortName) ParseFactoryEdgeEndpoint(string endpoint)
    {
        int dotIndex = endpoint.IndexOf('.');
        if (dotIndex < 0)
            throw new FormatException($"Factory edge endpoint '{endpoint}' is not in 'subJobId.portName' format.");

        string idPart = endpoint[..dotIndex];
        string portName = endpoint[(dotIndex + 1)..];

        if (!int.TryParse(idPart, out int subJobId))
            throw new FormatException($"Factory edge endpoint '{endpoint}' has non-integer sub-job ID '{idPart}'.");

        return (subJobId, portName);
    }

    /// <summary>
    /// Computes the rendered position of an output port (East/right side of card) for a ReadOnlyJob.
    /// Mirrors GetOutputPortPosition but works with ReadOnlyJob.
    /// </summary>
    private static (double X, double Y) GetOutputPortPositionReadOnly(LayoutNode node, ReadOnlyJob job, string portName)
    {
        int portIndex = 0;
        foreach (var port in job.PortsOut.Values)
        {
            if (port.Name == portName)
                break;
            portIndex++;
        }

        double x = node.X + node.Width;
        double y = node.Y + 38 + portIndex * 22;
        return (x, y);
    }

    /// <summary>
    /// Computes the rendered position of an input port (West/left side of card) for a ReadOnlyJob.
    /// Mirrors GetInputPortPosition but works with ReadOnlyJob.
    /// </summary>
    private static (double X, double Y) GetInputPortPositionReadOnly(LayoutNode node, double nodeHeight, ReadOnlyJob job, string portName)
    {
        int portIndex = 0;
        foreach (var port in job.PortsIn.Values)
        {
            if (port.Name == portName)
                break;
            portIndex++;
        }

        double x = node.X + 2;
        double y = node.Y + 38 + portIndex * 22;
        return (x, y);
    }

    /// <summary>
    /// Computes a DiagramLayout for a factory definition's sub-jobs and internal edges.
    /// Uses blueprint sub-job IDs (1-based, local to definition) as ItemId in layout nodes.
    /// Supports incremental layout from the definition's existing DiagramLayout if present.
    /// </summary>
    public static DiagramLayout ComputeLayoutForDefinition(ReadOnlyFactoryDefinition definition)
    {
        var subJobs = definition.SubJobs;
        if (subJobs.Count == 0)
            return new DiagramLayout { ConnectivityHash = "" };

        // 1. Build sub-job lookup by actual blueprint ID (stored on Job.Id)
        var subJobById = new Dictionary<int, ReadOnlyJob>();
        foreach (var subJob in subJobs)
            subJobById[subJob.Id] = subJob;

        // 2. Parse internal edges
        var parsedEdges = new List<(int srcId, string srcPort, int tgtId, string tgtPort)>();
        foreach (var ie in definition.InternalEdges)
        {
            var (srcId, srcPort) = ParseFactoryEdgeEndpoint(ie.Source);
            var (tgtId, tgtPort) = ParseFactoryEdgeEndpoint(ie.Target);
            if (subJobById.ContainsKey(srcId) && subJobById.ContainsKey(tgtId))
                parsedEdges.Add((srcId, srcPort, tgtId, tgtPort));
        }

        // 2b. Parse external edges (incoming from outside the factory)
        var parsedExternalEdges = new List<(int extJobId, string extPort, int tgtId, string tgtPort)>();
        foreach (var ext in definition.ExternalEdges)
        {
            if (subJobById.TryGetValue(ext.SubJobId, out var extSubJob) &&
                extSubJob.PortsIn.ContainsKey(ext.SubJobPort))
                parsedExternalEdges.Add((ext.ExternalJobId, ext.ExternalPort, ext.SubJobId, ext.SubJobPort));
        }
        var uniqueExternalJobIds = parsedExternalEdges.Select(e => e.extJobId).Distinct().ToList();

        // 3. Compute connectivity hash
        var hash = ComputeDefinitionConnectivityHash(subJobById, parsedEdges, parsedExternalEdges);
        var previous = definition.DiagramLayout;
        if (previous != null && previous.ConnectivityHash == hash)
            return previous;

        // 4. Compute node dimensions
        double maxWidth = VisualProvider.GetWidth(2);

        var nodeDimensions = new Dictionary<int, (double width, double height)>();
        foreach (var (blueprintId, job) in subJobById)
        {
            double originalWidth = VisualProvider.GetWidth(job.CardSquareCount.X);
            double originalHeight = VisualProvider.GetHeight(job.CardSquareCount.Y);

            if (originalWidth > maxWidth)
            {
                double scaleFactor = maxWidth / originalWidth;
                double contentHeight = VisualProvider.JabCardContentSquareSideLength * job.CardSquareCount.Y;
                double diagramHeight = VisualProvider.JobCardHeaderHeight +
                                       (contentHeight * scaleFactor) +
                                       VisualProvider.JobCardFooterHeight;
                nodeDimensions[blueprintId] = (maxWidth, diagramHeight);
            }
            else
            {
                nodeDimensions[blueprintId] = (originalWidth, originalHeight);
            }
        }

        // 5. Build ELK graph
        var graph = new LayoutGraph();
        graph.Options.Direction = LayoutDirection.Right;
        graph.Options.EdgeRouting = EdgeRoutingStyle.Polyline;
        graph.Options.NodeSpacing = 35;
        graph.Options.LayerSpacing = 60;
        graph.Options.Padding = 25;
        graph.Options.Thoroughness = 10;

        var layoutNodes = new Dictionary<int, LayoutNode>();

        // Create nodes keyed by blueprint ID
        foreach (var (blueprintId, _) in subJobById)
        {
            var (w, h) = nodeDimensions[blueprintId];
            layoutNodes[blueprintId] = graph.AddNode(w, h);
        }

        // Create ports for sub-jobs
        var portMap = new Dictionary<(int blueprintId, bool isOutput, string portName), LayoutPort>();

        foreach (var (blueprintId, job) in subJobById)
        {
            var node = layoutNodes[blueprintId];

            foreach (var portIn in job.PortsIn.Values)
            {
                var elkPort = node.AddPort(PortSideHint.West);
                portMap[(blueprintId, false, portIn.Name)] = elkPort;
            }

            foreach (var portOut in job.PortsOut.Values)
            {
                var elkPort = node.AddPort(PortSideHint.East);
                portMap[(blueprintId, true, portOut.Name)] = elkPort;
            }
        }

        // Create stub nodes for external source jobs
        const double stubNodeSize = 8;
        var externalLayoutNodes = new Dictionary<int, LayoutNode>();
        var externalPortMap = new Dictionary<(int extJobId, string portName), LayoutPort>();
        foreach (var extJobId in uniqueExternalJobIds)
            externalLayoutNodes[extJobId] = graph.AddNode(stubNodeSize, stubNodeSize);
        foreach (var (extJobId, extPort, _, _) in parsedExternalEdges)
        {
            var key = (extJobId, extPort);
            if (!externalPortMap.ContainsKey(key))
                externalPortMap[key] = externalLayoutNodes[extJobId].AddPort(PortSideHint.East);
        }

        // Create internal edges
        foreach (var (srcId, srcPort, tgtId, tgtPort) in parsedEdges)
        {
            if (portMap.TryGetValue((srcId, true, srcPort), out var sourcePort) &&
                portMap.TryGetValue((tgtId, false, tgtPort), out var targetPort))
            {
                graph.AddEdge(sourcePort, targetPort);
            }
        }

        // Create external edges
        foreach (var (extJobId, extPort, tgtId, tgtPort) in parsedExternalEdges)
        {
            if (externalPortMap.TryGetValue((extJobId, extPort), out var extSrcPort) &&
                portMap.TryGetValue((tgtId, false, tgtPort), out var extTgtPort))
            {
                graph.AddEdge(extSrcPort, extTgtPort);
            }
        }

        // 6. Incremental layout: use previous positions as hints
        if (previous is { Nodes.Count: > 0 })
        {
            var prevPositions = new Dictionary<int, DiagramLayoutNode>();
            foreach (var pn in previous.Nodes)
                prevPositions[pn.ItemId] = pn;

            bool anyHinted = false;
            foreach (var (blueprintId, _) in subJobById)
            {
                if (prevPositions.TryGetValue(blueprintId, out var prevNode))
                {
                    layoutNodes[blueprintId].X = prevNode.X;
                    layoutNodes[blueprintId].Y = prevNode.Y;
                    anyHinted = true;
                }
            }

            if (anyHinted)
                graph.Options.Interactive = true;
        }

        // 7. Run layout
        LayeredLayoutEngine.Layout(graph);
        if (graph.Options.Interactive)
            LayeredLayoutEngine.RelaxLayout(graph, strainThreshold: 3.0, maxIterations: 2);

        // 8. Extract results
        var result = new DiagramLayout
        {
            GraphWidth = graph.Width,
            GraphHeight = graph.Height,
            ConnectivityHash = hash
        };

        // Build nodes
        foreach (var (blueprintId, _) in subJobById)
        {
            var node = layoutNodes[blueprintId];
            result.Nodes.Add(new DiagramLayoutNode
            {
                ItemId = blueprintId,
                IsFolder = false,
                IsFactoryInstance = false,
                X = node.X,
                Y = node.Y,
                Width = node.Width,
                Height = node.Height
            });
        }

        // External stub nodes (for layout/edge routing — not rendered as items)
        foreach (var extJobId in uniqueExternalJobIds)
        {
            var node = externalLayoutNodes[extJobId];
            result.Nodes.Add(new DiagramLayoutNode
            {
                ItemId = -extJobId,
                IsFolder = false,
                IsFactoryInstance = false,
                X = node.X,
                Y = node.Y,
                Width = stubNodeSize,
                Height = stubNodeSize
            });
        }

        // Build edges with port metadata
        var jobLookup = new Dictionary<int, (LayoutNode node, ReadOnlyJob job, double width, double height)>();
        foreach (var (blueprintId, job) in subJobById)
            jobLookup[blueprintId] = (layoutNodes[blueprintId], job, nodeDimensions[blueprintId].width, nodeDimensions[blueprintId].height);

        var elkEdges = graph.Edges.ToList();
        for (int i = 0; i < parsedEdges.Count && i < elkEdges.Count; i++)
        {
            var (srcId, srcPort, tgtId, tgtPort) = parsedEdges[i];
            var elkEdge = elkEdges[i];

            var srcInfo = jobLookup[srcId];
            var srcPos = GetOutputPortPositionReadOnly(srcInfo.node, srcInfo.job, srcPort);

            var tgtInfo = jobLookup[tgtId];
            var tgtPos = GetInputPortPositionReadOnly(tgtInfo.node, tgtInfo.height, tgtInfo.job, tgtPort);

            // Determine resource type from source port if available
            string resourceType = "";
            if (srcInfo.job.PortsOut.TryGetValue(srcPort, out var srcPortOut))
                resourceType = srcPortOut.ResourceType?.Name ?? "";

            result.Edges.Add(new DiagramLayoutEdge
            {
                SourceJobId = srcId,
                SourcePortName = srcPort,
                TargetJobId = tgtId,
                TargetPortName = tgtPort,
                ResourceType = resourceType,
                SourceX = srcPos.X,
                SourceY = srcPos.Y,
                TargetX = tgtPos.X,
                TargetY = tgtPos.Y,
                BendPoints = elkEdge.BendPoints.ToList()
            });
        }

        // Build external edge layout entries
        int externalEdgeOffset = parsedEdges.Count;
        for (int i = 0; i < parsedExternalEdges.Count; i++)
        {
            int elkIdx = externalEdgeOffset + i;
            if (elkIdx >= elkEdges.Count) break;

            var (extJobId, extPort, tgtId, tgtPort) = parsedExternalEdges[i];
            var elkEdge = elkEdges[elkIdx];

            // Source: right-center of stub node
            var extNode = externalLayoutNodes[extJobId];
            double srcX = extNode.X + stubNodeSize;
            double srcY = extNode.Y + stubNodeSize / 2.0;

            // Target: input port position on the sub-job
            var tgtInfo = jobLookup[tgtId];
            var tgtPos = GetInputPortPositionReadOnly(tgtInfo.node, tgtInfo.height, tgtInfo.job, tgtPort);

            // Resource type from target port
            string resourceType = "";
            if (tgtInfo.job.PortsIn.TryGetValue(tgtPort, out var tgtPortIn))
                resourceType = tgtPortIn.ResourceType?.Name ?? "";

            result.Edges.Add(new DiagramLayoutEdge
            {
                SourceJobId = -extJobId,
                SourcePortName = extPort,
                TargetJobId = tgtId,
                TargetPortName = tgtPort,
                ResourceType = resourceType,
                SourceX = srcX,
                SourceY = srcY,
                TargetX = tgtPos.X,
                TargetY = tgtPos.Y,
                BendPoints = elkEdge.BendPoints.ToList()
            });
        }

        // Post-process: compact disconnected components
        PostProcessDefinitionComponents(result);

        // Compute actual bounding box
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
            if (edge.BendPoints is { Count: > 0 })
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

    /// <summary>
    /// Post-processes disconnected components for a factory definition layout.
    /// Uses the same compactor as the main layout but with blueprint IDs as job lookups.
    /// </summary>
    private static void PostProcessDefinitionComponents(DiagramLayout result)
    {
        int n = result.Nodes.Count;
        if (n == 0) return;

        var jobIdToIndex = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
            jobIdToIndex[result.Nodes[i].ItemId] = i;

        var nodes = new DisconnectedComponentCompactor.NodeRect[n];
        for (int i = 0; i < n; i++)
        {
            var nd = result.Nodes[i];
            nodes[i] = new DisconnectedComponentCompactor.NodeRect
            {
                X = nd.X, Y = nd.Y, Width = nd.Width, Height = nd.Height,
                ItemId = nd.ItemId, IsFolder = false
            };
        }

        var edges = new DisconnectedComponentCompactor.EdgeCoords[result.Edges.Count];
        for (int i = 0; i < result.Edges.Count; i++)
        {
            var e = result.Edges[i];
            jobIdToIndex.TryGetValue(e.SourceJobId, out int srcIdx);
            jobIdToIndex.TryGetValue(e.TargetJobId, out int tgtIdx);

            edges[i] = new DisconnectedComponentCompactor.EdgeCoords
            {
                SourceNodeIndex = srcIdx, TargetNodeIndex = tgtIdx,
                SourceX = e.SourceX, SourceY = e.SourceY,
                TargetX = e.TargetX, TargetY = e.TargetY,
                BendPoints = e.BendPoints?.ToList()
            };
        }

        DisconnectedComponentCompactor.Compact(nodes, edges, nodeSpacing: 35, singletonGap: 70);

        for (int i = 0; i < n; i++)
        {
            result.Nodes[i] = new DiagramLayoutNode
            {
                ItemId = nodes[i].ItemId, IsFolder = false,
                IsFactoryInstance = false,
                X = nodes[i].X, Y = nodes[i].Y,
                Width = nodes[i].Width, Height = nodes[i].Height
            };
        }

        for (int i = 0; i < result.Edges.Count; i++)
        {
            var orig = result.Edges[i];
            result.Edges[i] = new DiagramLayoutEdge
            {
                SourceJobId = orig.SourceJobId, SourcePortName = orig.SourcePortName,
                TargetJobId = orig.TargetJobId, TargetPortName = orig.TargetPortName,
                ResourceType = orig.ResourceType,
                SourceX = edges[i].SourceX, SourceY = edges[i].SourceY,
                TargetX = edges[i].TargetX, TargetY = edges[i].TargetY,
                BendPoints = edges[i].BendPoints
            };
        }
    }

    /// <summary>
    /// Computes a connectivity hash for factory definition layout cache invalidation.
    /// </summary>
    private static string ComputeDefinitionConnectivityHash(
        Dictionary<int, ReadOnlyJob> subJobById,
        List<(int srcId, string srcPort, int tgtId, string tgtPort)> parsedEdges,
        List<(int extJobId, string extPort, int tgtId, string tgtPort)> parsedExternalEdges)
    {
        var itemKeys = subJobById.Keys.Select(id => $"SJ{id}").OrderBy(k => k, StringComparer.Ordinal).ToList();

        var edgeDescs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (srcId, srcPort, tgtId, tgtPort) in parsedEdges)
            edgeDescs.Add($"SJ{srcId}.{srcPort}->SJ{tgtId}.{tgtPort}");
        foreach (var (extJobId, extPort, tgtId, tgtPort) in parsedExternalEdges)
            edgeDescs.Add($"EXT{extJobId}.{extPort}->SJ{tgtId}.{tgtPort}");

        var dimDescs = subJobById
            .Select(kvp => $"SJ{kvp.Key}:{kvp.Value.CardSquareCount.X}x{kvp.Value.CardSquareCount.Y}")
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();

        var raw = string.Join(",", itemKeys) +
                  "|" + string.Join(",", edgeDescs) +
                  "|" + string.Join(",", dimDescs);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }
}
