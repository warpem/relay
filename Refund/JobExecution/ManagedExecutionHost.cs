using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;

namespace Refund.JobExecution;

public sealed class ManagedExecutionHost
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<Guid, ManagedExecution> _executions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private bool _stopping;

    public async Task<BackendStartResult> StartAsync(
        ExecutionAttemptSnapshot attempt,
        string scriptPath,
        string workingDirectory,
        string standardOutput,
        string standardError,
        IReadOnlyList<int> gpuIndices,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_stopping)
                throw new InvalidOperationException("Managed execution is shutting down.");
        }
        finally
        {
            _gate.Release();
        }

        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var control = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        var ready = new AnonymousPipeServerStream(
            PipeDirection.In,
            HandleInheritability.Inheritable);
        Process process = null;

        try
        {
            process = StartRunner(
                scriptPath,
                workingDirectory,
                standardOutput,
                standardError,
                gpuIndices,
                control.GetClientHandleAsString(),
                ready.GetClientHandleAsString());
            control.DisposeLocalCopyOfClientHandle();
            ready.DisposeLocalCopyOfClientHandle();

            using (ready)
            using (var reader = new StreamReader(ready))
            {
                string signal = await reader.ReadLineAsync(startCancellation.Token)
                    .AsTask()
                    .WaitAsync(ReadyTimeout, startCancellation.Token);
                if (signal != RelayRunner.ReadySignal)
                    throw new InvalidOperationException(
                        $"relay-runner exited before becoming ready (signal: {signal ?? "EOF"}).");
            }

            await _gate.WaitAsync(CancellationToken.None);
            try
            {
                if (_stopping)
                    throw new InvalidOperationException("Managed execution is shutting down.");
                if (_executions.ContainsKey(attempt.Id))
                    throw new InvalidOperationException(
                        $"Attempt {attempt.Id} already has a managed execution.");

                var execution = new ManagedExecution(process, control, StopTimeout);
                _executions[attempt.Id] = execution;
                process = null;
                control = null;
                return new BackendStartResult(
                    new BackendReceipt(execution.ProcessId.ToString()),
                    IsRunning: false,
                    RequiresActivation: true);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            control?.Dispose();
            if (process != null)
                await StopProcessAsync(process);
            throw;
        }
    }

    public async Task ActivateAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        if (!_executions.TryGetValue(attempt.Id, out var execution))
            throw new InvalidOperationException(
                "The managed execution is not owned by this Relay process.");

        try
        {
            await execution.ActivateAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await execution.StopAsync(CancellationToken.None);
                if (_executions.TryRemove(attempt.Id, out var removed))
                    removed.Dispose();
            }
            catch
            {
            }
            throw;
        }
    }

    public BackendObservation Observe(ExecutionAttemptSnapshot attempt)
    {
        if (!_executions.TryGetValue(attempt.Id, out var execution))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The managed execution is not owned by this Relay process.");

        if (!execution.HasExited)
            return new BackendObservation(BackendObservationKind.Running);

        _executions.TryRemove(attempt.Id, out _);
        int exitCode = execution.ExitCode;
        execution.Dispose();
        return new BackendObservation(
            exitCode == 0 ? BackendObservationKind.Succeeded : BackendObservationKind.Failed,
            $"relay-runner exited with code {exitCode}.");
    }

    public async Task<BackendObservation> CancelAsync(
        ExecutionAttemptSnapshot attempt,
        CancellationToken cancellationToken)
    {
        if (!_executions.TryGetValue(attempt.Id, out var execution))
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                "The managed execution is not owned by this Relay process.");

        try
        {
            await execution.StopAsync(cancellationToken);
            if (_executions.TryRemove(attempt.Id, out var removed))
                removed.Dispose();
            return new BackendObservation(BackendObservationKind.Canceled);
        }
        catch (Exception exception)
        {
            return new BackendObservation(
                BackendObservationKind.Indeterminate,
                $"The managed execution is still being contained: {exception.Message}");
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        KeyValuePair<Guid, ManagedExecution>[] executions;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_stopping)
            {
                _stopping = true;
                _shutdown.Cancel();
            }
            executions = _executions.ToArray();
        }
        finally
        {
            _gate.Release();
        }

        await Task.WhenAll(executions.Select(async pair =>
        {
            if (!_executions.TryGetValue(pair.Key, out var execution))
                return;

            try
            {
                await execution.StopAsync(cancellationToken);
                if (_executions.TryRemove(pair.Key, out var removed))
                    removed.Dispose();
            }
            catch
            {
            }
        }));
    }

    private static Process StartRunner(
        string scriptPath,
        string workingDirectory,
        string standardOutput,
        string standardError,
        IReadOnlyList<int> gpuIndices,
        string controlHandle,
        string readyHandle)
    {
        string entryAssembly = Assembly.GetEntryAssembly()?.Location
                               ?? throw new InvalidOperationException("Relay executable path is unavailable.");
        string processPath = Environment.ProcessPath
                             ?? throw new InvalidOperationException("Relay process path is unavailable.");

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
        Add(info, "control-handle", controlHandle);
        Add(info, "ready-handle", readyHandle);
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

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None)
                .WaitAsync(StopTimeout, CancellationToken.None);
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private sealed class ManagedExecution : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _control;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly TimeSpan _stopTimeout;
        private bool _activated;
        private bool _stopping;

        public ManagedExecution(
            Process process,
            Stream control,
            TimeSpan stopTimeout)
        {
            _process = process;
            _control = new StreamWriter(control) { AutoFlush = true };
            _stopTimeout = stopTimeout;
        }

        public int ProcessId => _process.Id;
        public bool HasExited => _process.HasExited;
        public int ExitCode => _process.ExitCode;

        public async Task ActivateAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_stopping)
                    throw new InvalidOperationException("The managed execution is stopping.");
                if (_activated)
                    return;

                await _control.WriteLineAsync(RelayRunner.GoSignal.AsMemory(), cancellationToken);
                await _control.FlushAsync(cancellationToken);
                _activated = true;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_stopping)
                {
                    _stopping = true;
                    try
                    {
                        await _control.WriteLineAsync(
                            RelayRunner.StopSignal.AsMemory(),
                            cancellationToken);
                        await _control.FlushAsync(cancellationToken);
                    }
                    catch
                    {
                    }
                    _control.Dispose();
                }
            }
            finally
            {
                _gate.Release();
            }

            try
            {
                await _process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(_stopTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                TryKill();
                try
                {
                    await _process.WaitForExitAsync(CancellationToken.None)
                        .WaitAsync(_stopTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    throw new TimeoutException(
                        $"relay-runner {_process.Id} did not exit after forced termination.");
                }
            }
        }

        public void Dispose()
        {
            _control.Dispose();
            _process.Dispose();
            _gate.Dispose();
        }

        private void TryKill()
        {
            try
            {
                if (!_process.HasExited)
                    _process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
        }
    }
}
