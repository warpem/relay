using Refund.Services.Core.DataManager;

namespace Refund.Tests.Services;

public class GroupEventTests
{
    [Fact]
    public async Task SlowSubscriberDoesNotLoseDistinctObjectNotifications()
    {
        var events = new GroupEvent<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<int>();
        var subscription = events.Add("deleted", async args =>
        {
            if (args.Object == 0)
            {
                entered.SetResult();
                await release.Task;
            }
            received.Add(args.Object);
            if (args.Object == 100)
                completed.SetResult();
        });

        try
        {
            await events.Invoke("deleted", new GroupEventArgs<int>(0));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            for (int id = 1; id <= 100; id++)
                await events.Invoke("deleted", new GroupEventArgs<int>(id));
            release.SetResult();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(Enumerable.Range(0, 101), received);
        }
        finally
        {
            release.TrySetResult();
            subscription.Unsubscribe();
        }
    }

    [Fact]
    public async Task Invoke_DoesNotWaitForBlockedSubscriber()
    {
        var events = new GroupEvent<int>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthyCompleted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedSubscription = events.Add("group", _ =>
        {
            entered.TrySetResult();
            return neverCompletes.Task;
        });
        var healthySubscription = events.Add("group", args =>
        {
            healthyCompleted.TrySetResult(args.Object);
            return Task.CompletedTask;
        });

        await events.Invoke("group", new GroupEventArgs<int>(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, await healthyCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)));

        blockedSubscription.Unsubscribe();
        healthySubscription.Unsubscribe();
    }

    [Fact]
    public async Task Subscriber_ReceivesNotificationsInOrder()
    {
        var events = new GroupEvent<int>();
        var received = new List<int>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscription = events.Add("group", args =>
        {
            received.Add(args.Object);
            if (args.Object == 3)
                completed.TrySetResult();
            return Task.CompletedTask;
        });

        await events.Invoke("group", new GroupEventArgs<int>(1));
        await events.Invoke("group", new GroupEventArgs<int>(2));
        await events.Invoke("group", new GroupEventArgs<int>(3));
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal([1, 2, 3], received);
        subscription.Unsubscribe();
    }

    [Fact]
    public async Task SubscriberFailure_DoesNotBlockOtherSubscribers()
    {
        var events = new GroupEvent<int>();
        var completed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failing = events.Add("group", _ => throw new InvalidOperationException("broken observer"));
        var healthy = events.Add("group", args =>
        {
            completed.TrySetResult(args.Object);
            return Task.CompletedTask;
        });

        await events.Invoke("group", new GroupEventArgs<int>(17));

        Assert.Equal(17, await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        failing.Unsubscribe();
        healthy.Unsubscribe();
    }
}
