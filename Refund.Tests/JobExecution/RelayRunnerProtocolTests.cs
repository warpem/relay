using System.IO.Pipes;
using System.Diagnostics;
using Refund.JobExecution;

namespace Refund.Tests.JobExecution;

public sealed class RelayRunnerProtocolTests
{
    [Fact]
    public async Task PayloadStartsOnlyAfterGoAndReturnsItsExitCode()
    {
        string directory = CreateDirectory();
        try
        {
            string marker = Path.Combine(directory, "started");
            string script = Path.Combine(directory, "payload.sh");
            string stdout = Path.Combine(directory, "stdout.txt");
            string stderr = Path.Combine(directory, "stderr.txt");
            await File.WriteAllTextAsync(
                script,
                $"touch '{marker}'\necho ready\necho problem >&2\nexit 7\n");

            await using var protocol = await RunnerProtocol.StartAsync(
                Options(script, directory, stdout, stderr));

            RelayRunner.ParseReadySignal(await protocol.Ready.ReadLineAsync());
            Assert.False(File.Exists(marker));

            await protocol.Control.WriteLineAsync(RelayRunner.GoSignal);

            Assert.Equal(7, await protocol.Result.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("ready\n", await File.ReadAllTextAsync(stdout));
            Assert.Equal("problem\n", await File.ReadAllTextAsync(stderr));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ClosingTheControlChannelStopsThePayload()
    {
        string directory = CreateDirectory();
        try
        {
            string marker = Path.Combine(directory, "started");
            string script = Path.Combine(directory, "payload.sh");
            await File.WriteAllTextAsync(script, $"touch '{marker}'\nsleep 30\n");
            await using var protocol = await RunnerProtocol.StartAsync(Options(
                script,
                directory,
                Path.Combine(directory, "stdout.txt"),
                Path.Combine(directory, "stderr.txt")));
            RelayRunner.ParseReadySignal(await protocol.Ready.ReadLineAsync());

            await protocol.Control.WriteLineAsync(RelayRunner.GoSignal);
            await WaitUntilAsync(() => File.Exists(marker));
            protocol.CloseControl();

            Assert.Equal(137, await protocol.Result.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CleanPayloadExitKillsRemainingProcessGroup()
    {
        string directory = CreateDirectory();
        string setsid = CreateSetsidStandIn(directory);
        int childPid = 0;
        string previous = SupervisedProcess.SetsidPathOverride;
        SupervisedProcess.SetsidPathOverride = setsid;
        try
        {
            string pidFile = Path.Combine(directory, "child.pid");
            string script = Path.Combine(directory, "payload.sh");
            await File.WriteAllTextAsync(
                script,
                $"sleep 30 >/dev/null 2>&1 & echo $! > '{pidFile}'\n");
            await using var protocol = await RunnerProtocol.StartAsync(Options(
                script,
                directory,
                Path.Combine(directory, "stdout.txt"),
                Path.Combine(directory, "stderr.txt")));
            RelayRunner.ParseReadySignal(await protocol.Ready.ReadLineAsync());
            await protocol.Control.WriteLineAsync(RelayRunner.GoSignal);
            await WaitUntilAsync(() => File.Exists(pidFile), TimeSpan.FromSeconds(10));
            childPid = int.Parse(await File.ReadAllTextAsync(pidFile));

            Assert.Equal(0, await protocol.Result.WaitAsync(TimeSpan.FromSeconds(10)));
            await WaitUntilAsync(() => !IsAlive(childPid), TimeSpan.FromSeconds(10));
        }
        finally
        {
            SupervisedProcess.SetsidPathOverride = previous;
            KillIfAlive(childPid);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ClosingControlBeforeGoLeavesNoPayload()
    {
        string directory = CreateDirectory();
        string previous = SupervisedProcess.SetsidPathOverride;
        SupervisedProcess.SetsidPathOverride = CreateSetsidStandIn(directory);
        try
        {
            string marker = Path.Combine(directory, "started");
            string script = Path.Combine(directory, "payload.sh");
            await File.WriteAllTextAsync(script, $"touch '{marker}'\n");
            await using var protocol = await RunnerProtocol.StartAsync(Options(
                script, directory,
                Path.Combine(directory, "stdout.txt"),
                Path.Combine(directory, "stderr.txt")));
            int? processGroup = RelayRunner.ParseReadySignal(await protocol.Ready.ReadLineAsync());
            Assert.NotNull(processGroup);
            Assert.False(File.Exists(marker));

            protocol.CloseControl();

            Assert.Equal(125, await protocol.Result.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(SupervisedProcess.ProcessGroupIsEmpty(processGroup.Value));
            Assert.False(File.Exists(marker));
        }
        finally
        {
            SupervisedProcess.SetsidPathOverride = previous;
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunnerDeathDoesNotReleaseItsLivePayloadGroup()
    {
        string directory = CreateDirectory();
        string previous = SupervisedProcess.SetsidPathOverride;
        SupervisedProcess.SetsidPathOverride = CreateSetsidStandIn(directory);
        int payloadPid = 0;
        try
        {
            string pidFile = Path.Combine(directory, "payload.pid");
            string script = Path.Combine(directory, "payload.sh");
            await File.WriteAllTextAsync(script, $"echo $$ > '{pidFile}'\nexec sleep 30\n");
            using var payload = SupervisedProcess.Prepare(
                script, directory, [],
                Path.Combine(directory, "stdout.txt"),
                Path.Combine(directory, "stderr.txt"));
            Assert.NotNull(payload.ProcessGroup);

            // An independent process stands in for a runner that dies abruptly;
            // the real payload group deliberately remains alive after its death.
            var runner = Process.Start(new ProcessStartInfo("/bin/bash")
            {
                ArgumentList = { "-c", "exec sleep 30" },
                UseShellExecute = false
            })!;
            using var execution = new ManagedExecutionHost.ManagedExecution(
                runner, Stream.Null, payload.ProcessGroup, TimeSpan.FromSeconds(5));
            await payload.ActivateAsync(CancellationToken.None);
            await WaitUntilAsync(() => File.Exists(pidFile));
            payloadPid = int.Parse(await File.ReadAllTextAsync(pidFile));

            runner.Kill();
            await runner.WaitForExitAsync();
            Assert.True(execution.HasExited);
            Assert.True(IsAlive(payloadPid));

            await execution.StopAsync(CancellationToken.None);

            Assert.True(execution.TryConfirmStopped());
            Assert.False(IsAlive(payloadPid));
        }
        finally
        {
            SupervisedProcess.SetsidPathOverride = previous;
            KillIfAlive(payloadPid);
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task BrokenOutputFileDoesNotStopDrainingThePayload()
    {
        string directory = CreateDirectory();
        try
        {
            string blocker = Path.Combine(directory, "blocker");
            await File.WriteAllTextAsync(blocker, "not a directory");
            string script = Path.Combine(directory, "payload.sh");
            await File.WriteAllTextAsync(
                script,
                "for i in $(seq 1 20000); do echo output-$i; done\n");
            await using var protocol = await RunnerProtocol.StartAsync(Options(
                script,
                directory,
                Path.Combine(blocker, "stdout.txt"),
                Path.Combine(directory, "stderr.txt")));
            RelayRunner.ParseReadySignal(await protocol.Ready.ReadLineAsync());

            await protocol.Control.WriteLineAsync(RelayRunner.GoSignal);

            Assert.Equal(0, await protocol.Result.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static Dictionary<string, string> Options(
        string script,
        string directory,
        string stdout,
        string stderr) => new(StringComparer.Ordinal)
    {
        ["script"] = script,
        ["working-directory"] = directory,
        ["stdout"] = stdout,
        ["stderr"] = stderr,
        ["gpus"] = ""
    };

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan? timeout = null)
    {
        using var cancellation = new CancellationTokenSource(
            timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, cancellation.Token);
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"relay-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateSetsidStandIn(string directory)
    {
        foreach (string candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
            if (File.Exists(candidate))
                return candidate;

        string path = Path.Combine(directory, "setsid");
        File.WriteAllText(
            path,
            "#!/bin/bash\nexec /usr/bin/perl -e 'setpgrp(0,0); exec @ARGV or die' \"$@\"\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            return !Process.GetProcessById(processId).HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void KillIfAlive(int processId)
    {
        if (processId <= 0)
            return;

        try
        {
            Process.GetProcessById(processId).Kill();
        }
        catch
        {
        }
    }

    private sealed class RunnerProtocol : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _controlServer;
        private readonly NamedPipeServerStream _readyServer;
        private readonly NamedPipeClientStream _controlClient;
        private readonly NamedPipeClientStream _readyClient;
        private bool _controlClosed;

        private RunnerProtocol(
            NamedPipeServerStream controlServer,
            NamedPipeServerStream readyServer,
            NamedPipeClientStream controlClient,
            NamedPipeClientStream readyClient,
            Task<int> result)
        {
            _controlServer = controlServer;
            _readyServer = readyServer;
            _controlClient = controlClient;
            _readyClient = readyClient;
            Control = new StreamWriter(controlServer) { AutoFlush = true };
            Ready = new StreamReader(readyServer);
            Result = result;
        }

        public StreamWriter Control { get; }
        public StreamReader Ready { get; }
        public Task<int> Result { get; }

        public static async Task<RunnerProtocol> StartAsync(
            IReadOnlyDictionary<string, string> options)
        {
            string suffix = Guid.NewGuid().ToString("N")[..8];
            string controlName = $"rc-{suffix}";
            string readyName = $"rr-{suffix}";
            var controlServer = new NamedPipeServerStream(
                controlName,
                PipeDirection.Out,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var readyServer = new NamedPipeServerStream(
                readyName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var controlClient = new NamedPipeClientStream(
                ".", controlName, PipeDirection.In, PipeOptions.Asynchronous);
            var readyClient = new NamedPipeClientStream(
                ".", readyName, PipeDirection.Out, PipeOptions.Asynchronous);

            Task controlConnected = controlServer.WaitForConnectionAsync();
            Task readyConnected = readyServer.WaitForConnectionAsync();
            await Task.WhenAll(
                controlClient.ConnectAsync(),
                readyClient.ConnectAsync(),
                controlConnected,
                readyConnected);

            Task<int> result = RelayRunner.RunAsync(
                options,
                controlClient,
                readyClient);
            return new RunnerProtocol(
                controlServer,
                readyServer,
                controlClient,
                readyClient,
                result);
        }

        public void CloseControl()
        {
            if (_controlClosed)
                return;
            _controlClosed = true;
            Control.Dispose();
            _controlServer.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            CloseControl();
            Ready.Dispose();
            _readyServer.Dispose();
            _controlClient.Dispose();
            _readyClient.Dispose();
            try
            {
                await Result.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
    }
}
