using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobQueues;
using Refund.Jobs.Common.Notes.Note;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;

namespace Refund.Tests.JobExecution;

[Collection("JobRegistry")]
public class SubmissionUsernameTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerAndWorkerCommandsReceiveSubmittingUsername(bool worker)
    {
        string root = Path.Combine(Path.GetTempPath(), "relay-username-" + Guid.NewGuid());
        var job = new Note
        {
            Id = 1, Space = new Space { RootDirectory = root }, SubmittedByUsername = "alice$1"
        };
        var queue = new ClusterQueue
        {
            SubmitJobTemplate = "test '{{ username }}' = 'alice$1' && printf 'Submitted batch job 123'"
        };
        var coordinator = new ExecutionCoordinator([
            new ExecutionQueuePolicy(1, ExecutionBackendKind.ExternalScheduler,
                BackendConfiguration: queue.ToJson().ToJsonString())
        ]);
        var attempt = coordinator.RequestRun(new JobAddress(1, 1, 1), 1, ResourceVector.None, true,
            new WorkerGroupRequest(1, 1, 3));
        var operations = new RelayExecutionOperations(_ => job, (updated, action) =>
        {
            action(updated);
            return Task.CompletedTask;
        });
        try
        {
            var result = worker
                ? await operations.StartWorkerAsync(attempt.CreateSnapshot(), Guid.NewGuid(), CancellationToken.None)
                : await operations.StartAsync(attempt.CreateSnapshot(), [], CancellationToken.None);
            Assert.Equal("123", result.Receipt.Id);
        }
        finally
        {
            await operations.ShutdownAsync(CancellationToken.None);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManagerAndWorkerScriptsExpandUsernameLiterally(bool worker)
    {
        JobRegistry.EnsurePopulated();
        string root = Path.Combine(Path.GetTempPath(), "relay-username-script-" + Guid.NewGuid());
        var space = new Space { RootDirectory = root };
        var queue = new ClusterQueue { SubmissionScriptTemplate = "# user={{ username }}\n{{ command }}" };
        try
        {
            string script;
            if (worker)
            {
                var job = new MotionAndCTF2D { Id = 1, Space = space, SubmittedByUsername = "alice$1" };
                script = queue.BuildWorkerScript("worker", ((IPooledJob)job).GetWorkerResourceValues(root),
                    [], Path.Combine(root, "worker.sh"));
            }
            else
            {
                var job = new Note { Id = 1, Space = space, SubmittedByUsername = "alice$1" };
                script = await queue.PrepareAndWriteScript(job, Guid.NewGuid());
            }
            Assert.StartsWith("# user=alice$1\n", await File.ReadAllTextAsync(script));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
