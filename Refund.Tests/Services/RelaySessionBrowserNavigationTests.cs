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
public class RelaySessionBrowserNavigationTests
{
    [Fact]
    public async Task ForwardToExpandedJobDoesNotBlockWhileItsContentLoads()
    {
        string directory = Path.Combine(Path.GetTempPath(), "relay-browser-navigation-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var manager = new DataManager(new RelayConfiguration
        {
            UsersPath = Path.Combine(directory, "users.json"),
            ProjectsPath = Path.Combine(directory, "projects.json"),
            QueuesPath = Path.Combine(directory, "queues.json")
        });
        try
        {
            JobRegistry.EnsurePopulated();
            var user = await manager.CreateUser(new User { Name = "Navigation test" });
            var project = await manager.CreateProject(user);
            var space = await manager.CreateSpace(user, project, new Space { RootDirectory = directory });
            var view = await manager.CreateView(user, space);
            var job = await manager.CreateJob(user, view, new Note().TypeGuid);
            var navigation = new BrowserNavigationManager();
            await using var session = new RelaySession(navigation, null!, null!, manager);
            using var expanded = new ExpandedJobViewService(manager, session,
                NullLogger<ExpandedJobViewService>.Instance);

            ReadOnlyJob? displayedJob = null;
            bool delayLoading = false;
            var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var loadFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            expanded.OnJobChanged += async selectedJob =>
            {
                if (selectedJob != null && delayLoading)
                {
                    loadStarted.TrySetResult();
                    // Avoid capturing a UI context so the test can release the old
                    // blocking implementation in finally instead of hanging the runner.
                    await releaseLoad.Task.ConfigureAwait(false);
                }
                displayedJob = selectedJob;
                if (selectedJob != null && delayLoading)
                    loadFinished.TrySetResult();
            };

            var request = new NavigationRequest
            {
                ProjectId = project.Id, SpaceId = space.Id, ViewId = view.Id, JobId = job.Id
            };
            await session.NavigateToAsync(request);
            Assert.Same(job, displayedJob);

            string jobUrl = RelaySession.BuildUrl(request);
            request.JobId = null;
            navigation.NavigateFromBrowser(RelaySession.BuildUrl(request));
            Assert.Null(displayedJob);

            delayLoading = true;
            Task forward = Task.Run(() => navigation.NavigateFromBrowser(jobUrl));
            try
            {
                await loadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // The browser event must return while async content is still loading,
                // leaving Blazor's dispatcher free to render and resume the load.
                await forward.WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Null(displayedJob);
                Assert.Same(job, session.Job);
            }
            finally
            {
                releaseLoad.TrySetResult();
                await forward.WaitAsync(TimeSpan.FromSeconds(5));
                await loadFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }

            Assert.Same(job, displayedJob);
        }
        finally
        {
            await manager.ShutdownAsync();
            Field<DataRepository>(manager, "_dataRepository").Dispose();
            Field<UserRepository>(manager, "_userRepository").Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static T Field<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private sealed class BrowserNavigationManager : NavigationManager
    {
        public BrowserNavigationManager() => Initialize("https://relay.test/", "https://relay.test/");

        public void NavigateFromBrowser(string url)
        {
            Uri = ToAbsoluteUri(url).AbsoluteUri;
            NotifyLocationChanged(isInterceptedLink: true);
        }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }
}
