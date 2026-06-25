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
    public async Task ListActiveJobs_ThrowsWhenTemplateNotConfigured()
    {
        var queue = new ClusterQueue((_, _) => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ListActiveJobs());
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
    public void ParseActiveJobs_ParsesIdsAndHandlesLineEndingsAndBlanks(string output, string[] expectedIds)
    {
        var queue = new ClusterQueue((_, _) => { });
        var result = queue.ParseActiveJobs(output);
        Assert.Equal(expectedIds.ToHashSet(), result.Keys.ToHashSet());
    }

    [Fact]
    public void ParseActiveJobs_IdOnlyLinesClassifyAsUnknown()
    {
        var queue = new ClusterQueue((_, _) => { });
        var result = queue.ParseActiveJobs("123\n456\n");
        Assert.Equal(ClusterJobStatus.Unknown, result["123"]);
        Assert.Equal(ClusterJobStatus.Unknown, result["456"]);
    }

    [Theory]
    [InlineData("RUNNING", ClusterJobStatus.Running)]
    [InlineData("R",       ClusterJobStatus.Running)]   // SLURM short state code
    [InlineData("PENDING", ClusterJobStatus.Pending)]
    [InlineData("PD",      ClusterJobStatus.Pending)]   // SLURM short state code
    public void ParseActiveJobs_ClassifiesStateColumn(string state, ClusterJobStatus expected)
    {
        var queue = new ClusterQueue((_, _) => { });
        // Mirrors squeue -o "%i %T" (and "%i %t") — ID first, state second.
        var result = queue.ParseActiveJobs($"12345 {state}\n");
        Assert.Equal(expected, result["12345"]);
    }
}
