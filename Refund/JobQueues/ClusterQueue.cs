using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobExecution;
using Refund.JobQueues.ReadOnly;
using Refund.Utils;
using Serilog;

namespace Refund.JobQueues;

public sealed class ClusterQueue : JobQueue
{
    private static readonly ConditionalWeakTable<ClusterQueue, ReadOnlyClusterQueue> ReadOnlyCache = new();
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ClusterCommandGates = new();

    [RelayProperty]
    public ClusterScheduler SchedulerType { get; set; } = ClusterScheduler.Slurm;

    [RelayProperty]
    public int ManagedCores { get; set; } = Environment.ProcessorCount;

    [RelayProperty]
    public int ManagedMemoryGb { get; set; } = 64;

    [RelayProperty]
    public int ManagedGpus { get; set; } = 1;

    public bool IsManaged => SchedulerType == ClusterScheduler.Managed;

    public string ManagedDisabledReason { get; internal set; }

    [RelayProperty]
    public string CustomShell { get; set; } = "";

    [RelayProperty]
    public string CustomShellArguments { get; set; } = "";

    [RelayProperty]
    public string SendCommmandTemplate { get; set; } = "";

    [RelayProperty]
    public string SubmitJobTemplate { get; set; } = "";

    [RelayProperty]
    public string StatusJobTemplate { get; set; } = "";

    [RelayProperty]
    public string TerminalStatusJobTemplate { get; set; } = "";

    [RelayProperty]
    public string AbortJobTemplate { get; set; } = "";

    [RelayProperty]
    public string JobIdParseRegex { get; set; } = "";

    [RelayProperty]
    public string JobStatusParseTemplatePending { get; set; } = "";

    [RelayProperty]
    public string JobStatusParseTemplateRunning { get; set; } = "";

    [RelayProperty]
    public string JobStatusParseTemplateFailed { get; set; } = "";

    [RelayProperty]
    public string JobStatusParseTemplateSucceeded { get; set; } = "";

    [RelayProperty]
    public string JobStatusParseTemplateCanceled { get; set; } = "";

    [RelayProperty]
    public string SubmissionScriptTemplate { get; set; } = "";

    [RelayProperty]
    public string ListJobsTemplate { get; set; } = "";

    [RelayProperty]
    public string CancelManyJobsTemplate { get; set; } = "";

    public Dictionary<string, (string description, string defaultValue)> CustomVariables { get; set; } = new();

    public ClusterQueue()
    {
    }

    internal async Task<string> PrepareAndWriteScript(
        Job job,
        Guid attemptId,
        Dictionary<string, string> customValues = null)
    {
        job.ResetWorkingDirectory();
        Directory.CreateDirectory(job.RelayResultsDirectoryPath);
        await job.WriteToLifecycleLog("Preparation started");
        job.Stage();

        string scriptPath = Path.Combine(job.DirectoryPath, "submit.sh");
        var command = new StringBuilder();
        command.AppendLine($"cd {job.RunDirectory}\n");
        command.Append(job.CommandPrefix);
        command.Append($"{job.CommandName} {JobTools.ComposeArgumentString(job.ComposeCommandArguments())}");
        command.AppendLine(job.CommandSuffix);

        string script = ProcessSubmissionScript(
            SubmissionScriptTemplate
                .ReplaceRegex("{{\\s*command\\s*}}", command.ToString())
                .ReplaceRegex("{{\\s*job_id\\s*}}", job.Id.ToString())
                .ReplaceRegex("{{\\s*attempt_id\\s*}}", attemptId.ToString("D")),
            job.GetResourceValues(),
            job.RequiredModules,
            customValues);

        await File.WriteAllTextAsync(scriptPath, script);
        await job.WriteToLifecycleLog(
            $"Written following submission script to {scriptPath}:\n\n{script}\n\n");
        return scriptPath;
    }

    public async Task<string> SubmitScript(
        string scriptPath,
        Action<string> onRawOutput = null)
    {
        if (IsManaged)
            throw new InvalidOperationException("Managed queues launch scripts through relay-runner.");
        if (string.IsNullOrWhiteSpace(SubmitJobTemplate))
            throw new InvalidOperationException($"Queue \"{Alias}\" has no submission command.");

        string command = SubmitJobTemplate.ReplaceRegex(
            "{{\\s*script_path_abs\\s*}}",
            scriptPath);
        string output = await ExecuteOnCluster(command);
        onRawOutput?.Invoke(output);
        return ParseClusterJobId(output);
    }

    internal void ValidateSubmissionConfiguration()
    {
        if (string.IsNullOrWhiteSpace(SubmissionScriptTemplate))
            throw new InvalidOperationException(
                $"Queue \"{Alias}\" has no submission script template.");
        if (IsManaged)
            return;
        if (string.IsNullOrWhiteSpace(SubmitJobTemplate))
            throw new InvalidOperationException($"Queue \"{Alias}\" has no submission command.");
        if (string.IsNullOrWhiteSpace(StatusJobTemplate))
            throw new InvalidOperationException($"Queue \"{Alias}\" has no status command.");
        if (string.IsNullOrWhiteSpace(AbortJobTemplate))
            throw new InvalidOperationException($"Queue \"{Alias}\" has no cancellation command.");
        if (SchedulerType == ClusterScheduler.Custom)
        {
            if (string.IsNullOrWhiteSpace(JobIdParseRegex))
                throw new InvalidOperationException(
                    $"Queue \"{Alias}\" has no scheduler receipt pattern.");
            var expression = new Regex(JobIdParseRegex);
            if (expression.GetGroupNumbers().Length < 2)
                throw new InvalidOperationException(
                    $"Queue \"{Alias}\" scheduler receipt pattern needs a capture group.");
            if (new[]
                {
                    JobStatusParseTemplatePending,
                    JobStatusParseTemplateRunning,
                    JobStatusParseTemplateFailed,
                    JobStatusParseTemplateSucceeded,
                    JobStatusParseTemplateCanceled
                }.All(string.IsNullOrWhiteSpace))
                throw new InvalidOperationException(
                    $"Queue \"{Alias}\" has no scheduler status patterns.");
        }
    }

    public async Task<BackendObservation> ObserveReceipt(string receiptId)
    {
        if (IsManaged)
            throw new InvalidOperationException("Managed execution is observed by relay-runner.");
        if (string.IsNullOrWhiteSpace(receiptId))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The scheduler receipt is empty.");
        if (string.IsNullOrWhiteSpace(StatusJobTemplate))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The queue has no active-status command configured.");

        BackendObservation active;
        string activeFailure = null;
        try
        {
            active = ParseBackendObservation(await ExecuteOnCluster(
                ReplaceJobId(StatusJobTemplate, receiptId)));
        }
        catch (Exception exception)
        {
            activeFailure = exception.Message;
            active = new BackendObservation(
                BackendObservationKind.Unreachable,
                activeFailure);
        }

        if (active.Kind is not (BackendObservationKind.AbsentFromActiveView or
            BackendObservationKind.Unparseable or BackendObservationKind.Indeterminate or
            BackendObservationKind.Unreachable))
            return active;

        string terminalTemplate = string.IsNullOrWhiteSpace(TerminalStatusJobTemplate)
            ? ClusterSchedulerProtocol.DefaultTerminalStatusTemplate(SchedulerType)
            : TerminalStatusJobTemplate;
        if (string.IsNullOrWhiteSpace(terminalTemplate))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The active scheduler view was inconclusive and no terminal-status command is configured.");

        BackendObservation terminal;
        try
        {
            terminal = ParseBackendObservation(await ExecuteOnCluster(
                ReplaceJobId(terminalTemplate, receiptId)));
        }
        catch (Exception exception)
        {
            string detail = activeFailure == null
                ? $"The terminal scheduler view failed: {exception.Message}"
                : $"The active scheduler view failed: {activeFailure}\n" +
                  $"The terminal scheduler view failed: {exception.Message}";
            return new BackendObservation(BackendObservationKind.Indeterminate, detail);
        }

        return terminal.Kind is BackendObservationKind.AbsentFromActiveView or
            BackendObservationKind.Unparseable
            ? new BackendObservation(
                BackendObservationKind.Indeterminate,
                activeFailure == null
                    ? "The job could not be resolved from the scheduler's active or terminal views."
                    : $"The active scheduler view failed: {activeFailure}\n" +
                      "The terminal scheduler view did not contain a recognized state.")
            : terminal;
    }

    public async Task CancelReceipt(string receiptId)
    {
        if (IsManaged)
            throw new InvalidOperationException("Managed execution is canceled through relay-runner.");
        if (string.IsNullOrWhiteSpace(receiptId))
            throw new ArgumentException("The scheduler receipt is empty.", nameof(receiptId));
        if (string.IsNullOrWhiteSpace(AbortJobTemplate))
            throw new InvalidOperationException($"Queue \"{Alias}\" has no cancellation command.");

        await ExecuteOnCluster(ReplaceJobId(AbortJobTemplate, receiptId));
    }

    internal BackendObservation ParseBackendObservation(string output) =>
        ClusterSchedulerProtocol.ParseObservation(this, output);

    public string BuildWorkerScript(
        string command,
        Dictionary<string, string> resourceValues,
        string[] requiredModules,
        string scriptPath)
    {
        string script = ProcessSubmissionScript(
            SubmissionScriptTemplate.ReplaceRegex("{{\\s*command\\s*}}", command),
            resourceValues,
            requiredModules);

        string directory = Path.GetDirectoryName(scriptPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private string ProcessSubmissionScript(
        string scriptTemplate,
        Dictionary<string, string> resourceValues,
        string[] requiredModules,
        Dictionary<string, string> customValues = null)
    {
        string result = scriptTemplate;

        foreach (var (name, value) in resourceValues)
        {
            string pattern = $"{{{{\\s*{Regex.Escape(name)}\\s*}}}}";
            result = Regex.Replace(result, pattern, _ => value);
        }

        foreach (var (name, definition) in CustomVariables)
        {
            string pattern = $"{{{{\\s*{Regex.Escape(name)}\\s*}}}}";
            string value = customValues?.GetValueOrDefault(name) ?? definition.defaultValue ?? "";
            result = Regex.Replace(result, pattern, _ => value);
        }

        var blocks = new List<(int Start, int End, string Module)>();
        int position = 0;
        while (position < result.Length)
        {
            int blockStart = result.IndexOf("{{", position, StringComparison.Ordinal);
            if (blockStart < 0)
                break;
            int blockEnd = result.IndexOf("}}", blockStart, StringComparison.Ordinal);
            if (blockEnd < 0)
                break;

            string module = result[(blockStart + 2)..blockEnd].Trim();
            if (module.StartsWith('/'))
            {
                string closingModule = module[1..];
                int openIndex = blocks.FindLastIndex(block => block.Module == closingModule);
                if (openIndex >= 0)
                {
                    var open = blocks[openIndex];
                    if (requiredModules.Contains(closingModule))
                    {
                        result = result.Remove(blockStart, blockEnd - blockStart + 2);
                        result = result.Remove(open.Start, open.End - open.Start + 2);
                        position = blockStart - (open.End - open.Start + 2);
                    }
                    else
                    {
                        result = result.Remove(open.Start, blockEnd - open.Start + 2);
                        position = open.Start;
                    }

                    blocks.RemoveRange(openIndex, blocks.Count - openIndex);
                    continue;
                }
            }
            else
            {
                blocks.Add((blockStart, blockEnd, module));
            }

            position = blockEnd + 2;
        }

        foreach (var block in blocks.OrderByDescending(block => block.Start))
            result = result.Remove(block.Start, block.End - block.Start + 2);

        return result;
    }

    public async Task<IReadOnlyDictionary<string, BackendObservation>> ObserveReceipts(
        IEnumerable<string> receiptIds)
    {
        string[] ids = receiptIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return new Dictionary<string, BackendObservation>();

        if (string.IsNullOrWhiteSpace(ListJobsTemplate))
            return await ObserveReceiptsIndividually(ids);

        string output;
        try
        {
            output = await ExecuteOnCluster(ListJobsTemplate);
        }
        catch
        {
            return await ObserveReceiptsIndividually(ids);
        }

        var active = ParseActiveReceipts(output);
        var observations = new Dictionary<string, BackendObservation>(ids.Length);
        foreach (string id in ids)
        {
            if (active.TryGetValue(id, out var observation) && observation.Kind is
                BackendObservationKind.Pending or
                BackendObservationKind.Running or
                BackendObservationKind.Succeeded or
                BackendObservationKind.Failed or
                BackendObservationKind.Canceled)
                observations[id] = observation;
            else
                observations[id] = await ObserveReceipt(id);
        }

        return observations;
    }

    internal Dictionary<string, BackendObservation> ParseActiveReceipts(string output)
    {
        var result = new Dictionary<string, BackendObservation>();
        foreach (var line in output.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tokens = line.Split(
                new[] { ' ', '\t', ',' },
                StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            string state = tokens.Length > 1 ? string.Join(' ', tokens.Skip(1)) : "";
            result[tokens[0]] = ParseBackendObservation(state);
        }

        return result;
    }

    public async Task CancelReceipts(IEnumerable<string> receiptIds)
    {
        string[] ids = receiptIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
            return;

        if (string.IsNullOrWhiteSpace(CancelManyJobsTemplate))
        {
            await Task.WhenAll(ids.Select(CancelReceipt));
            return;
        }

        string command = CancelManyJobsTemplate.ReplaceRegex(
            "{{\\s*job_ids\\s*}}",
            string.Join(" ", ids));
        await ExecuteOnCluster(command);
    }

    internal string ParseClusterJobId(string output) =>
        ClusterSchedulerProtocol.ParseReceipt(this, output);

    public override void ReadFromJson(JsonNode reader)
    {
        base.ReadFromJson(reader);
        if (reader["customVariables"] != null)
            CustomVariables = JsonSerializer.Deserialize<
                Dictionary<string, (string description, string defaultValue)>>(
                reader["customVariables"].ToJsonString()) ?? new();
    }

    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);
        writer["customVariables"] = JsonSerializer.SerializeToNode(CustomVariables);
    }

    public override ReadOnlyJobQueue AsReadOnly() =>
        ReadOnlyCache.GetValue(this, queue => new ReadOnlyClusterQueue(queue));

    private async Task<string> ExecuteOnCluster(string command)
    {
        var commandGate = ClusterCommandGates.GetOrAdd(
            Id,
            _ => new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount)));
        if (!await commandGate.WaitAsync(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("Timed out waiting to run a cluster command.");

        try
        {
            return await ExecuteClusterCommand(command);
        }
        finally
        {
            commandGate.Release();
        }
    }

    private async Task<string> ExecuteClusterCommand(string command)
    {
        string fullCommand = string.IsNullOrWhiteSpace(SendCommmandTemplate)
            ? command
            : SendCommmandTemplate.ReplaceRegex("{{\\s*command\\s*}}", command);
        using var process = new Process();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        if (!string.IsNullOrEmpty(CustomShell))
        {
            process.StartInfo.FileName = CustomShell;
            process.StartInfo.Arguments = CustomShellArguments.ReplaceRegex(
                "{{\\s*command\\s*}}",
                fullCommand);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            process.StartInfo.FileName = "cmd.exe";
            process.StartInfo.Arguments = $"/c {fullCommand}";
        }
        else
        {
            process.StartInfo.FileName = "/bin/bash";
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add(fullCommand);
        }

        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.WorkingDirectory = Path.GetTempPath();

        foreach (var key in process.StartInfo.Environment.Keys
                     .Where(key => key.StartsWith("ASPNETCORE_") || key.StartsWith("Kestrel__"))
                     .ToArray())
            process.StartInfo.Environment.Remove(key);

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"Cluster command timed out after 2 minutes: {command}");
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Cluster command exited with code {process.ExitCode}: {command}\n{error}");

        Log.ForContext<ClusterQueue>().Debug(
            "Cluster command completed for queue {QueueId}: {Command}",
            Id,
            command);
        return output;
    }

    private static string ReplaceJobId(string template, string receiptId) =>
        template.ReplaceRegex("{{\\s*job_id\\s*}}", receiptId);

    private async Task<IReadOnlyDictionary<string, BackendObservation>> ObserveReceiptsIndividually(
        IEnumerable<string> receiptIds)
    {
        var pairs = await Task.WhenAll(receiptIds.Select(async id =>
            new KeyValuePair<string, BackendObservation>(id, await ObserveReceipt(id))));
        return pairs.ToDictionary(pair => pair.Key, pair => pair.Value);
    }
}
