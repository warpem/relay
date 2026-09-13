using Refund.DataModel;
using Refund.JobExecution;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ClusterQueueBatchTests
{
    [Fact]
    public void ListJobsTemplate_DefaultsToEmpty()
    {
        var queue = new ClusterQueue();
        Assert.Equal("", queue.ListJobsTemplate);
    }

    [Fact]
    public void CancelManyJobsTemplate_DefaultsToEmpty()
    {
        var queue = new ClusterQueue();
        Assert.Equal("", queue.CancelManyJobsTemplate);
    }

    [Fact]
    public async Task ObserveReceipts_FallsBackToPerReceiptStatusCommands()
    {
        var queue = new ClusterQueue
        {
            SchedulerType = ClusterScheduler.Custom,
            StatusJobTemplate = "printf RUNNING",
            JobStatusParseTemplateRunning = "RUNNING"
        };

        var observations = await queue.ObserveReceipts(["123", "456"]);

        Assert.All(observations.Values, observation =>
            Assert.Equal(BackendObservationKind.Running, observation.Kind));
    }

    [Fact]
    public async Task ObserveReceipts_FallsBackWhenBulkOutputIsInconclusive()
    {
        var queue = new ClusterQueue
        {
            SchedulerType = ClusterScheduler.Custom,
            ListJobsTemplate = "printf '123,WHAT\\n'",
            StatusJobTemplate = "printf RUNNING",
            JobStatusParseTemplateRunning = "RUNNING"
        };

        var observations = await queue.ObserveReceipts(["123"]);

        Assert.Equal(BackendObservationKind.Running, observations["123"].Kind);
    }

    [Fact]
    public async Task ObserveReceiptsFallsBackWhenBulkCommandFails()
    {
        var queue = new ClusterQueue
        {
            SchedulerType = ClusterScheduler.Custom,
            ListJobsTemplate = "exit 1",
            StatusJobTemplate = "printf RUNNING",
            JobStatusParseTemplateRunning = "RUNNING"
        };

        var observations = await queue.ObserveReceipts(["123"]);

        Assert.Equal(BackendObservationKind.Running, observations["123"].Kind);
    }

    [Fact]
    public async Task ObserveReceiptUsesTerminalViewWhenActiveCommandRejectsMissingJob()
    {
        var queue = new ClusterQueue
        {
            SchedulerType = ClusterScheduler.Custom,
            StatusJobTemplate = "printf 'not active' >&2; exit 1",
            TerminalStatusJobTemplate = "printf COMPLETED",
            JobStatusParseTemplateSucceeded = "COMPLETED"
        };

        var observation = await queue.ObserveReceipt("123");

        Assert.Equal(BackendObservationKind.Succeeded, observation.Kind);
    }

    [Fact]
    public async Task ObserveReceiptIsIndeterminateWhenBothSchedulerViewsFail()
    {
        var queue = new ClusterQueue
        {
            SchedulerType = ClusterScheduler.Custom,
            StatusJobTemplate = "exit 1",
            TerminalStatusJobTemplate = "exit 2"
        };

        var observation = await queue.ObserveReceipt("123");

        Assert.Equal(BackendObservationKind.Indeterminate, observation.Kind);
        Assert.Contains("active scheduler view failed", observation.Detail);
        Assert.Contains("terminal scheduler view failed", observation.Detail);
    }

    [Fact]
    public async Task CancelReceipts_FallsBackToPerReceiptCancellationCommands()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var queue = new ClusterQueue
        {
            AbortJobTemplate = $"printf '%s\\n' {{{{ job_id }}}} >> {path}"
        };
        try
        {
            await queue.CancelReceipts(["123", "456"]);

            Assert.Equal(
                new[] { "123", "456" }.ToHashSet(),
                File.ReadAllLines(path).ToHashSet());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData("123\n456\n", new[] { "123", "456" })]
    [InlineData("123\r\n456\r\n", new[] { "123", "456" })]
    [InlineData("  789  \n\n1011\n", new[] { "789", "1011" })]
    [InlineData("", new string[0])]
    public void ParseActiveReceipts_ParsesIdsAndHandlesLineEndingsAndBlanks(string output, string[] expectedIds)
    {
        var queue = new ClusterQueue();
        var result = queue.ParseActiveReceipts(output);
        Assert.Equal(expectedIds.ToHashSet(), result.Keys.ToHashSet());
    }

    [Fact]
    public void ParseActiveReceipts_IdOnlyLinesAreInconclusive()
    {
        var queue = new ClusterQueue();
        var result = queue.ParseActiveReceipts("123\n456\n");
        Assert.Equal(BackendObservationKind.AbsentFromActiveView, result["123"].Kind);
        Assert.Equal(BackendObservationKind.AbsentFromActiveView, result["456"].Kind);
    }

    [Theory]
    [InlineData("RUNNING", BackendObservationKind.Running)]
    [InlineData("R",       BackendObservationKind.Running)]
    [InlineData("PENDING", BackendObservationKind.Pending)]
    [InlineData("PD",      BackendObservationKind.Pending)]
    public void ParseActiveReceipts_ClassifiesStateColumn(
        string state,
        BackendObservationKind expected)
    {
        var queue = new ClusterQueue();
        var result = queue.ParseActiveReceipts($"12345 {state}\n");
        Assert.Equal(expected, result["12345"].Kind);
    }

    [Theory]
    [InlineData("12345,RUNNING", "12345", BackendObservationKind.Running)]
    [InlineData("12345,PENDING", "12345", BackendObservationKind.Pending)]
    public void ParseActiveReceipts_AcceptsCommaSeparator(
        string line,
        string id,
        BackendObservationKind expected)
    {
        var queue = new ClusterQueue();
        var result = queue.ParseActiveReceipts(line + "\n");
        Assert.Equal(expected, result[id].Kind);
    }

    [Fact]
    public void BuildWorkerScript_PreservesDollarSignsInCommand()
    {
        var queue = new ClusterQueue { SubmissionScriptTemplate = "#!/bin/bash\n{{ command }}\n" };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".sh");
        try
        {
            queue.BuildWorkerScript(
                "WarpWorker2 --worker-id \"$(hostname)-$$-0-0\" ${SCHEDULER_JOB_ID:-x}",
                new Dictionary<string, string>(), Array.Empty<string>(), path);

            var script = File.ReadAllText(path);
            Assert.Contains("$(hostname)-$$-0-0", script);
            Assert.Contains("${SCHEDULER_JOB_ID:-x}", script);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void BuildWorkerScript_ExpandsAttemptCorrelationToken()
    {
        var queue = new ClusterQueue
        {
            SubmissionScriptTemplate = "# {{ attempt_id }}\n{{ command }}\n"
        };
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".sh");
        var attemptId = Guid.NewGuid();
        try
        {
            queue.BuildWorkerScript(
                "worker",
                new Dictionary<string, string> { ["attempt_id"] = attemptId.ToString("D") },
                Array.Empty<string>(),
                path);

            Assert.Contains(attemptId.ToString("D"), File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ConfigurationSnapshotPreservesCustomVariableDefaultsInSubmissionScripts()
    {
        var queue = new ClusterQueue
        {
            SubmissionScriptTemplate = "# account={{ account }}\n{{ command }}\n",
            CustomVariables = new()
            {
                ["account"] = ("Billing account", "cryo-em"),
                ["optional"] = ("Optional setting", "")
            }
        };
        var snapshot = new ClusterQueue();
        snapshot.ReadFromJson(queue.ToJson());

        Assert.Equal(queue.CustomVariables, snapshot.CustomVariables);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".sh");
        try
        {
            snapshot.BuildWorkerScript("worker", new(), Array.Empty<string>(), path);

            Assert.Equal("# account=cryo-em\nworker\n", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
