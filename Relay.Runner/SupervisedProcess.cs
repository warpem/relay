using System.Diagnostics;

namespace Relay.Runner;

internal sealed class SupervisedProcess : IDisposable
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ContainmentGrace = TimeSpan.FromSeconds(5);
    private static readonly string InstalledSetsidPath =
        new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);

    private readonly Process _process;
    private readonly Task _outputPumps;
    private readonly int? _processGroup;

    internal static string SetsidPathOverride { get; set; }

    private static string SetsidPath => SetsidPathOverride ?? InstalledSetsidPath;

    private SupervisedProcess(Process process, Task outputPumps, int? processGroup)
    {
        _process = process;
        _outputPumps = outputPumps;
        _processGroup = processGroup;
    }

    public int ExitCode => _process.ExitCode;
    public int? ProcessGroup => _processGroup;

    public static SupervisedProcess Prepare(
        string scriptPath,
        string workingDirectory,
        IReadOnlyList<int> gpuIndices,
        string standardOutputPath,
        string standardErrorPath)
    {
        bool ownsProcessGroup = SetsidPath != null;
        var info = new ProcessStartInfo
        {
            FileName = ownsProcessGroup ? SetsidPath : "/bin/bash",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (ownsProcessGroup)
            info.ArgumentList.Add("/bin/bash");
        // Keep the shell alive until its group has been identified and Relay has
        // persisted the runner receipt. EOF also makes an abandoned shell exit.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("read -r start && [ \"$start\" = GO ] && exec /bin/bash \"$1\"");
        info.ArgumentList.Add("relay-payload");
        info.ArgumentList.Add(scriptPath);
        info.Environment["CUDA_VISIBLE_DEVICES"] = string.Join(",", gpuIndices);

        foreach (var key in info.Environment.Keys
                     .Where(key => key.StartsWith("ASPNETCORE_") || key.StartsWith("Kestrel__"))
                     .ToArray())
            info.Environment.Remove(key);

        var process = new Process { StartInfo = info };
        process.Start();

        int? processGroup = ownsProcessGroup
            ? ConfirmProcessGroup(process, TimeSpan.FromSeconds(5))
            : null;
        if (ownsProcessGroup && processGroup == null)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }
            process.Dispose();
            throw new InvalidOperationException(
                "The managed payload did not enter its private process group.");
        }

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, standardOutputPath),
            PumpAsync(process.StandardError, standardErrorPath));
        return new SupervisedProcess(process, pumps, processGroup);
    }

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync("GO".AsMemory(), cancellationToken);
        _process.StandardInput.Close();
    }

    public void KillTree()
    {
        if (global::Relay.Runner.ProcessGroup.Kill(_processGroup))
            return;

        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    public Task WaitForProcessExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);
        try
        {
            await _outputPumps.WaitAsync(DrainGrace, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }

    public async Task<bool> WaitForContainmentAsync(CancellationToken cancellationToken)
    {
        await WaitForExitAsync(cancellationToken);
        if (_processGroup == null)
            return true;

        var started = Stopwatch.StartNew();
        while (started.Elapsed < ContainmentGrace)
        {
            if (global::Relay.Runner.ProcessGroup.IsEmpty(_processGroup.Value))
                return true;
            await Task.Delay(25, cancellationToken);
        }

        return global::Relay.Runner.ProcessGroup.IsEmpty(_processGroup.Value);
    }

    public void Dispose()
    {
        _process.Dispose();
    }

    private static int? ConfirmProcessGroup(Process process, TimeSpan timeout)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < timeout)
        {
            if (process.HasExited)
                return null;

            try
            {
                int group = global::Relay.Runner.ProcessGroup.Get(process.Id);
                if (group == process.Id && group > 1)
                    return group;
            }
            catch
            {
            }

            Thread.Sleep(1);
        }

        return null;
    }

    private static async Task PumpAsync(StreamReader reader, string path)
    {
        StreamWriter writer = null;
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            writer = new StreamWriter(path, append: false) { AutoFlush = true };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not open managed job output at {path}; " +
                                    $"output will be discarded: {exception.Message}");
        }

        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (writer == null)
                    continue;

                try
                {
                    await writer.WriteLineAsync(line);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"Stopped writing managed job output to {path}; " +
                                            $"output will be discarded: {exception.Message}");
                    await writer.DisposeAsync();
                    writer = null;
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Stopped reading managed job output for {path}: {exception.Message}");
        }
        finally
        {
            if (writer != null)
                await writer.DisposeAsync();
        }
    }

}
