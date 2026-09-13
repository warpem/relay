namespace Relay.Runner;

internal static class RunnerProtocol
{
    internal const string ReadySignal = "READY";
    internal const string GoSignal = "GO";
    internal const string StopSignal = "STOP";

    internal static int? ParseReadySignal(string signal)
    {
        string[] fields = signal?.Split(' ') ?? [];
        if (fields.Length != 2 || fields[0] != ReadySignal ||
            !int.TryParse(fields[1], out int processGroup) || processGroup < 0 || processGroup == 1)
            throw new InvalidOperationException(
                $"relay-runner did not report its containment group (signal: {signal ?? "EOF"}).");
        return processGroup == 0 ? null : processGroup;
    }
}
