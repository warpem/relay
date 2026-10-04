using System.Reflection;
using Microsoft.AspNetCore.Components;
using Refund.Services;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Refund.Tests.Services;

public class JobEditorServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingEditorCanOverlapSubscriptionCleanup(bool dispose)
    {
        await using var session = new RelaySession(new TestNavigationManager(), null!, null!, null!);
        using var editor = new JobEditorService(null!, session);
        var subscriptions = (List<GroupEventSubscription>)typeof(JobEditorService)
            .GetField("_subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(editor)!;
        int firstUnsubscribed = 0;
        int secondUnsubscribed = 0;

        // Re-enter cleanup while the outer close is still processing its subscriptions.
        // Previously the nested close cleared the list under the outer enumerator.
        subscriptions.Add(new GroupEventSubscription(() =>
        {
            if (Interlocked.Increment(ref firstUnsubscribed) == 1)
            {
                if (dispose)
                    editor.Dispose();
                else
                    editor.SetJob(null).GetAwaiter().GetResult();
            }
        }));
        subscriptions.Add(new GroupEventSubscription(() => secondUnsubscribed++));

        await editor.SetJob(null);

        Assert.Null(editor.CurrentJob);
        Assert.Empty(subscriptions);
        Assert.Equal(1, firstUnsubscribed);
        Assert.Equal(1, secondUnsubscribed);
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://relay.test/", "https://relay.test/");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }
}
