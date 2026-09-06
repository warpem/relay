using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Refund.JobExecution;

internal sealed class SupervisedProcess : IDisposable
{
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(10);
    private static readonly string SetsidPath =
        new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);

    private readonly Process _process;
    private readonly Task _outputPumps;
    private readonly bool _ownsProcessGroup;
    private int _confirmedProcessGroup;
    private long _exitObservedAt;

    private SupervisedProcess(Process process, Task outputPumps, bool ownsProcessGroup)
    {
        _process = process;
        _outputPumps = outputPumps;
        _ownsProcessGroup = ownsProcessGroup;
    }

    public int ExitCode => _process.ExitCode;

    public bool HasExited
    {
        get
        {
            if (!_process.HasExited)
                return false;
            if (_outputPumps.IsCompleted)
                return true;

            Interlocked.CompareExchange(ref _exitObservedAt, Stopwatch.GetTimestamp(), 0);
            return Stopwatch.GetElapsedTime(Interlocked.Read(ref _exitObservedAt)) > DrainGrace;
        }
    }

    public static SupervisedProcess Start(
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
        info.ArgumentList.Add(scriptPath);
        info.Environment["CUDA_VISIBLE_DEVICES"] = string.Join(",", gpuIndices);

        foreach (var key in info.Environment.Keys
                     .Where(key => key.StartsWith("ASPNETCORE_") || key.StartsWith("Kestrel__"))
                     .ToArray())
            info.Environment.Remove(key);

        var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, standardOutputPath),
            PumpAsync(process.StandardError, standardErrorPath));
        return new SupervisedProcess(process, pumps, ownsProcessGroup);
    }

    public void KillTree()
    {
        int? processGroup = ProcessGroup;
        if (processGroup is { } group && group > 1 && Kill(-group, SigKill) == 0)
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

    public void Dispose()
    {
        _process.Dispose();
    }

    private int? ProcessGroup
    {
        get
        {
            int confirmed = Volatile.Read(ref _confirmedProcessGroup);
            if (confirmed != 0)
                return confirmed;
            if (!_ownsProcessGroup || _process.HasExited)
                return null;

            try
            {
                int group = GetProcessGroup(_process.Id);
                if (group == _process.Id && group > 1)
                {
                    Volatile.Write(ref _confirmedProcessGroup, group);
                    return group;
                }
            }
            catch
            {
            }

            return null;
        }
    }

    private static async Task PumpAsync(StreamReader reader, string path)
    {
        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            await using var writer = new StreamWriter(path, append: false) { AutoFlush = true };
            while (await reader.ReadLineAsync() is { } line)
                await writer.WriteLineAsync(line);
        }
        catch (Exception exception)
        {
            Log.ForContext<SupervisedProcess>().Warning(
                exception,
                "Stopped writing managed job output to {Path}",
                path);
        }
    }

    private const int SigKill = 9;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int signal);

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetProcessGroup(int pid);
}
