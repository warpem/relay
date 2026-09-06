using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

namespace Refund.JobExecution;

public sealed class ManagedExecutionHost
{
    private readonly ConcurrentDictionary<Guid, Process> _processes = new();

    public BackendStartResult Start(
        ExecutionAttemptSnapshot attempt,
        string scriptPath,
        string workingDirectory,
        string standardOutput,
        string standardError,
        IReadOnlyList<int> gpuIndices)
    {
        var process = StartRunner(
            scriptPath, workingDirectory, standardOutput, standardError, gpuIndices);
        if (!_processes.TryAdd(attempt.Id, process))
        {
            TryKill(process);
            process.Dispose();
            throw new InvalidOperationException($"Attempt {attempt.Id} already has a managed process.");
        }

        return new BackendStartResult(new BackendReceipt(process.Id.ToString()), true);
    }

    public BackendObservation Observe(ExecutionAttemptSnapshot attempt)
    {
        if (!_processes.TryGetValue(attempt.Id, out var process))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The managed process is not owned by this Relay process.");

        if (!process.HasExited)
            return new BackendObservation(BackendObservationKind.Running);

        _processes.TryRemove(attempt.Id, out _);
        int exitCode = process.ExitCode;
        process.Dispose();
        return new BackendObservation(
            exitCode == 0 ? BackendObservationKind.Succeeded : BackendObservationKind.Failed,
            $"relay-runner exited with code {exitCode}.");
    }

    public async Task<BackendObservation> CancelAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        if (!_processes.TryGetValue(attempt.Id, out var process))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The managed process is not owned by this Relay process.");

        TryKill(process);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            _processes.TryRemove(attempt.Id, out _);
            process.Dispose();
        }

        return new BackendObservation(BackendObservationKind.Canceled);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        var processes = _processes.ToArray();
        foreach (var (_, process) in processes)
            TryKill(process);

        foreach (var (attemptId, process) in processes)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
            }
            finally
            {
                _processes.TryRemove(attemptId, out _);
                process.Dispose();
            }
        }
    }

    private static Process StartRunner(
        string scriptPath,
        string workingDirectory,
        string standardOutput,
        string standardError,
        IReadOnlyList<int> gpuIndices)
    {
        string entryAssembly = Assembly.GetEntryAssembly()?.Location
                               ?? throw new InvalidOperationException("Relay executable path is unavailable.");
        string processPath = Environment.ProcessPath
                             ?? throw new InvalidOperationException("Relay process path is unavailable.");
        using var current = Process.GetCurrentProcess();

        var info = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };

        if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(entryAssembly);

        info.ArgumentList.Add(RelayRunner.Command);
        Add(info, "parent-pid", current.Id.ToString());
        Add(info, "parent-start-ticks", current.StartTime.ToUniversalTime().Ticks.ToString());
        Add(info, "script", scriptPath);
        Add(info, "working-directory", workingDirectory);
        Add(info, "stdout", standardOutput);
        Add(info, "stderr", standardError);
        Add(info, "gpus", string.Join(",", gpuIndices));

        var process = new Process { StartInfo = info };
        process.Start();
        return process;
    }

    private static void Add(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add($"--{name}");
        info.ArgumentList.Add(value);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
