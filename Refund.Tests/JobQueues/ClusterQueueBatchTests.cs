using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ClusterQueueBatchTests
{
    [Fact]
    public void ListJobsTemplate_DefaultsToEmpty()
    {
        var queue = new ClusterQueue((_, _) => { });
        Assert.Equal("", queue.ListJobsTemplate);
    }

    [Fact]
    public void CancelManyJobsTemplate_DefaultsToEmpty()
    {
        var queue = new ClusterQueue((_, _) => { });
        Assert.Equal("", queue.CancelManyJobsTemplate);
    }

    [Fact]
    public async Task ListActiveJobIds_ThrowsWhenTemplateNotConfigured()
    {
        var queue = new ClusterQueue((_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ListActiveJobIds());
    }

    [Fact]
    public async Task CancelJobs_ThrowsWhenTemplateNotConfigured()
    {
        var queue = new ClusterQueue((_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.CancelJobs(new[] { "123", "456" }));
    }
}
