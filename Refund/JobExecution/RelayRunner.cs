using System.Diagnostics;
using Refund.JobQueues;

namespace Refund.JobExecution;

public static class RelayRunner
{
    public const string Command = "--relay-runner";

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var options = Parse(arguments);
        int parentPid = int.Parse(Required(options, "parent-pid"));
        long parentStartTicks = long.Parse(Required(options, "parent-start-ticks"));
        string script = Required(options, "script");
        string workingDirectory = Required(options, "working-directory");
        string standardOutput = Required(options, "stdout");
        string standardError = Required(options, "stderr");
        int[] gpus = options.GetValueOrDefault("gpus", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();

        var payload = SystemManagedProcess.Start(
            script, workingDirectory, gpus, standardOutput, standardError);

        void StopPayload()
        {
            payload.KillTree();
        }

        EventHandler processExit = (_, _) => StopPayload();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            StopPayload();
        };
        AppDomain.CurrentDomain.ProcessExit += processExit;
        Console.CancelKeyPress += cancel;

        try
        {
            while (!payload.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ParentMatches(parentPid, parentStartTicks))
                {
                    StopPayload();
                    await payload.WaitForExitAsync(CancellationToken.None);
                    return 137;
                }

                await Task.Delay(500, cancellationToken);
            }

            await payload.WaitForExitAsync(CancellationToken.None);
            return payload.ExitCode;
        }
        finally
        {
            AppDomain.CurrentDomain.ProcessExit -= processExit;
            Console.CancelKeyPress -= cancel;
            StopPayload();
        }
    }

    private static bool ParentMatches(int pid, long startTicks)
    {
        try
        {
            using var parent = Process.GetProcessById(pid);
            return !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == startTicks;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> Parse(IReadOnlyList<string> arguments)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < arguments.Count; index += 2)
        {
            if (index + 1 >= arguments.Count || !arguments[index].StartsWith("--"))
                throw new ArgumentException("relay-runner arguments must be --name value pairs.");
            result[arguments[index][2..]] = arguments[index + 1];
        }

        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing --{name}.");
}
