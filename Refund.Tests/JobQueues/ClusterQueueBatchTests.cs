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

    [Theory]
    [InlineData("12345,RUNNING", "12345", ClusterJobStatus.Running)]   // space-free squeue -o "%i,%T"
    [InlineData("12345,PENDING", "12345", ClusterJobStatus.Pending)]
    public void ParseActiveJobs_AcceptsCommaSeparator(string line, string id, ClusterJobStatus expected)
    {
        var queue = new ClusterQueue((_, _) => { });
        var result = queue.ParseActiveJobs(line + "\n");
        Assert.Equal(expected, result[id]);
    }

    [Fact]
    public void BuildWorkerScript_PreservesDollarSignsInCommand()
    {
        var queue = new ClusterQueue((_, _) => { }) { SubmissionScriptTemplate = "#!/bin/bash\n{{ command }}\n" };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".sh");
        try
        {
            // Shell content that the old Regex.Replace-as-substitution path corrupted:
            // "$$" -> "$" (so bash later read "$-"), and "${VAR}" -> a group reference.
            queue.BuildWorkerScript(
                "WarpWorker2 --worker-id \"$(hostname)-$$-0-0\" ${SLURM_JOB_ID:-x}",
                new Dictionary<string, string>(), Array.Empty<string>(), path);

            var script = File.ReadAllText(path);
            Assert.Contains("$(hostname)-$$-0-0", script);   // $$ preserved (not collapsed to $)
            Assert.Contains("${SLURM_JOB_ID:-x}", script);   // ${...} preserved (not a group ref)
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
