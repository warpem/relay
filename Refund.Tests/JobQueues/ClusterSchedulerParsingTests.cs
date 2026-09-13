using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ClusterSchedulerParsingTests
{
    private static ClusterQueue Queue(ClusterScheduler scheduler) =>
        new ClusterQueue { SchedulerType = scheduler };

    #region Default and persistence

    [Fact]
    public void SchedulerType_DefaultsToSlurm()
    {
        var queue = new ClusterQueue();
        Assert.Equal(ClusterScheduler.Slurm, queue.SchedulerType);
    }

    [Fact]
    public void SchedulerType_AbsentFromJson_DeserialisesAsSlurm()
    {
        var saved = new ClusterQueue { Alias = "Default" }.ToJson();
        saved.AsObject().Remove("schedulerType");

        var loaded = new ClusterQueue();
        loaded.ReadFromJson(saved);

        Assert.Equal(ClusterScheduler.Slurm, loaded.SchedulerType);
    }

    [Fact]
    public void SchedulerType_RoundTripsThroughJson()
    {
        var saved = Queue(ClusterScheduler.Flux).ToJson();

        var loaded = new ClusterQueue();
        loaded.ReadFromJson(saved);

        Assert.Equal(ClusterScheduler.Flux, loaded.SchedulerType);
    }

    #endregion

    #region Explicit scheduler selection

    [Fact]
    public void ParseClusterJobId_SlurmQueue_DoesNotFallThroughToOtherSchedulers()
    {
        var queue = Queue(ClusterScheduler.Slurm);
        Assert.Throws<InvalidOperationException>(() =>
            queue.ParseClusterJobId("Your job 123 has been submitted"));
    }

    [Fact]
    public void ParseBackendObservation_CustomQueue_UsesConfiguredPatterns()
    {
        var queue = Queue(ClusterScheduler.Custom);
        queue.JobStatusParseTemplatePending = "QUEUED";
        queue.JobStatusParseTemplateRunning = "EXECUTING";
        queue.JobStatusParseTemplateFailed  = "ABORTED";

        Assert.Equal(
            BackendObservationKind.Running,
            queue.ParseBackendObservation("EXECUTING").Kind);
        Assert.Equal(
            BackendObservationKind.Pending,
            queue.ParseBackendObservation("QUEUED").Kind);
        Assert.Equal(
            BackendObservationKind.Failed,
            queue.ParseBackendObservation("ABORTED").Kind);
    }

    [Fact]
    public void ParseClusterJobId_CustomQueue_UsesConfiguredRegex()
    {
        var queue = Queue(ClusterScheduler.Custom);
        queue.JobIdParseRegex = @"accepted as (\w+)";

        Assert.Equal("xyz42", queue.ParseClusterJobId("request accepted as xyz42"));
    }

    [Fact]
    public void ParseBackendObservation_UnrecognisedOutput_IsInconclusive()
    {
        var queue = Queue(ClusterScheduler.Slurm);
        Assert.Equal(
            BackendObservationKind.Unparseable,
            queue.ParseBackendObservation("nonsense").Kind);
    }

    #endregion

    #region Execution observations

    [Theory]
    [InlineData(ClusterScheduler.Slurm, "COMPLETED|", BackendObservationKind.Succeeded)]
    [InlineData(ClusterScheduler.Slurm, "CANCELLED by 1000|", BackendObservationKind.Canceled)]
    [InlineData(ClusterScheduler.Lsf, "DONE", BackendObservationKind.Succeeded)]
    [InlineData(ClusterScheduler.Pbs, "job_state = R", BackendObservationKind.Running)]
    [InlineData(ClusterScheduler.Sge, "123 0.5 job user r queue", BackendObservationKind.Running)]
    [InlineData(ClusterScheduler.Flux, "COMPLETED", BackendObservationKind.Succeeded)]
    public void ParseBackendObservation_UsesSelectedScheduler(
        ClusterScheduler scheduler,
        string output,
        BackendObservationKind expected)
    {
        Assert.Equal(expected, Queue(scheduler).ParseBackendObservation(output).Kind);
    }

    [Fact]
    public void ParseBackendObservation_CustomQueue_SupportsEveryTerminalOutcome()
    {
        var queue = Queue(ClusterScheduler.Custom);
        queue.JobStatusParseTemplateSucceeded = "ALL GOOD";
        queue.JobStatusParseTemplateFailed = "BROKEN";
        queue.JobStatusParseTemplateCanceled = "STOPPED";

        Assert.Equal(
            BackendObservationKind.Succeeded,
            queue.ParseBackendObservation("ALL GOOD").Kind);
        Assert.Equal(
            BackendObservationKind.Failed,
            queue.ParseBackendObservation("BROKEN").Kind);
        Assert.Equal(
            BackendObservationKind.Canceled,
            queue.ParseBackendObservation("STOPPED").Kind);
    }

    [Fact]
    public void TerminalStatusTemplate_IsConfigurableForEveryScheduler()
    {
        var queue = Queue(ClusterScheduler.Pbs);
        queue.TerminalStatusJobTemplate = "qstat -xf {{job_id}}";

        var saved = queue.ToJson();
        var loaded = Queue(ClusterScheduler.Slurm);
        loaded.ReadFromJson(saved);

        Assert.Equal(ClusterScheduler.Pbs, loaded.SchedulerType);
        Assert.Equal("qstat -xf {{job_id}}", loaded.TerminalStatusJobTemplate);
    }

    [Theory]
    [InlineData(ClusterScheduler.Lsf)]
    [InlineData(ClusterScheduler.Pbs)]
    [InlineData(ClusterScheduler.Sge)]
    [InlineData(ClusterScheduler.Flux)]
    [InlineData(ClusterScheduler.Custom)]
    public void DefaultTerminalStatusTemplate_DoesNotInventSchedulerCommands(
        ClusterScheduler scheduler)
    {
        Assert.Null(ClusterSchedulerProtocol.DefaultTerminalStatusTemplate(scheduler));
    }

    [Fact]
    public void DefaultTerminalStatusTemplate_SlurmObservesTheAllocationWithoutJobSteps()
    {
        var template = ClusterSchedulerProtocol.DefaultTerminalStatusTemplate(ClusterScheduler.Slurm);

        Assert.Contains("sacct", template);
        Assert.Contains("--allocations", template);
    }

    [Theory]
    [InlineData("job_state = F\nExit_status = 0", BackendObservationKind.Succeeded)]
    [InlineData("job_state = F\nExit_status = 137", BackendObservationKind.Failed)]
    public void ParseBackendObservation_PbsUsesTerminalExitStatus(
        string output,
        BackendObservationKind expected)
    {
        Assert.Equal(expected, Queue(ClusterScheduler.Pbs).ParseBackendObservation(output).Kind);
    }

    [Theory]
    [InlineData("failed        0\nexit_status   0", BackendObservationKind.Succeeded)]
    [InlineData("failed        1\nexit_status   0", BackendObservationKind.Failed)]
    [InlineData("failed        0\nexit_status   1", BackendObservationKind.Failed)]
    public void ParseBackendObservation_SgeUsesAccountingResult(
        string output,
        BackendObservationKind expected)
    {
        Assert.Equal(expected, Queue(ClusterScheduler.Sge).ParseBackendObservation(output).Kind);
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
        Assert.Throws<InvalidOperationException>(() =>
            Queue(ClusterScheduler.Flux).ParseClusterJobId("\n"));
    }

    #endregion

    #region Flux job status

    [Theory]
    // Pending states
    [InlineData("DEPEND",    BackendObservationKind.Pending)]
    [InlineData("PRIORITY",  BackendObservationKind.Pending)]
    [InlineData("SCHED",     BackendObservationKind.Pending)]
    // Running states
    [InlineData("RUN",       BackendObservationKind.Running)]
    [InlineData("CLEANUP",   BackendObservationKind.Running)]
    // Terminal results
    [InlineData("COMPLETED", BackendObservationKind.Succeeded)]
    [InlineData("FAILED",    BackendObservationKind.Failed)]
    [InlineData("CANCELED",  BackendObservationKind.Canceled)]
    [InlineData("TIMEOUT",   BackendObservationKind.Failed)]
    public void ParseBackendObservation_Flux_MapsLongStatusNames(
        string status,
        BackendObservationKind expected)
    {
        Assert.Equal(expected, Queue(ClusterScheduler.Flux).ParseBackendObservation(status + "\n").Kind);
    }

    [Theory]
    [InlineData("D",  BackendObservationKind.Pending)]
    [InlineData("P",  BackendObservationKind.Pending)]
    [InlineData("S",  BackendObservationKind.Pending)]
    [InlineData("R",  BackendObservationKind.Running)]
    [InlineData("C",  BackendObservationKind.Running)]
    [InlineData("CD", BackendObservationKind.Succeeded)]
    [InlineData("F",  BackendObservationKind.Failed)]
    [InlineData("CA", BackendObservationKind.Canceled)]
    [InlineData("TO", BackendObservationKind.Failed)]
    public void ParseBackendObservation_Flux_MapsAbbreviatedStatusCodes(
        string status,
        BackendObservationKind expected)
    {
        Assert.Equal(expected, Queue(ClusterScheduler.Flux).ParseBackendObservation(status + "\n").Kind);
    }

    [Fact]
    public void ParseBackendObservation_Flux_AbbreviationsAreMatchedWholeNotByPrefix()
    {
        var queue = Queue(ClusterScheduler.Flux);
        Assert.Equal(BackendObservationKind.Succeeded, queue.ParseBackendObservation("CD\n").Kind);
        Assert.Equal(BackendObservationKind.Canceled, queue.ParseBackendObservation("CA\n").Kind);
    }

    [Fact]
    public void ParseBackendObservation_Flux_UnknownStateIsInconclusive()
    {
        Assert.Equal(
            BackendObservationKind.Unparseable,
            Queue(ClusterScheduler.Flux).ParseBackendObservation("WAT\n").Kind);
    }

    [Theory]
    [InlineData("diagnostic F text")]
    [InlineData("job COMPLETED")]
    public void ParseBackendObservation_FluxRequiresTheWholeResponseToBeAState(string output)
    {
        Assert.Equal(
            BackendObservationKind.Unparseable,
            Queue(ClusterScheduler.Flux).ParseBackendObservation(output).Kind);
    }

    #endregion

    #region Pool list parsing honours the selected scheduler

    [Fact]
    public void ParseActiveReceipts_FluxQueue_ClassifiesFluxStates()
    {
        var result = Queue(ClusterScheduler.Flux).ParseActiveReceipts(
            "101,RUN\n102,SCHED\n103,CLEANUP\n");

        Assert.Equal(BackendObservationKind.Running, result["101"].Kind);
        Assert.Equal(BackendObservationKind.Pending, result["102"].Kind);
        Assert.Equal(BackendObservationKind.Running, result["103"].Kind);
    }

    [Fact]
    public void ParseActiveReceipts_SlurmQueue_ClassifiesShortStateCodes()
    {
        var result = Queue(ClusterScheduler.Slurm).ParseActiveReceipts("12345,R\n12346,PD\n");

        Assert.Equal(BackendObservationKind.Running, result["12345"].Kind);
        Assert.Equal(BackendObservationKind.Pending, result["12346"].Kind);
    }

    #endregion
}
