using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Common.Notes.Note;

namespace Refund.Tests.DataModel;

[Collection("JobRegistry")]
public class ViewCollectionConcurrencyTests
{
    public ViewCollectionConcurrencyTests() => JobRegistry.EnsurePopulated();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeletingAJobPreservesCollectionsAlreadyBeingRead(bool inFolder)
    {
        var view = new View();
        var folder = inFolder ? new Folder { Id = 1 } : null;
        if (folder != null)
            view.AddFolder(folder);
        var first = new Note { Id = 1 };
        var second = new Note { Id = 2 };
        view.AddJob(first, folder);
        view.AddJob(second, folder);

        var items = folder?.Items ?? view.RootItems;
        var jobs = view.Jobs;
        using var enumerator = items.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.Same(first, enumerator.Current);

        view.RemoveJob(second);

        Assert.True(enumerator.MoveNext());
        Assert.Same(second, enumerator.Current);
        Assert.False(enumerator.MoveNext());
        Assert.Equal(new Job[] { first, second }, jobs);
        Assert.Equal(new IFolderContent[] { first, second }, items);
        Assert.Same(first, Assert.Single(view.Jobs));
        Assert.Same(first, Assert.Single(folder?.Items ?? view.RootItems));
        Assert.Same(first.AsReadOnly(), Assert.Single(
            folder?.AsReadOnly().Items ?? view.AsReadOnly().RootItems));
    }

    [Fact]
    public void RemovingFoldersAndFactoriesPreservesExistingSnapshots()
    {
        var view = new View();
        var folder = new Folder { Id = 1 };
        var instance = new FactoryInstance { Id = 1 };
        view.AddFolder(folder);
        view.AddFactoryInstance(instance, folder);
        var folders = view.Folders;
        var instances = view.FactoryInstances;
        var root = view.RootItems;
        var children = folder.Items;

        view.RemoveFolder(folder);
        view.RemoveFactoryInstance(instance);

        Assert.Same(folder, Assert.Single(folders));
        Assert.Same(instance, Assert.Single(instances));
        Assert.Same(folder, Assert.Single(root));
        Assert.Same(instance, Assert.Single(children));
        Assert.Empty(view.AsReadOnly().Folders);
        Assert.Empty(view.AsReadOnly().FactoryInstances);
        Assert.Empty(view.AsReadOnly().RootItems);
        Assert.Empty(folder.AsReadOnly().Items);
    }

    [Fact]
    public void MovingItemsUpdatesOrderWithoutChangingExistingSnapshots()
    {
        var view = new View();
        var first = new Note { Id = 1 };
        var second = new Note { Id = 2 };
        var folder = new Folder { Id = 1 };
        view.AddJob(first);
        view.AddJob(second);
        view.AddFolder(folder);
        var original = view.RootItems;

        view.MoveItem(second, 0);
        Assert.Equal(new IFolderContent[] { second, first, folder }, view.RootItems);
        Assert.Equal(new IFolderContent[] { first, second, folder }, original);

        view.MoveJobToFolder(first, folder);
        view.MoveJobToFolder(second, folder);
        var children = folder.Items;
        folder.MoveItem(second, 0);
        Assert.Equal(new IFolderContent[] { second, first }, folder.Items);
        Assert.Equal(new IFolderContent[] { first, second }, children);

        view.RemoveFolder(folder);
        Assert.Equal(new IFolderContent[] { second, first }, view.RootItems);
        Assert.Empty(folder.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadOnlyProjectionsCanBeReadWhileJobsAreRemovedAndAdded(bool inFolder)
    {
        var view = new View();
        var folder = inFolder ? new Folder { Id = 1 } : null;
        if (folder != null)
            view.AddFolder(folder);
        var jobs = Enumerable.Range(1, 128).Select(id => new Note { Id = id }).ToArray();
        foreach (var job in jobs)
            view.AddJob(job, folder);

        using var barrier = new Barrier(2);
        var writer = Task.Run(() =>
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                foreach (var job in jobs)
                    view.RemoveJob(job);
                foreach (var job in jobs)
                    view.AddJob(job, folder);
            }
        });
        var reader = Task.Run(() =>
        {
            for (int iteration = 0; iteration < 100; iteration++)
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                for (int read = 0; read < 4; read++)
                {
                    var items = folder?.AsReadOnly().Items ?? view.AsReadOnly().RootItems;
                    Assert.All(items, item => Assert.IsAssignableFrom<ReadOnlyJob>(item));
                    Assert.Equal(items.Count, items.Select(item => item.Id).Distinct().Count());
                    var projectedJobs = view.AsReadOnly().Jobs;
                    Assert.All(projectedJobs, job => Assert.NotNull(job));
                    Assert.Equal(projectedJobs.Count, projectedJobs.Select(job => job.Id).Distinct().Count());
                }
            }
        });

        await Task.WhenAll(writer, reader);
        Assert.Equal(jobs.Length, view.AsReadOnly().Jobs.Count);
        Assert.Equal(jobs.Length, (folder?.AsReadOnly().Items ?? view.AsReadOnly().RootItems).Count);
    }
}
