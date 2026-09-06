using System.Diagnostics;
using Refund.JobExecution;

namespace Refund.Tests.JobExecution;

public sealed class RelayRunnerTests
{
    [Fact]
    public async Task RunAsync_ReturnsPayloadExitCodeAfterDrainingOutput()
    {
        string directory = CreateDirectory();
        try
        {
            string script = Path.Combine(directory, "payload.sh");
            string stdout = Path.Combine(directory, "stdout.txt");
            string stderr = Path.Combine(directory, "stderr.txt");
            await File.WriteAllTextAsync(script, "echo ready\necho problem >&2\nexit 7\n");

            int result = await RelayRunner.RunAsync(Arguments(script, directory, stdout, stderr));

            Assert.Equal(7, result);
            Assert.Equal("ready\n", await File.ReadAllTextAsync(stdout));
            Assert.Equal("problem\n", await File.ReadAllTextAsync(stderr));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RunAsync_StopsPayloadWhenRelayIdentityIsGone()
    {
        string directory = CreateDirectory();
        try
        {
            string script = Path.Combine(directory, "payload.sh");
            await File.WriteAllTextAsync(script, "sleep 30\n");
            var arguments = Arguments(
                script,
                directory,
                Path.Combine(directory, "stdout.txt"),
                Path.Combine(directory, "stderr.txt"));
            arguments[1] = int.MaxValue.ToString();

            int result = await RelayRunner.RunAsync(arguments).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(137, result);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static List<string> Arguments(
        string script,
        string directory,
        string stdout,
        string stderr)
    {
        using var current = Process.GetCurrentProcess();
        return
        [
            "--parent-pid", current.Id.ToString(),
            "--parent-start-ticks", current.StartTime.ToUniversalTime().Ticks.ToString(),
            "--script", script,
            "--working-directory", directory,
            "--stdout", stdout,
            "--stderr", stderr,
            "--gpus", ""
        ];
    }

    private static string CreateDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"relay-runner-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
