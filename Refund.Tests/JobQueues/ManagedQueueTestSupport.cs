using Refund.DataModel;
using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

/// <summary>
/// Job.PopulateStatic() mutates process-wide statics and is not idempotent, so every test class
/// that constructs a concrete Job type goes through here. Classes using it must also carry
/// [Collection("JobRegistry")], which serialises them.
/// </summary>
internal static class JobRegistry
{
    private static readonly object PopulateLock = new();

    public static void EnsurePopulated()
    {
        lock (PopulateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }
}

/// <summary>
/// Stands in for a real OS process so liveness can be driven deterministically. KillTree does not
/// make the process exit: the executor must keep holding a condemned job's resources until the
/// exit is separately confirmed, and tests drive that with <see cref="Exit"/>.
/// </summary>
internal sealed class FakeProcess : IManagedProcess
{
    public int Pid { get; init; } = 4242;
    public DateTime StartTime { get; init; } = new(2026, 1, 1);
    public bool HasExited { get; private set; }
    public int ExitCode { get; private set; }
    public bool WasKilled => KillCount > 0;
    public int KillCount { get; private set; }

    /// <summary>Fired inside <see cref="KillTree"/>, for tests that need to look at the world at
    /// the moment the executor is disowning this process.</summary>
    public Action? OnKill { get; init; }

    public void Exit(int code) { ExitCode = code; HasExited = true; }
    public void KillTree() { KillCount++; OnKill?.Invoke(); }
    public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
}
