using Refund.DataModel;
using Refund.Jobs.Refinement.Masks.CreateMask;
using Refund.Utils;

namespace Refund.Tests.Utils;

[Collection("JobRegistry")]
public sealed class LayoutConnectivityTests
{
    [Theory]
    [InlineData("folder-card")]
    [InlineData("folder-diagram")]
    [InlineData("view")]
    [InlineData("factory-instance")]
    [InlineData("factory-definition")]
    [InlineData("factory-card")]
    public void RewiringExistingNodesRecomputesTheirDependencyOrder(string surface)
    {
        JobRegistry.EnsurePopulated();
        // Only topology is relevant here; no resources are resolved or executed.
        var space = new Space();
        var view = space.CreateView(null);
        var folder = new Folder();
        var jobs = Enumerable.Range(1, 3).Select(id => new CreateMask { Id = id }).ToArray();
        foreach (var job in jobs)
        {
            space.AddJob(job, view);
            folder.AddItem(job);
        }
        var instance = new FactoryInstance { Space = space, SubJobIds = [1, 2, 3] };
        var definition = new FactoryDefinition { SubJobs = jobs.Cast<Job>().ToList() };
        DiagramLayout? diagram = null;
        FolderLayout? card = null;

        void Connect(int source, int target)
        {
            space.CreateEdge(jobs[source - 1].PortsOut[CreateMask.PortOutMask],
                jobs[target - 1].PortsIn[CreateMask.PortInMap]);
            definition.InternalEdges.Add(new FactoryEdge(
                $"{source}.{CreateMask.PortOutMask}", $"{target}.{CreateMask.PortInMap}"));
        }

        Dictionary<int, double> Layout()
        {
            switch (surface)
            {
                case "folder-card": card = FolderLayoutComputer.ComputeLayout(folder, space, card); break;
                case "factory-card": card = FolderLayoutComputer.ComputeCardLayoutForDefinition(definition.AsReadOnly(), card); break;
                case "folder-diagram": diagram = DiagramLayoutComputer.ComputeLayout(folder, space, diagram); break;
                case "view": diagram = DiagramLayoutComputer.ComputeLayout(view, space, diagram); break;
                case "factory-instance": diagram = DiagramLayoutComputer.ComputeLayout(instance, space, diagram); break;
                case "factory-definition":
                    diagram = definition.DiagramLayout = DiagramLayoutComputer.ComputeLayoutForDefinition(definition.AsReadOnly());
                    break;
            }
            return card != null
                ? card.Nodes.ToDictionary(node => node.ItemId, node => node.X)
                : diagram!.Nodes.ToDictionary(node => node.ItemId, node => node.X);
        }

        Connect(1, 2);
        Connect(2, 3);
        var before = Layout();
        Assert.True(before[1] < before[2] && before[2] < before[3]);
        foreach (var edge in space.Edges.ToArray())
            space.DeleteEdge(edge);
        definition.InternalEdges.Clear();
        Connect(3, 2);
        Connect(2, 1);

        var after = Layout();
        Assert.True(after[3] < after[2] && after[2] < after[1],
            $"Expected 3 -> 2 -> 1 from left to right, got X: {after[3]}, {after[2]}, {after[1]}");
        var cached = (object?)card ?? diagram;
        Layout();
        Assert.Same(cached, (object?)card ?? diagram);
    }
}
