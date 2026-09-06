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

            Assert.Equal(RelayRunner.ReadySignal, await protocol.Ready.ReadLineAsync());
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
            Assert.Equal(RelayRunner.ReadySignal, await protocol.Ready.ReadLineAsync());

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
            string release = Path.Combine(directory, "release");
            string script = Path.Combine(directory, "payload.sh");
            await File.WriteAllTextAsync(
                script,
                $"sleep 30 >/dev/null 2>&1 & echo $! > '{pidFile}'\n" +
                $"while [ ! -f '{release}' ]; do sleep 0.05; done\n");
            await using var protocol = await RunnerProtocol.StartAsync(Options(
                script,
                directory,
                Path.Combine(directory, "stdout.txt"),
                Path.Combine(directory, "stderr.txt")));
            Assert.Equal(RelayRunner.ReadySignal, await protocol.Ready.ReadLineAsync());
            await protocol.Control.WriteLineAsync(RelayRunner.GoSignal);
            await WaitUntilAsync(() => File.Exists(pidFile));
            childPid = int.Parse(await File.ReadAllTextAsync(pidFile));

            await File.WriteAllTextAsync(release, "");

            Assert.Equal(0, await protocol.Result.WaitAsync(TimeSpan.FromSeconds(10)));
            await WaitUntilAsync(() => !IsAlive(childPid));
        }
        finally
        {
            SupervisedProcess.SetsidPathOverride = previous;
            KillIfAlive(childPid);
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
            Assert.Equal(RelayRunner.ReadySignal, await protocol.Ready.ReadLineAsync());

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(10, timeout.Token);
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
