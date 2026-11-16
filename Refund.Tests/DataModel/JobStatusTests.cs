using Refund.DataModel;
using Refund.Jobs.Common.Notes.Note;
using Refund.Jobs.Fs.Warp;
using Refund.Utils;

namespace Refund.Tests.DataModel;

[Collection("JobRegistry")]
public sealed class JobStatusTests
{
    [Fact]
    public void ClearingCanFailAndBeRetried()
    {
        Assert.True(new Note { Status = JobStatus.Clearing }.CanTransitionState(JobStatus.Failed));
        Assert.True(new Note { Status = JobStatus.Failed }.CanTransitionState(JobStatus.Clearing));
    }

    [Fact]
    public void InterruptedIsTerminalAndCanBeQueuedAgain()
    {
        var job = new Note { Status = JobStatus.Interrupted };

        Assert.False(job.Status.IsUnsettled());
        Assert.True(job.CanTransitionState(JobStatus.Waiting));
        Assert.Equal(EventType.Interrupted, job.Status.ToEventType());
    }

    [Fact]
    public void StoppedResumableJobsReadAsPausedNextToTheirPauseIcon()
    {
        JobRegistry.EnsurePopulated();
        Assert.Equal("Paused", new WarpJob { Status = JobStatus.Aborted }.AsReadOnly().GetStatusDescription());
        Assert.Equal("Pausing", new WarpJob { Status = JobStatus.Aborting }.AsReadOnly().GetStatusDescription());
        Assert.Equal("Aborted", new Note { Status = JobStatus.Aborted }.AsReadOnly().GetStatusDescription());
        Assert.Equal("Running", new WarpJob { Status = JobStatus.Running }.AsReadOnly().GetStatusDescription());
        Assert.Equal("Paused", new WarpJob { Status = JobStatus.Aborted }.AsReadOnly().GetStatusLabel());
        Assert.Equal("Finalizing", new WarpJob { Status = JobStatus.Finalizing }.AsReadOnly().GetStatusLabel());
    }

    [Fact]
    public void FinalizingMapsToItsOwnEvent()
    {
        Assert.Equal(EventType.FinalizingStarted, JobStatus.Finalizing.ToEventType());
    }

    [Fact]
    public void WaitingAndFastSchedulerJobsSupportRealLifecycleTransitions()
    {
        var waiting = new Note { Status = JobStatus.Waiting };
        var staging = new Note { Status = JobStatus.Staging };

        Assert.True(waiting.CanTransitionState(JobStatus.Aborting));
        Assert.True(staging.CanTransitionState(JobStatus.Finalizing));
    }
}
