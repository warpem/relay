using System.Diagnostics;
using System.IO.Pipes;
using Refund.JobExecution;
using Relay.Runner;

namespace Refund.Tests.JobExecution;

public sealed class ManagedRunnerProcessTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ManagedHostLaunchesThePackagedRunnerFromTheTestHost()
    {
        string directory = CreateDirectory();
        var host = new ManagedExecutionHost();
        try
        {
            string marker = Path.Combine(directory, "started");
            string script = Path.Combine(directory, "payload.sh");
            string stdout = Path.Combine(directory, "stdout");
            await File.WriteAllTextAsync(script,
                $"touch '{marker}'\nprintf '%s' \"$CUDA_VISIBLE_DEVICES\"\nexit 7\n");
            var coordinator = new ExecutionCoordinator([
                new ExecutionQueuePolicy(1, ExecutionBackendKind.Managed, new ResourceVector(2, 4, 0))
            ]);
            var attempt = coordinator.RequestRun(new JobAddress(1, 1, 1), 1,
                ResourceVector.None, true).CreateSnapshot();

            var started = await host.StartAsync(attempt, script, directory, stdout,
                Path.Combine(directory, "stderr"), [2, 5], CancellationToken.None).WaitAsync(Timeout);
            Assert.False(started.IsRunning);
            Assert.False(File.Exists(marker));
            await host.ActivateAsync(attempt, CancellationToken.None).WaitAsync(Timeout);

            BackendObservation observation;
            using var timeout = new CancellationTokenSource(Timeout);
            while ((observation = host.Observe(attempt)).Kind == BackendObservationKind.Running)
                await Task.Delay(10, timeout.Token);

            Assert.Equal(BackendObservationKind.Failed, observation.Kind);
            Assert.Contains("code 7", observation.Detail);
            Assert.True(File.Exists(marker));
            Assert.Equal("2,5\n", await File.ReadAllTextAsync(stdout));
        }
        finally
        {
            await host.ShutdownAsync(CancellationToken.None).WaitAsync(Timeout);
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false, 125)]
    [InlineData(true, 137)]
    public async Task StandaloneRunnerCleansUpWhenTheOwnerPipeCloses(bool activate, int expectedExit)
    {
        string directory = CreateDirectory();
        using var control = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        using var ready = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var runner = new Process();
        int payloadPid = 0;
        bool launched = false;
        try
        {
            string pidFile = Path.Combine(directory, "payload.pid");
            string script = Path.Combine(directory, "payload.sh");
            await File.WriteAllTextAsync(script, $"echo $$ > '{pidFile}'\nexec sleep 30\n");
            runner.StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory,
                    OperatingSystem.IsWindows() ? "Relay.Runner.exe" : "Relay.Runner"),
                UseShellExecute = false,
                ArgumentList =
                {
                    "--control-handle", control.GetClientHandleAsString(),
                    "--ready-handle", ready.GetClientHandleAsString(),
                    "--script", script,
                    "--working-directory", directory,
                    "--stdout", Path.Combine(directory, "stdout"),
                    "--stderr", Path.Combine(directory, "stderr")
                }
            };
            launched = runner.Start();
            control.DisposeLocalCopyOfClientHandle();
            ready.DisposeLocalCopyOfClientHandle();
            using var reader = new StreamReader(ready);
            RunnerProtocol.ParseReadySignal(await reader.ReadLineAsync().WaitAsync(Timeout));
            Assert.False(File.Exists(pidFile));

            if (activate)
            {
                using var writer = new StreamWriter(control, leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(RunnerProtocol.GoSignal);
                using var timeout = new CancellationTokenSource(Timeout);
                while (!File.Exists(pidFile) || new FileInfo(pidFile).Length == 0)
                    await Task.Delay(10, timeout.Token);
                payloadPid = int.Parse(await File.ReadAllTextAsync(pidFile));
            }

            // No STOP command: closing the owner endpoint gives the same EOF as owner death.
            control.Dispose();
            await runner.WaitForExitAsync().WaitAsync(Timeout);

            Assert.Equal(expectedExit, runner.ExitCode);
            Assert.Equal(activate, File.Exists(pidFile));
            Assert.False(IsAlive(payloadPid));
        }
        finally
        {
            if (launched && !runner.HasExited)
            {
                runner.Kill(entireProcessTree: true);
                await runner.WaitForExitAsync().WaitAsync(Timeout);
            }
            if (IsAlive(payloadPid))
                Process.GetProcessById(payloadPid).Kill();
            Directory.Delete(directory, true);
        }
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string CreateDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "relay-runner-process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        return directory;
    }
}
