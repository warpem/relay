using Refund.DataModel;
using Refund.Jobs.Ts.Reconstruction.ReconstructMap;
using Xunit;

namespace Refund.Tests.Mcp;

/// <summary>
/// Locks the state-transition and input-validation behavior the MCP queue/abort tools now rely on
/// (enforced in DataManager). These are pure Job-level checks, so they run without a DataManager.
/// </summary>
[Collection("JobRegistry")]
public class JobStateAndValidationTests
{
    private static readonly object _lock = new();
    private static void EnsurePopulated()
    {
        lock (_lock)
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
    }

    [Fact]
    public void Abort_FromFailed_IsNotAllowed_ButFromRunning_Is()
    {
        EnsurePopulated();
        var job = new ReconstructMap { Status = JobStatus.Failed };
        Assert.False(job.CanTransitionState(JobStatus.Aborting));

        job.Status = JobStatus.Running;
        Assert.True(job.CanTransitionState(JobStatus.Aborting));
    }

    [Fact]
    public void ValidatePortInputs_FlagsUnconnectedRequiredPort()
    {
        EnsurePopulated();
        var job = new ReconstructMap(); // fresh: required input ports have no connections
        var errors = job.ValidatePortInputs();
        Assert.NotEmpty(errors);
        Assert.Contains(ReconstructMap.PortInDataSetTs, errors.Keys); // "TiltSeries"
    }
}
