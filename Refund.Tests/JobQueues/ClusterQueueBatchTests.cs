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

    [Theory]
    [InlineData("123\n456\n", new[] { "123", "456" })]
    [InlineData("123\r\n456\r\n", new[] { "123", "456" })]
    [InlineData("  789  \n\n1011\n", new[] { "789", "1011" })]
    [InlineData("", new string[0])]
    public void ParseJobIds_HandlesLineEndingsAndBlanks(string output, string[] expected)
    {
        var result = ClusterQueue.ParseJobIds(output);
        Assert.Equal(expected.ToHashSet(), result);
    }
}
