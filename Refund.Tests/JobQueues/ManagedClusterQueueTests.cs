using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobQueues;
using System.Text.Json.Nodes;

namespace Refund.Tests.JobQueues;

public sealed class ManagedClusterQueueTests
{
    private static ClusterQueue Managed() => new()
    {
        SchedulerType = ClusterScheduler.Managed,
        ManagedCores = 8,
        ManagedMemoryGb = 32,
        ManagedGpus = 2
    };

    [Fact]
    public void ManagedDefaultsAreSensibleForAWorkstation()
    {
        var queue = new ClusterQueue();

        Assert.Equal(Environment.ProcessorCount, queue.ManagedCores);
        Assert.Equal(64, queue.ManagedMemoryGb);
        Assert.Equal(1, queue.ManagedGpus);
    }

    [Fact]
    public void ManagedPropertiesRoundTripThroughJson()
    {
        var loaded = new ClusterQueue();
        loaded.ReadFromJson(Managed().ToJson());

        Assert.Equal(ClusterScheduler.Managed, loaded.SchedulerType);
        Assert.Equal(8, loaded.ManagedCores);
        Assert.Equal(32, loaded.ManagedMemoryGb);
        Assert.Equal(2, loaded.ManagedGpus);
    }

    [Fact]
    public void ManagedQueuesDoNotParseSchedulerOutput()
    {
        var queue = Managed();

        Assert.Throws<InvalidOperationException>(() => queue.ParseClusterJobId("anything"));
        Assert.Equal(
            BackendObservationKind.Unparseable,
            queue.ParseBackendObservation("anything").Kind);
    }

    [Fact]
    public void QueueConfigurationIgnoresPersistedRuntimeMembership()
    {
        var json = Managed().ToJson();
        json["Jobs"] = new JsonArray("1.2.3", "1.2.4");
        var loaded = new ClusterQueue();

        loaded.ReadFromJson(json);

        Assert.Empty(loaded.QueuedJobs);
    }

    [Fact]
    public void ExternalQueueMustBeObservableBeforeItCanSubmitWork()
    {
        var queue = new ClusterQueue
        {
            Alias = "PBS",
            SchedulerType = ClusterScheduler.Pbs,
            SubmissionScriptTemplate = "{{ command }}",
            SubmitJobTemplate = "qsub {{ script_path_abs }}",
            AbortJobTemplate = "qdel {{ job_id }}"
        };

        var error = Assert.Throws<InvalidOperationException>(
            queue.ValidateSubmissionConfiguration);

        Assert.Contains("status command", error.Message);
    }
}
