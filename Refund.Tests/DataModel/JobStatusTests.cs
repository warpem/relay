using Refund.DataModel;
using Refund.Jobs.Common.Notes.Note;

namespace Refund.Tests.DataModel;

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
