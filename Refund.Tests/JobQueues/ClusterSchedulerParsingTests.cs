using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

/// <summary>
/// Covers explicit scheduler selection (ClusterQueue.SchedulerType) and the Flux parsers.
///
/// Before SchedulerType existed, ParseClusterJobId/ParseClusterJobStatus tried every parser in
/// dictionary order and broke on the first non-null result. Because the SLURM status parser
/// returned Unknown rather than null when nothing matched, it always won and the queue's own
/// custom patterns were unreachable. These tests pin down the explicit behaviour instead.
/// </summary>
public class ClusterSchedulerParsingTests
{
    private static ClusterQueue Queue(ClusterScheduler scheduler) =>
        new ClusterQueue((_, _) => { }) { SchedulerType = scheduler };

    #region Default and persistence

    [Fact]
    public void SchedulerType_DefaultsToSlurm()
    {
        // Existing production queues predate this field and must keep behaving as SLURM queues.
        var queue = new ClusterQueue((_, _) => { });
        Assert.Equal(ClusterScheduler.Slurm, queue.SchedulerType);
    }

    [Fact]
    public void SchedulerType_AbsentFromJson_DeserialisesAsSlurm()
    {
        // A queue saved before this field existed has no "schedulerType" key at all.
        var saved = new ClusterQueue((_, _) => { }) { Alias = "Legacy" }.ToJson();
        saved.AsObject().Remove("schedulerType");

        var loaded = new ClusterQueue((_, _) => { });
        loaded.ReadFromJson(saved, (_, _, _) => null);

        Assert.Equal(ClusterScheduler.Slurm, loaded.SchedulerType);
    }

    [Fact]
    public void SchedulerType_RoundTripsThroughJson()
    {
        var saved = Queue(ClusterScheduler.Flux).ToJson();

        var loaded = new ClusterQueue((_, _) => { });
        loaded.ReadFromJson(saved, (_, _, _) => null);

        Assert.Equal(ClusterScheduler.Flux, loaded.SchedulerType);
    }

    #endregion

    #region Explicit selection replaces try-every-parser-in-order

    [Fact]
    public void ParseClusterJobId_SlurmQueue_DoesNotFallThroughToOtherSchedulers()
    {
        // SGE's "Your job 123" used to be picked up by any queue, because the parsers were tried
        // in sequence. A queue declared as SLURM must not silently accept another scheduler's output.
        var queue = Queue(ClusterScheduler.Slurm);
        Assert.Throws<Exception>(() => queue.ParseClusterJobId("Your job 123 has been submitted"));
    }

    [Fact]
    public void ParseClusterJobStatus_CustomQueue_UsesConfiguredPatterns()
    {
        // The regression this whole change exists for: the SLURM parser returned Unknown for
        // unrecognised output, which is non-null, so the loop broke before reaching "custom".
        var queue = Queue(ClusterScheduler.Custom);
        queue.JobStatusParseTemplatePending = "QUEUED";
        queue.JobStatusParseTemplateRunning = "EXECUTING";
        queue.JobStatusParseTemplateFailed  = "ABORTED";

        Assert.Equal(ClusterJobStatus.Running, queue.ParseClusterJobStatus("EXECUTING"));
        Assert.Equal(ClusterJobStatus.Pending, queue.ParseClusterJobStatus("QUEUED"));
        Assert.Equal(ClusterJobStatus.Failed,  queue.ParseClusterJobStatus("ABORTED"));
    }

    [Fact]
    public void ParseClusterJobId_CustomQueue_UsesConfiguredRegex()
    {
        var queue = Queue(ClusterScheduler.Custom);
        queue.JobIdParseRegex = @"accepted as (\w+)";

        Assert.Equal("xyz42", queue.ParseClusterJobId("request accepted as xyz42"));
    }

    [Fact]
    public void ParseClusterJobStatus_UnrecognisedOutput_ReturnsUnknownRatherThanThrowing()
    {
        // Preserves the effective pre-change behaviour for SLURM sites: garbage output leaves the
        // job's state undetermined instead of raising out of the daemon's polling loop.
        var queue = Queue(ClusterScheduler.Slurm);
        Assert.Equal(ClusterJobStatus.Unknown, queue.ParseClusterJobStatus("nonsense"));
    }

    #endregion

    #region Flux job IDs

    [Theory]
    [InlineData("ƒ2ELdc8V\n", "ƒ2ELdc8V")]  // F58, default rendering
    [InlineData("f2ELdc8V\n",      "f2ELdc8V")]       // F58 with FLUX_F58_FORCE_ASCII=1
    [InlineData("3799785836544\n", "3799785836544")]  // decimal
    [InlineData("  f2ELdc8V  \n",  "f2ELdc8V")]
    public void ParseClusterJobId_Flux_AcceptsEveryIdEncoding(string output, string expected)
    {
        Assert.Equal(expected, Queue(ClusterScheduler.Flux).ParseClusterJobId(output));
    }

    [Fact]
    public void ParseClusterJobId_Flux_ThrowsOnEmptyOutput()
    {
        Assert.Throws<Exception>(() => Queue(ClusterScheduler.Flux).ParseClusterJobId("\n"));
    }

    #endregion

    #region Flux job status

    [Theory]
    // Pending states
    [InlineData("DEPEND",    ClusterJobStatus.Pending)]
    [InlineData("PRIORITY",  ClusterJobStatus.Pending)]
    [InlineData("SCHED",     ClusterJobStatus.Pending)]
    // Running states. CLEANUP is transient but every job passes through it, and mapping it to
    // anything but Running makes HandleRunningState finalise the job as Failed on its way to
    // succeeding.
    [InlineData("RUN",       ClusterJobStatus.Running)]
    [InlineData("CLEANUP",   ClusterJobStatus.Running)]
    // Terminal results
    [InlineData("COMPLETED", ClusterJobStatus.Finished)]
    [InlineData("FAILED",    ClusterJobStatus.Failed)]
    [InlineData("CANCELED",  ClusterJobStatus.Failed)]   // Flux spells it with one L
    [InlineData("TIMEOUT",   ClusterJobStatus.Failed)]
    public void ParseClusterJobStatus_Flux_MapsLongStatusNames(string status, ClusterJobStatus expected)
    {
        Assert.Equal(expected, Queue(ClusterScheduler.Flux).ParseClusterJobStatus(status + "\n"));
    }

    [Theory]
    [InlineData("D",  ClusterJobStatus.Pending)]
    [InlineData("P",  ClusterJobStatus.Pending)]
    [InlineData("S",  ClusterJobStatus.Pending)]
    [InlineData("R",  ClusterJobStatus.Running)]
    [InlineData("C",  ClusterJobStatus.Running)]
    [InlineData("CD", ClusterJobStatus.Finished)]
    [InlineData("F",  ClusterJobStatus.Failed)]
    [InlineData("CA", ClusterJobStatus.Failed)]
    [InlineData("TO", ClusterJobStatus.Failed)]
    public void ParseClusterJobStatus_Flux_MapsAbbreviatedStatusCodes(string status, ClusterJobStatus expected)
    {
        Assert.Equal(expected, Queue(ClusterScheduler.Flux).ParseClusterJobStatus(status + "\n"));
    }

    [Fact]
    public void ParseClusterJobStatus_Flux_AbbreviationsAreMatchedWholeNotByPrefix()
    {
        // "C" (CLEANUP) is a prefix of both "CD" (COMPLETED) and "CA" (CANCELED). A Contains-style
        // match would report a finished job as still running, and the job would never leave Running.
        var queue = Queue(ClusterScheduler.Flux);
        Assert.Equal(ClusterJobStatus.Finished, queue.ParseClusterJobStatus("CD\n"));
        Assert.Equal(ClusterJobStatus.Failed,   queue.ParseClusterJobStatus("CA\n"));
    }

    [Fact]
    public void ParseClusterJobStatus_Flux_UnknownStateIsUnknown()
    {
        Assert.Equal(ClusterJobStatus.Unknown, Queue(ClusterScheduler.Flux).ParseClusterJobStatus("WAT\n"));
    }

    #endregion

    #region Pool list parsing honours the selected scheduler

    [Fact]
    public void ParseActiveJobs_FluxQueue_ClassifiesFluxStates()
    {
        // ListJobsTemplate for Flux is: flux jobs -no "{id.dec},{status}"
        var result = Queue(ClusterScheduler.Flux).ParseActiveJobs("101,RUN\n102,SCHED\n103,CLEANUP\n");

        Assert.Equal(ClusterJobStatus.Running, result["101"]);
        Assert.Equal(ClusterJobStatus.Pending, result["102"]);
        Assert.Equal(ClusterJobStatus.Running, result["103"]);
    }

    [Fact]
    public void ParseActiveJobs_SlurmQueue_StillClassifiesShortStateCodes()
    {
        // Guards the existing squeue -o "%i,%t" behaviour, which relies on the SLURM parser's
        // space-padded " R " / " PD " checks.
        var result = Queue(ClusterScheduler.Slurm).ParseActiveJobs("12345,R\n12346,PD\n");

        Assert.Equal(ClusterJobStatus.Running, result["12345"]);
        Assert.Equal(ClusterJobStatus.Pending, result["12346"]);
    }

    #endregion
}
