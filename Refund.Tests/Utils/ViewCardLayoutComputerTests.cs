using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.Jobs.Refinement.Masks.CreateMask;
using Refund.Utils;

namespace Refund.Tests.Utils;

[Collection("JobRegistry")]
public sealed class ViewCardLayoutComputerTests
{
    // Only topology is relevant here; no resources are resolved or executed.
    private sealed class Graph
    {
        public readonly Space Space = new();
        public readonly View View;
        public readonly CreateMask[] Jobs;

        public Graph(int jobCount)
        {
            JobRegistry.EnsurePopulated();
            View = Space.CreateView(null);
            Jobs = Enumerable.Range(1, jobCount).Select(id => new CreateMask { Id = id }).ToArray();
            foreach (var job in Jobs)
                Space.AddJob(job, View);
        }

        public Edge Connect(int source, int target) =>
            Space.CreateEdge(Jobs[source - 1].PortsOut[CreateMask.PortOutMask],
                Jobs[target - 1].PortsIn[CreateMask.PortInMap]);

        public FolderLayout Update()
        {
            View.UpdateCardLayout(Space);
            return View.CardLayout!;
        }
    }

    [Fact]
    public void FlattensNestedFoldersAndFactoryJobsIntoDistinctJobNodes()
    {
        var g = new Graph(6);
        // Folder and factory IDs deliberately collide with job IDs.
        var outer = new Folder { Id = 1 };
        var inner = new Folder { Id = 2 };
        g.View.AddFolder(outer);
        g.View.AddFolder(inner, outer);
        g.View.MoveJobToFolder(g.Jobs[1], outer);
        g.View.MoveJobToFolder(g.Jobs[2], inner);
        var instance = new FactoryInstance { Id = 1, Space = g.Space, SubJobIds = [4, 5] };
        g.View.AddFactoryInstance(instance, inner);
        g.View.RemoveJobFromRootItems(g.Jobs[3]);
        g.View.RemoveJobFromRootItems(g.Jobs[4]);
        g.View.RemoveJob(g.Jobs[5]); // in the space, but not in this view

        var layout = g.Update();

        Assert.Equal([1, 2, 3, 4, 5], layout.Nodes.Select(node => node.ItemId).Order());
        Assert.All(layout.Nodes, node =>
        {
            Assert.False(node.IsFolder);
            Assert.Equal((10.0, 10.0), (node.Width, node.Height));
        });
    }

    [Fact]
    public void DrawsOnlyInternalJobEdgesOncePerJobPair()
    {
        var g = new Graph(4);
        g.Connect(1, 2);
        g.Connect(1, 2); // parallel edge between the same jobs
        g.Connect(2, 3);
        g.Connect(3, 4);
        g.View.RemoveJob(g.Jobs[3]); // 3 -> 4 now leaves the view

        var layout = g.Update();

        Assert.Equal(3, layout.Nodes.Count);
        Assert.Equal(2, layout.Edges.Count);
        var x = layout.Nodes.ToDictionary(node => node.ItemId, node => node.X);
        Assert.True(x[1] < x[2] && x[2] < x[3]);
    }

    [Fact]
    public void StatusAndFolderPlacementChangesKeepTheCachedLayout()
    {
        var g = new Graph(3);
        g.Connect(1, 2);
        var original = g.Update();
        var coordinates = original.Nodes.Select(node => (node.ItemId, node.X, node.Y)).ToList();

        foreach (var job in g.Jobs)
            job.Status = JobStatus.Finished;
        Assert.Same(original, g.Update());

        // The card is flat, so regrouping jobs inside the view leaves it untouched.
        var folder = new Folder { Id = 1 };
        g.View.AddFolder(folder);
        g.View.MoveJobToFolder(g.Jobs[1], folder);
        Assert.Same(original, g.Update());
        Assert.Equal(coordinates, original.Nodes.Select(node => (node.ItemId, node.X, node.Y)));
    }

    [Fact]
    public void EdgeAndMembershipChangesRecomputeAndEmptyViewsClearTheLayout()
    {
        var g = new Graph(3);
        var original = g.Update();
        Assert.Empty(original.Edges);

        var edge = g.Connect(1, 2);
        var connected = g.Update();
        Assert.NotSame(original, connected);
        Assert.Single(connected.Edges);

        g.Space.DeleteEdge(edge);
        var disconnected = g.Update();
        Assert.NotSame(connected, disconnected);
        Assert.Empty(disconnected.Edges);

        g.View.RemoveJob(g.Jobs[2]);
        Assert.Equal(2, g.Update().Nodes.Count);

        g.View.RemoveJob(g.Jobs[0]);
        g.View.RemoveJob(g.Jobs[1]);
        var empty = g.Update();
        Assert.Empty(empty.Nodes);
        Assert.Empty(empty.Edges);
        Assert.Equal("", empty.ConnectivityHash);
        Assert.Same(empty, g.Update());
    }

    [Fact]
    public void CardLayoutIsIndependentOfTheDiagramLayout()
    {
        var g = new Graph(3);
        g.Connect(1, 2);
        g.Connect(2, 3);
        var folder = new Folder { Id = 1 };
        g.View.AddFolder(folder);
        g.View.MoveJobToFolder(g.Jobs[1], folder);
        g.View.MoveJobToFolder(g.Jobs[2], folder);

        g.Space.UpdateLayouts();
        var card = g.View.CardLayout!;
        var diagram = g.View.DiagramLayout!;

        Assert.Equal(3, card.Nodes.Count);
        Assert.Equal(2, card.Edges.Count);
        Assert.Equal(2, diagram.Nodes.Count); // job 1 and the collapsed folder
        Assert.All(diagram.Nodes, node => Assert.True(node.Width > 10));
        Assert.True(diagram.GraphWidth > card.GraphWidth);
        Assert.True(diagram.GraphHeight > card.GraphHeight);

        g.View.ResetDiagramLayout(g.Space);
        Assert.Same(card, g.View.CardLayout);
    }

    [Fact]
    public void SavedLayoutIsReusedWhenCurrentAndReplacedWhenStale()
    {
        var g = new Graph(3);
        g.Connect(1, 2);
        var original = g.Update();
        var saved = new JsonObject();
        g.View.WriteToJson(saved);

        View Load(JsonNode json)
        {
            var loaded = new View { Space = g.Space };
            loaded.ReadFromJson(json, new List<User>().AsReadOnly());
            return loaded;
        }

        var current = Load(saved);
        var restored = current.CardLayout!;
        current.UpdateCardLayout(g.Space);
        Assert.Same(restored, current.CardLayout);
        Assert.Equal(original.Nodes, restored.Nodes);
        Assert.Equal(original.Edges.Select(e => (e.SourceX, e.SourceY, e.TargetX, e.TargetY)),
            restored.Edges.Select(e => (e.SourceX, e.SourceY, e.TargetX, e.TargetY)));
        Assert.Equal((original.GraphWidth, original.GraphHeight), (restored.GraphWidth, restored.GraphHeight));

        saved["CardLayout"]!["ConnectivityHash"] = "stale";
        saved["CardLayout"]!["Nodes"] = new JsonArray();
        var stale = Load(saved);
        stale.UpdateCardLayout(g.Space);
        Assert.Equal(3, stale.CardLayout!.Nodes.Count);
        Assert.Equal(original.ConnectivityHash, stale.CardLayout.ConnectivityHash);
    }
}
