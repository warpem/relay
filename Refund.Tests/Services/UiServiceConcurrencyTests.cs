using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Refund.Configuration;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Common.Notes.Note;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Repositories;
using Refund.Services.Core.Session;

namespace Refund.Tests.Services;

[Collection("JobRegistry")]
public class UiServiceConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldJobCallbackCannotCloseReopenedJob(bool deleted)
    {
        await using var fixture = await Fixture.Create();
        using var editor = new JobEditorService(fixture.Manager, fixture.Session);
        await editor.SetJob(fixture.A);
        var callback = Callback(deleted ? fixture.Manager.JobDeleted : fixture.Manager.JobUpdated,
            GroupName.SpecificJob(fixture.A));
        await editor.SetJob(fixture.B);
        await editor.SetJob(fixture.A);
        fixture.Mutable(fixture.A).Status = JobStatus.Waiting;

        await callback(new(fixture.A));

        Assert.Same(fixture.A, editor.CurrentJob);
    }

    [Fact]
    public async Task OpeningJobCannotSendStaleNotificationAfterPanelAwait()
    {
        await using var fixture = await Fixture.Create();
        using var editor = new JobEditorService(fixture.Manager, fixture.Session);
        await fixture.Session.SetRightPanelCollapsed(true);
        var entered = Signal();
        var release = Signal();
        fixture.Session.OnRightPanelCollapsedChanged += () => { entered.TrySetResult(); return release.Task; };
        var changes = new List<ReadOnlyJob>();
        editor.OnJobChanged += job => { changes.Add(job); return Task.CompletedTask; };
        var opening = editor.SetJob(fixture.A);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            await editor.SetJob(fixture.B);
        }
        finally { release.TrySetResult(); await opening.WaitAsync(Timeout); }

        Assert.Same(fixture.B, editor.CurrentJob);
        Assert.Equal([fixture.B], changes);
        await editor.ClearJob(fixture.A);
        Assert.Same(fixture.B, editor.CurrentJob);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldFactoryCallbackCannotCloseAnotherInstance(bool deleted)
    {
        await using var fixture = await Fixture.Create();
        using var jobEditor = new JobEditorService(fixture.Manager, fixture.Session);
        using var editor = new FactoryEditorService(fixture.Manager, fixture.Session, jobEditor);
        var a = new FactoryInstance { Id = 1, Space = fixture.Mutable(fixture.A).Space, SubJobIds = [fixture.A.Id] }.AsReadOnly();
        var b = new FactoryInstance { Id = 2, Space = fixture.Mutable(fixture.B).Space, SubJobIds = [fixture.B.Id] }.AsReadOnly();
        await editor.SetInstance(a);
        Func<Task> callback;
        if (deleted)
        {
            var handler = Callback(fixture.Manager.FactoryInstanceDeleted,
                GroupName.FactoryInstance(a.Space.Project.Id, a.Space.Id, a.Id));
            callback = () => handler(new(a));
        }
        else
        {
            var handler = Callback(fixture.Manager.JobUpdated, GroupName.Job(a.Space.Project.Id, a.Space.Id, null));
            callback = () => handler(new(fixture.A));
        }
        await editor.SetInstance(b);
        fixture.Mutable(fixture.A).Status = JobStatus.Waiting;
        await callback();
        Assert.Same(b, editor.CurrentInstance);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FactoryCleanupCanOverlapCloseOrDispose(bool dispose)
    {
        await using var fixture = await Fixture.Create();
        using var jobEditor = new JobEditorService(fixture.Manager, fixture.Session);
        using var editor = new FactoryEditorService(fixture.Manager, fixture.Session, jobEditor);
        var subscriptions = Field<List<GroupEventSubscription>>(editor, "_subscriptions");
        int first = 0, second = 0;
        subscriptions.Add(new(() =>
        {
            if (++first != 1) return;
            if (dispose) editor.Dispose();
            else editor.SetInstance(null).GetAwaiter().GetResult();
        }));
        subscriptions.Add(new(() => second++));

        await editor.SetInstance(null);

        Assert.Empty(subscriptions);
        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task SelectionEnumerationSurvivesDeletionAndIdsAreSnapshots()
    {
        await using var fixture = await Fixture.Create();
        using var selection = new CardSelectionService(fixture.Session, fixture.Manager);
        var a = SelectionKey.ForJob(fixture.A);
        var b = SelectionKey.ForJob(fixture.B);
        await selection.AddRange([a, b]);
        var snapshot = selection.SelectedItems;
        var ids = selection.IdsOfType(ItemType.Job);
        using var enumerator = snapshot.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        var deletion = Callback(fixture.Manager.JobDeleted, GroupName.Job(null, null, null));
        await deletion(new(fixture.B));

        Assert.True(enumerator.MoveNext());
        Assert.Equal(b, enumerator.Current);
        Assert.Equal([a, b], snapshot);
        Assert.Equal([a.Id, b.Id], ids);
        Assert.Equal([a], selection.SelectedItems);
    }

    [Fact]
    public async Task ConcurrentSelectionAddsAreUniqueAndReplaceNotifiesOnlyCompleteState()
    {
        await using var fixture = await Fixture.Create();
        using var selection = new CardSelectionService(fixture.Session, fixture.Manager);
        var keys = Enumerable.Range(0, 100).Select(SelectionKey.ForJob).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(async () =>
        {
            await selection.AddRange(keys);
            Assert.Equal(100, selection.SelectedItems.Distinct().Count());
        })));
        Assert.Equal(100, selection.SelectedItems.Count);
        var replacement = new[] { SelectionKey.ForJob(200), SelectionKey.ForJob(201) };
        var seen = new List<SelectionKey[]>();
        selection.OnSelectionChanged += () => { seen.Add(selection.SelectedItems.ToArray()); return Task.CompletedTask; };
        await selection.Replace(replacement);
        Assert.Single(seen);
        Assert.Equal(replacement, seen[0]);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("staging")]
    [InlineData("output")]
    public async Task SwitchingJobsDiscardsOutstandingLogReads(string log)
    {
        await using var fixture = await Fixture.Create();
        await fixture.WriteLogs(fixture.A, 3, "old");
        await fixture.WriteLogs(fixture.B, 0, "new");
        string blockedPath = log switch
        {
            "error" => fixture.A.ErrorFilePath,
            "staging" => fixture.A.LifecycleFilePath,
            _ => fixture.A.LogFilePath(3)
        };
        var entered = Signal();
        var release = Signal();
        int blocked = 0;
        using var expanded = new ExpandedJobViewService(fixture.Manager, fixture.Session,
            NullLogger<ExpandedJobViewService>.Instance, async path =>
            {
                string text = await File.ReadAllTextAsync(path);
                if (path == blockedPath && Interlocked.Increment(ref blocked) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return text;
            });
        var changes = new List<ReadOnlyJob>();
        expanded.OnJobChanged += job => { changes.Add(job); return Task.CompletedTask; };
        var opening = fixture.Select(fixture.A);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            await fixture.Select(fixture.B).WaitAsync(Timeout);
            Assert.Equal("new errors", expanded.CurrentErrors);
            Assert.Equal("new staging", expanded.CurrentStaging);
            Assert.Equal("new output", expanded.GetLogsForIteration(0));
        }
        finally { release.TrySetResult(); await opening.WaitAsync(Timeout); }

        Assert.Equal("new errors", expanded.CurrentErrors);
        Assert.Equal("new staging", expanded.CurrentStaging);
        Assert.Equal("new output", expanded.GetLogsForIteration(0));
        Assert.Equal("", expanded.GetLogsForIteration(3));
        Assert.Equal([0], expanded.AvailableIterations);
        Assert.Equal(0, expanded.CurrentIteration);
        Assert.Equal([fixture.B], changes);
    }

    [Fact]
    public async Task ReopeningSameJobDiscardsReadsFromPreviousSelection()
    {
        await using var fixture = await Fixture.Create();
        await fixture.WriteLogs(fixture.A, 0, "old");
        var entered = Signal();
        var release = Signal();
        int reads = 0;
        using var expanded = new ExpandedJobViewService(fixture.Manager, fixture.Session,
            NullLogger<ExpandedJobViewService>.Instance, async path =>
            {
                var text = await File.ReadAllTextAsync(path);
                if (path == fixture.A.ErrorFilePath && Interlocked.Increment(ref reads) == 1)
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return text;
            });
        var opening = fixture.Select(fixture.A);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            await fixture.Select(fixture.B).WaitAsync(Timeout);
            await fixture.WriteLogs(fixture.A, 0, "fresh");
            await fixture.Select(fixture.A).WaitAsync(Timeout);
        }
        finally { release.TrySetResult(); await opening.WaitAsync(Timeout); }
        Assert.Equal("fresh errors", expanded.CurrentErrors);
    }

    [Fact]
    public async Task DisposingExpandedViewDiscardsPendingReadAndRemovesSessionObserver()
    {
        await using var fixture = await Fixture.Create();
        await fixture.WriteLogs(fixture.A, 0, "old");
        var entered = Signal();
        var release = Signal();
        using var expanded = new ExpandedJobViewService(fixture.Manager, fixture.Session,
            NullLogger<ExpandedJobViewService>.Instance, async path =>
            {
                var text = await File.ReadAllTextAsync(path);
                entered.TrySetResult();
                await release.Task;
                return text;
            });
        int changes = 0;
        expanded.OnJobChanged += _ => { changes++; return Task.CompletedTask; };
        var opening = fixture.Select(fixture.A);
        try
        {
            await entered.Task.WaitAsync(Timeout);
            expanded.Dispose();
            await fixture.Select(fixture.B).WaitAsync(Timeout);
        }
        finally { release.TrySetResult(); await opening.WaitAsync(Timeout); }
        Assert.Equal("", expanded.CurrentErrors);
        Assert.Equal("", expanded.GetLogsForIteration(0));
        Assert.Equal(0, changes);
    }

    [Fact]
    public async Task JobUpdatesFollowLatestIterationAndObserversCanReenter()
    {
        await using var fixture = await Fixture.Create();
        await fixture.WriteLogs(fixture.A, 0, "initial");
        using var expanded = new ExpandedJobViewService(fixture.Manager, fixture.Session,
            NullLogger<ExpandedJobViewService>.Instance);
        await fixture.Select(fixture.A);
        expanded.OnErrorsUpdated += _ => expanded.OpenLogPanel(LogSection.Errors);
        await fixture.WriteLogs(fixture.A, 1, "updated");
        var update = Callback(fixture.Manager.JobUpdated, GroupName.SpecificJob(fixture.A));
        await update(new(fixture.A)).WaitAsync(Timeout);

        Assert.Equal(1, expanded.CurrentIteration);
        Assert.Equal([0, 1], expanded.AvailableIterations);
        Assert.Equal("updated output", expanded.GetLogsForIteration(1));
        Assert.True(expanded.IsLogPanelExpanded);
    }

    // Keep the original callback alive to model a dispatcher that already dequeued
    // it before unsubscribe. Tests control the ordering without relying on sleeps.
    private static Func<GroupEventArgs<T>, Task> Callback<T>(GroupEvent<T> events, string group)
    {
        var groups = Field<Dictionary<string, List<GroupEventSubscriber<T>>>>(events, "_groups");
        return Field<Func<GroupEventArgs<T>, Task>>(Assert.Single(groups[group]), "_action");
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class Fixture : IAsyncDisposable
    {
        public required string DirectoryPath { get; init; }
        public required DataManager Manager { get; init; }
        public required RelaySession Session { get; init; }
        public required ReadOnlyView View { get; init; }
        public required ReadOnlyJob A { get; init; }
        public required ReadOnlyJob B { get; init; }

        public static async Task<Fixture> Create()
        {
            JobRegistry.EnsurePopulated();
            string directory = Path.Combine(Path.GetTempPath(), "relay-ui-races-" + Guid.NewGuid());
            Directory.CreateDirectory(directory);
            var manager = new DataManager(new RelayConfiguration
            {
                UsersPath = Path.Combine(directory, "users.json"),
                ProjectsPath = Path.Combine(directory, "projects.json"),
                QueuesPath = Path.Combine(directory, "queues.json")
            });
            var user = await manager.CreateUser(new User { Name = "UI races test" });
            var project = await manager.CreateProject(user);
            var space = await manager.CreateSpace(user, project, new Space { RootDirectory = directory });
            var view = await manager.CreateView(user, space);
            var a = await manager.CreateJob(user, view, new Note().TypeGuid);
            var b = await manager.CreateJob(user, view, new Note().TypeGuid);
            return new Fixture
            {
                DirectoryPath = directory, Manager = manager, View = view, A = a, B = b,
                Session = new RelaySession(new TestNavigationManager(), null!, null!, manager)
            };
        }

        public Job Mutable(ReadOnlyJob job) => Field<DataRepository>(Manager, "_dataRepository")
            .FindJob(job.Space.Project.Id, job.Space.Id, job.Id);

        public Task Select(ReadOnlyJob job) => Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = View.Space.Project.Id, SpaceId = View.Space.Id, ViewId = View.Id, JobId = job.Id
        });

        public async Task WriteLogs(ReadOnlyJob job, int iteration, string text)
        {
            Mutable(job).LogsAvailableIteration = iteration;
            Directory.CreateDirectory(Path.GetDirectoryName(job.ErrorFilePath)!);
            await File.WriteAllTextAsync(job.ErrorFilePath, text + " errors");
            await File.WriteAllTextAsync(job.LifecycleFilePath, text + " staging");
            await File.WriteAllTextAsync(job.LogFilePath(iteration), text + " output");
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Manager.ShutdownAsync();
            Field<DataRepository>(Manager, "_dataRepository").Dispose();
            Field<UserRepository>(Manager, "_userRepository").Dispose();
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://relay.test/", "https://relay.test/");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).AbsoluteUri;
    }
}
