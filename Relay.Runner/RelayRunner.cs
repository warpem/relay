using System.IO.Pipes;
using static Relay.Runner.RunnerProtocol;

namespace Relay.Runner;

internal static class RelayRunner
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        var options = Parse(arguments);
        await using var control = new AnonymousPipeClientStream(
            PipeDirection.In,
            Required(options, "control-handle"));
        await using var ready = new AnonymousPipeClientStream(
            PipeDirection.Out,
            Required(options, "ready-handle"));
        return await RunAsync(options, control, ready, cancellationToken);
    }

    internal static async Task<int> RunAsync(
        IReadOnlyDictionary<string, string> options,
        Stream control,
        Stream ready,
        CancellationToken cancellationToken = default)
    {
        string script = Required(options, "script");
        string workingDirectory = Required(options, "working-directory");
        string standardOutput = Required(options, "stdout");
        string standardError = Required(options, "stderr");
        int[] gpus = options.GetValueOrDefault("gpus", "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(int.Parse)
            .ToArray();

        using var payload = SupervisedProcess.Prepare(
            script,
            workingDirectory,
            gpus,
            standardOutput,
            standardError);
        int exitCode = 125;
        bool contained;
        try
        {
            using var controlReader = new StreamReader(control);
            await using (var readyWriter = new StreamWriter(ready) { AutoFlush = true })
            {
                await readyWriter.WriteLineAsync(
                    $"{ReadySignal} {payload.ProcessGroup ?? 0}".AsMemory(), cancellationToken);
            }

            string command = await controlReader.ReadLineAsync(cancellationToken);
            if (command == GoSignal)
            {
                await payload.ActivateAsync(cancellationToken);
                using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task payloadExit = payload.WaitForProcessExitAsync(CancellationToken.None);
                Task<string> controlCommand = controlReader.ReadLineAsync(monitorCancellation.Token).AsTask();
                Task completed = await Task.WhenAny(payloadExit, controlCommand);
                exitCode = completed == controlCommand ? 137 : payload.ExitCode;
                monitorCancellation.Cancel();
                try
                {
                    await controlCommand;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }
        finally
        {
            payload.KillTree();
            contained = await payload.WaitForContainmentAsync(CancellationToken.None);
        }

        return contained ? exitCode : 125;
    }

    internal static Dictionary<string, string> Parse(IReadOnlyList<string> arguments)
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
