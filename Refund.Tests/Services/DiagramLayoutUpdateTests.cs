using System.Reflection;
using System.Text.Json.Nodes;
using NavigationManager = Microsoft.AspNetCore.Components.NavigationManager;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Common.Import.ImportMap;
using Refund.Jobs.Refinement.Masks.CreateMask;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Repositories;
using Refund.Services.Core.Session;
using Warp.Tools;

namespace Refund.Tests.Services;

[Collection("JobRegistry")]
public sealed class DiagramLayoutUpdateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloningJobsIncludesNewNodesAndFinalConnections(bool tree)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.CreateJobAsync(new ImportMap());
        var target = await fixture.CreateJobAsync(new CreateMask());
        await fixture.ConnectAsync(source, target);

        if (tree)
            await fixture.Manager.CloneJobTree(fixture.User, [source, target], fixture.View);
        else
            await fixture.Manager.CloneJob(fixture.User, target, fixture.View);

        var clones = fixture.View.Jobs.Where(job => job.Id != source.Id && job.Id != target.Id).ToList();
        Assert.Equal(tree ? 2 : 1, clones.Count);
        foreach (var clone in clones)
            Assert.Contains(fixture.View.DiagramLayout!.Nodes, node => node.ItemId == clone.Id);
        var clonedTarget = clones.Single(job => job.TypeGuid == target.TypeGuid);
        int expectedSource = tree ? clones.Single(job => job.TypeGuid == source.TypeGuid).Id : source.Id;
        Assert.Contains(fixture.View.DiagramLayout!.Edges,
            edge => edge.SourceJobId == expectedSource && edge.TargetJobId == clonedTarget.Id);
    }

    [Fact]
    public async Task AddingAndRemovingJobsRefreshesViewAndContainingFolder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.CreateJobAsync(new ImportMap());
        var target = await fixture.CreateJobAsync(new CreateMask());
        await fixture.ConnectAsync(source, target);
        var otherView = await fixture.Manager.CreateView(fixture.User, fixture.Space);

        await fixture.Manager.AddJobToView(fixture.User, otherView, source);
        await fixture.Manager.AddJobToView(fixture.User, otherView, target);
        Assert.Equal(2, otherView.DiagramLayout!.Nodes.Count);
        Assert.Single(otherView.DiagramLayout.Edges);

        await fixture.Manager.RemoveJobFromView(fixture.User, otherView, target);
        Assert.Single(otherView.DiagramLayout.Nodes);
        Assert.Empty(otherView.DiagramLayout.Edges);
        Assert.Single(fixture.View.DiagramLayout!.Edges);

        var folder = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Folder");
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, source, folder);
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, target, folder);
        Assert.Single(folder.DiagramLayout!.Edges);

        await fixture.Manager.RemoveJobFromView(fixture.User, fixture.View, target);
        Assert.Single(folder.DiagramLayout.Nodes);
        Assert.Empty(folder.DiagramLayout.Edges);
    }

    [Fact]
    public async Task FactoryCreationHidesSubJobsAndCloningInitializesTheirLayout()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateFactoryAsync();

        var node = Assert.Single(fixture.View.DiagramLayout!.Nodes);
        Assert.True(node.IsFactoryInstance);
        Assert.Equal(instance.Id, node.ItemId);
        Assert.Equal(2, instance.DiagramLayout!.Nodes.Count);
        Assert.Single(instance.DiagramLayout.Edges);

        var clone = await fixture.Manager.CloneFactoryInstance(fixture.User, fixture.View, instance);
        Assert.Equal(2, clone.DiagramLayout!.Nodes.Count);
        var edge = Assert.Single(clone.DiagramLayout.Edges);
        Assert.Contains(clone.SubJobIds, id => id == edge.SourceJobId);
        Assert.Contains(clone.SubJobIds, id => id == edge.TargetJobId);
        Assert.All(fixture.View.DiagramLayout.Nodes, item => Assert.True(item.IsFactoryInstance));
    }

    [Fact]
    public async Task DeletingAnInternalEdgeRefreshesTheFactoryDiagram()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateFactoryAsync();
        var edge = Assert.Single(fixture.Space.Edges);
        Assert.Single(instance.DiagramLayout!.Edges);

        await fixture.Manager.DeleteEdge(edge);

        Assert.Empty(instance.DiagramLayout.Edges);
        Assert.Equal(2, instance.DiagramLayout.Nodes.Count);
    }

    [Fact]
    public async Task FactoryConnectionsInsideFoldersRefreshOnCreateAndDelete()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.CreateJobAsync(new ImportMap());
        var folder = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Folder");
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, source, folder);
        var instance = await fixture.CreateFactoryAsync(folder, includeSource: false);
        Assert.Empty(folder.DiagramLayout!.Edges);

        var edge = await fixture.ConnectAsync(source, Assert.Single(instance.SubJobs));

        Assert.Single(folder.DiagramLayout.Edges);
        await fixture.Manager.DeleteEdge(edge);
        Assert.Empty(folder.DiagramLayout.Edges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovingFactoryInstancesRefreshesTheirContainingLayouts(bool delete)
    {
        await using var fixture = await Fixture.CreateAsync();
        var folder = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Folder");
        var instance = await fixture.CreateFactoryAsync(folder);
        var otherView = await fixture.Manager.CreateView(fixture.User, fixture.Space);
        await fixture.Manager.AddFactoryInstanceToView(fixture.User, otherView, instance);
        Assert.Single(folder.DiagramLayout!.Nodes);
        Assert.Single(otherView.DiagramLayout!.Nodes);

        if (delete)
            await fixture.Manager.DeleteFactoryInstance(fixture.User, fixture.Space, instance);
        else
            await fixture.Manager.RemoveFactoryInstanceFromView(fixture.User, fixture.View, instance);

        Assert.Empty(folder.DiagramLayout.Nodes);
        Assert.Equal(delete ? 0 : 1, otherView.DiagramLayout.Nodes.Count);
    }

    [Fact]
    public async Task CardSizeChangesRefreshLayoutWhileMetadataKeepsTheCachedLayout()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.CreateJobAsync(new ImportMap());
        var original = fixture.View.DiagramLayout!;

        await fixture.Manager.UpdateJob(fixture.User, job, mutable => mutable.Alias = "Renamed");
        Assert.Same(original, fixture.View.DiagramLayout);

        await fixture.Manager.UpdateJob(fixture.User, job, mutable => mutable.CardSquareCount = new int2(1, 2));
        Assert.NotSame(original, fixture.View.DiagramLayout);
        Assert.NotEqual(original.Nodes[0].Height, fixture.View.DiagramLayout!.Nodes[0].Height);
    }

    [Fact]
    public async Task SpaceNotificationSeesAllLayoutsAfterAGenericGraphEdit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.CreateJobAsync(new ImportMap());
        var target = await fixture.CreateJobAsync(new CreateMask());
        await fixture.ConnectAsync(source, target);
        var folder = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Folder");
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, source, folder);
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, target, folder);
        var otherView = await fixture.Manager.CreateView(fixture.User, fixture.Space);
        await fixture.Manager.AddJobToView(fixture.User, otherView, source);
        await fixture.Manager.AddJobToView(fixture.User, otherView, target);
        var notified = new TaskCompletionSource<(int FolderNodes, int FolderEdges, int ViewNodes, int ViewEdges)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = fixture.Manager.SpaceUpdated.Add(
            GroupName.Space(fixture.Space.Project.Id, fixture.Space.Id), _ =>
            {
                notified.TrySetResult((folder.DiagramLayout!.Nodes.Count, folder.DiagramLayout.Edges.Count,
                    otherView.DiagramLayout!.Nodes.Count, otherView.DiagramLayout.Edges.Count));
                return Task.CompletedTask;
            });
        try
        {
            // The generic mutator has no job-specific layout hooks.
            await fixture.Manager.UpdateSpace(fixture.User, fixture.Space,
                space => space.DeleteJob(space.FindJob(target.Id)));

            Assert.Equal((1, 0, 1, 0), await notified.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            subscription.Unsubscribe();
        }
    }

    [Fact]
    public async Task MetadataEditsPreserveBothPopulatedAndEmptyLayoutCaches()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CreateJobAsync(new ImportMap());
        var emptyView = await fixture.Manager.CreateView(fixture.User, fixture.Space);
        var original = fixture.View.DiagramLayout;
        var empty = emptyView.DiagramLayout;

        await fixture.Manager.UpdateView(fixture.User, fixture.View, view => view.Alias = "Renamed");

        Assert.Same(original, fixture.View.DiagramLayout);
        Assert.Same(empty, emptyView.DiagramLayout);
    }

    [Fact]
    public async Task GenericFactoryEditsRefreshTheSubJobDiagram()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateFactoryAsync();
        int remainingId = instance.SubJobIds[1];

        await fixture.Manager.UpdateFactoryInstance(fixture.User, instance,
            mutable => mutable.SubJobIds.RemoveAt(0));

        Assert.Equal(remainingId, Assert.Single(instance.DiagramLayout!.Nodes).ItemId);
        Assert.Empty(instance.DiagramLayout.Edges);
    }

    [Fact]
    public async Task LoadingRepairsAStaleSavedDiagram()
    {
        await using var fixture = await Fixture.CreateAsync();
        var job = await fixture.CreateJobAsync(new ImportMap());
        var space = fixture.Repository.FindSpace(fixture.Space.Project.Id, fixture.Space.Id);
        var saved = new JsonObject();
        space.WriteToJson(saved);
        saved["Views"]![0]!["DiagramLayout"]!["ConnectivityHash"] = "stale";
        saved["Views"]![0]!["DiagramLayout"]!["Nodes"] = new JsonArray();

        var loaded = new Space { Project = space.Project };
        loaded.ReadFromJson(saved, fixture.Manager.Users.Select(user =>
            Field<UserRepository>(fixture.Manager, "_userRepository").FindUser(user.Id)).ToList().AsReadOnly());

        var node = Assert.Single(loaded.Views[0].DiagramLayout!.Nodes);
        Assert.Equal(job.Id, node.ItemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupLoadingRecreatesMissingLayouts(bool removeAllLayouts)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.CreateJobAsync(new ImportMap());
        var target = await fixture.CreateJobAsync(new CreateMask());
        await fixture.ConnectAsync(source, target);
        var parent = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Parent");
        var child = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Child");
        await fixture.Manager.MoveFolderToFolder(fixture.User, fixture.View, child, parent);
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, source, child);
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, target, child);
        var instance = await fixture.CreateFactoryAsync(parent);
        var emptyView = await fixture.Manager.CreateView(fixture.User, fixture.Space);
        var space = fixture.Repository.FindSpace(fixture.Space.Project.Id, fixture.Space.Id);
        var saved = space.ToJson();
        var savedView = saved["Views"]![0]!;

        // Simulate a deployed space predating view-card graphs, including a saved diagram position.
        savedView["DiagramLayout"]!["Nodes"]![0]!["X"] = 1234.0;
        if (removeAllLayouts)
            RemoveLayouts(saved);
        else
            foreach (var view in saved["Views"]!.AsArray())
                view!.AsObject().Remove("CardLayout");

        // Exercise the same repository -> project -> space loading path used by DataManager startup.
        // Use separate files so the live fixture's autosave cannot replace the legacy document.
        string spacePath = Path.Combine(space.RootDirectory, "startup-space.json");
        string projectsPath = Path.Combine(space.RootDirectory, "startup-projects.json");
        var project = space.Project.ToJson();
        project["Spaces"] = new JsonArray(JsonValue.Create(spacePath));
        await File.WriteAllTextAsync(spacePath, saved.ToJsonString());
        await File.WriteAllTextAsync(projectsPath, new JsonObject
        {
            ["Projects"] = new JsonArray(project)
        }.ToJsonString());
        var users = fixture.Manager.Users.Select(user =>
            Field<UserRepository>(fixture.Manager, "_userRepository").FindUser(user.Id)).ToList().AsReadOnly();
        using var repository = new DataRepository(projectsPath);

        repository.LoadAll(users);

        var loaded = repository.FindSpace(space.Project.Id, space.Id);
        var loadedView = loaded.Views.Single(view => view.Id == fixture.View.Id);
        Assert.Equal(4, loadedView.CardLayout!.Nodes.Count);
        Assert.Equal(2, loadedView.CardLayout.Edges.Count);
        Assert.True(Assert.Single(loadedView.DiagramLayout!.Nodes).IsFolder);
        if (!removeAllLayouts)
            Assert.Equal(1234.0, loadedView.DiagramLayout.Nodes[0].X);

        var loadedChild = loadedView.Folders.Single(folder => folder.Id == child.Id);
        Assert.Equal(2, loadedChild.Layout!.Nodes.Count);
        Assert.Single(loadedChild.Layout.Edges);
        Assert.Equal(2, loadedChild.DiagramLayout!.Nodes.Count);
        Assert.Single(loadedChild.DiagramLayout.Edges);
        Assert.All(loadedView.Folders, folder =>
        {
            Assert.NotNull(folder.Layout);
            Assert.NotNull(folder.DiagramLayout);
        });
        var loadedInstance = loaded.FactoryInstances.Single(factory => factory.Id == instance.Id);
        Assert.Equal(2, loadedInstance.DiagramLayout!.Nodes.Count);
        Assert.Single(loadedInstance.DiagramLayout.Edges);
        var definition = Assert.Single(loaded.FactoryDefinitions);
        Assert.Equal(2, definition.CardLayout!.Nodes.Count);
        Assert.Single(definition.CardLayout.Edges);
        Assert.Equal(2, definition.DiagramLayout!.Nodes.Count);
        Assert.Single(definition.DiagramLayout.Edges);
        var loadedEmpty = loaded.Views.Single(view => view.Id == emptyView.Id);
        Assert.Empty(loadedEmpty.CardLayout!.Nodes);
        Assert.Empty(loadedEmpty.DiagramLayout!.Nodes);

        static void RemoveLayouts(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                obj.Remove("Layout");
                obj.Remove("DiagramLayout");
                obj.Remove("CardLayout");
                foreach (var child in obj.ToArray())
                    RemoveLayouts(child.Value);
            }
            else if (node is JsonArray array)
                foreach (var child in array)
                    RemoveLayouts(child);
        }
    }

    [Fact]
    public async Task DefinitionEditsRefreshBothLayoutsBeforeNotifyingWithoutABuilder()
    {
        await using var fixture = await Fixture.CreateAsync();
        var definition = await fixture.Manager.CreateFactoryDefinition(fixture.User, fixture.Space);
        await fixture.Manager.UpdateFactoryDefinition(fixture.User, fixture.Space, definition, mutable =>
        {
            mutable.SubJobs.Add(new ImportMap { Id = 1 });
            mutable.SubJobs.Add(new CreateMask { Id = 2 });
        });
        var originalDiagram = definition.DiagramLayout;
        var originalCard = definition.CardLayout;
        Assert.NotNull(originalDiagram);
        Assert.Empty(originalCard.Edges);

        var notified = new TaskCompletionSource<(int DiagramEdges, int CardEdges)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = fixture.Manager.FactoryDefinitionUpdated.Add(
            GroupName.FactoryDefinition(fixture.Space.Project.Id, fixture.Space.Id, definition.Id), _ =>
            {
                notified.TrySetResult((definition.DiagramLayout.Edges.Count, definition.CardLayout.Edges.Count));
                return Task.CompletedTask;
            });
        try
        {
            await fixture.Manager.UpdateFactoryDefinition(fixture.User, fixture.Space, definition,
                mutable => mutable.InternalEdges.Add(new FactoryEdge(
                    $"1.{ImportMap.PortOutMap}", $"2.{CreateMask.PortInMap}")));
            Assert.Equal((1, 1), await notified.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotSame(originalDiagram, definition.DiagramLayout);
            Assert.NotSame(originalCard, definition.CardLayout);
        }
        finally
        {
            subscription.Unsubscribe();
        }

        var diagram = definition.DiagramLayout;
        var card = definition.CardLayout;
        await fixture.Manager.UpdateFactoryDefinition(fixture.User, fixture.Space, definition,
            mutable => mutable.Alias = "Renamed");
        Assert.Same(diagram, definition.DiagramLayout);
        Assert.Same(card, definition.CardLayout);

        await fixture.Manager.UpdateFactoryDefinition(fixture.User, fixture.Space, definition,
            mutable => mutable.InternalEdges.Clear());
        Assert.Empty(definition.DiagramLayout.Edges);
        Assert.Empty(definition.CardLayout.Edges);

        await fixture.Manager.UpdateFactoryDefinition(fixture.User, fixture.Space, definition,
            mutable => mutable.SubJobs.Clear());
        Assert.Empty(definition.DiagramLayout.Nodes);
        Assert.Empty(definition.CardLayout.Nodes);
        diagram = definition.DiagramLayout;
        card = definition.CardLayout;
        await fixture.Manager.UpdateFactoryDefinition(fixture.User, fixture.Space, definition,
            mutable => mutable.Alias = "Empty");
        Assert.Same(diagram, definition.DiagramLayout);
        Assert.Same(card, definition.CardLayout);
    }

    [Fact]
    public async Task FolderCardConnectionsRefreshAcrossNestedFoldersAndViews()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.CreateJobAsync(new ImportMap());
        var target = await fixture.CreateJobAsync(new CreateMask());
        var parent = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Parent");
        var child = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Child");
        await fixture.Manager.MoveFolderToFolder(fixture.User, fixture.View, child, parent);
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, source, child);
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, target, parent);
        var otherView = await fixture.Manager.CreateView(fixture.User, fixture.Space);
        await fixture.Manager.AddJobToView(fixture.User, otherView, source);
        await fixture.Manager.AddJobToView(fixture.User, otherView, target);
        var original = parent.Layout;

        var edge = await fixture.ConnectAsync(source, target);
        Assert.NotSame(original, parent.Layout);
        Assert.Single(parent.Layout!.Edges);
        Assert.Single(otherView.DiagramLayout!.Edges);
        Assert.Empty(child.Layout!.Edges);

        await fixture.Manager.DeleteEdge(edge);
        Assert.Empty(parent.Layout.Edges);
        Assert.Empty(otherView.DiagramLayout.Edges);
    }

    [Fact]
    public async Task ViewCardLayoutFollowsFlatMembershipAndConnections()
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = await fixture.CreateJobAsync(new ImportMap());
        var target = await fixture.CreateJobAsync(new CreateMask());
        var parent = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Parent");
        var child = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Child");
        await fixture.Manager.MoveFolderToFolder(fixture.User, fixture.View, child, parent);
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, target, child);
        var unconnected = fixture.View.CardLayout!;
        Assert.Equal(2, unconnected.Nodes.Count);

        var edge = await fixture.ConnectAsync(source, target);
        var connected = fixture.View.CardLayout!;
        Assert.NotSame(unconnected, connected);
        Assert.Single(connected.Edges);

        await fixture.Manager.UpdateJob(fixture.User, target, mutable => mutable.Alias = "Renamed");
        await fixture.Manager.MoveJobToFolder(fixture.User, fixture.View, source, parent);
        Assert.Same(connected, fixture.View.CardLayout);

        await fixture.Manager.DeleteEdge(edge);
        Assert.Empty(fixture.View.CardLayout!.Edges);

        await fixture.Manager.RemoveJobFromView(fixture.User, fixture.View, source);
        Assert.Equal(target.Id, Assert.Single(fixture.View.CardLayout!.Nodes).ItemId);
        await fixture.Manager.RemoveJobFromView(fixture.User, fixture.View, target);
        Assert.Empty(fixture.View.CardLayout!.Nodes);
    }

    [Fact]
    public async Task ViewCardLayoutIncludesFactorySubJobsWithoutIdCollisions()
    {
        await using var fixture = await Fixture.CreateAsync();
        var instance = await fixture.CreateFactoryAsync();

        Assert.Equal(instance.SubJobIds.Order(),
            fixture.View.CardLayout!.Nodes.Select(node => node.ItemId).Order());
        Assert.Single(fixture.View.CardLayout.Edges);
        Assert.True(Assert.Single(fixture.View.DiagramLayout!.Nodes).IsFactoryInstance);
    }

    [Theory]
    [InlineData(0)] // Home activity
    [InlineData(1)] // Containing view, outside the factory
    [InlineData(2)] // Already browsing the factory
    public async Task FactorySubJobEditOpensInItsFactoryContext(int initialContext)
    {
        await using var fixture = await Fixture.CreateAsync();
        var folder = await fixture.Manager.CreateFolder(fixture.User, fixture.View, "Factory folder");
        var instance = await fixture.CreateFactoryAsync(folder);
        var job = instance.SubJobs.First();
        await using var session = new RelaySession(new TestNavigationManager(), null!, null!, fixture.Manager);
        if (initialContext == 1)
            await session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = fixture.Space.Project.Id, SpaceId = fixture.Space.Id, ViewId = fixture.View.Id
            });
        else if (initialContext == 2)
            await session.NavigateToAsync(JobNavigation.ForJob(job, fixture.View));
        using var editor = new JobEditorService(fixture.Manager, session);
        var menus = new MenuActionService(fixture.Manager, session, editor, null!, null!, null!, null!, null!);

        var edit = Assert.Single(menus.GetJobActions([job]), action => action.Name == "Edit job");
        await edit.Action();

        Assert.Same(job, editor.CurrentJob);
        Assert.Equal(fixture.View.Id, session.ViewId);
        Assert.Equal(folder.Id, session.FolderId);
        Assert.Equal(instance.Id, session.FactoryInstanceId);
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://relay.test/", "https://relay.test/");

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "relay-diagram-" + Guid.NewGuid());
        public DataManager Manager { get; }
        public ReadOnlyUser User { get; private set; } = null!;
        public ReadOnlySpace Space { get; private set; } = null!;
        public ReadOnlyView View { get; private set; } = null!;
        public DataRepository Repository => Field<DataRepository>(Manager, "_dataRepository");

        private Fixture()
        {
            Directory.CreateDirectory(_directory);
            Manager = new DataManager(new RelayConfiguration
            {
                UsersPath = Path.Combine(_directory, "users.json"),
                ProjectsPath = Path.Combine(_directory, "projects.json"),
                QueuesPath = Path.Combine(_directory, "queues.json")
            });
        }

        public static async Task<Fixture> CreateAsync()
        {
            JobRegistry.EnsurePopulated();
            var fixture = new Fixture();
            try
            {
                fixture.User = await fixture.Manager.CreateUser(new User { Name = "Diagram test" });
                var project = await fixture.Manager.CreateProject(fixture.User);
                fixture.Space = await fixture.Manager.CreateSpace(fixture.User, project,
                    new Space { RootDirectory = fixture._directory });
                fixture.View = await fixture.Manager.CreateView(fixture.User, fixture.Space);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public Task<ReadOnlyJob> CreateJobAsync(Job job) =>
            Manager.CreateJob(User, View, job.TypeGuid);

        public Task<ReadOnlyEdge> ConnectAsync(ReadOnlyJob source, ReadOnlyJob target) =>
            Manager.CreateEdge(Space, source.PortsOut[ImportMap.PortOutMap], target.PortsIn[CreateMask.PortInMap]);

        public async Task<ReadOnlyFactoryInstance> CreateFactoryAsync(ReadOnlyFolder? folder = null, bool includeSource = true)
        {
            var definition = await Manager.CreateFactoryDefinition(User, Space);
            await Manager.UpdateFactoryDefinition(User, Space, definition, mutable =>
            {
                int targetId = includeSource ? 2 : 1;
                if (includeSource)
                {
                    mutable.SubJobs.Add(new ImportMap { Id = 1 });
                    mutable.InternalEdges.Add(new FactoryEdge($"1.{ImportMap.PortOutMap}", $"2.{CreateMask.PortInMap}"));
                }
                mutable.SubJobs.Add(new CreateMask { Id = targetId });
                mutable.ExposedPortsIn.Add(new ExposedPort { SubJobId = targetId, PortName = CreateMask.PortInMap });
            });
            return await Manager.CreateFactoryInstance(User, View, definition.Id, folder);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Manager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                Repository.Dispose();
                Field<UserRepository>(Manager, "_userRepository").Dispose();
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
