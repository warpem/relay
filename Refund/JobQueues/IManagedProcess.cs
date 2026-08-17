namespace Refund.JobQueues;

/// <summary>
/// The bit of an OS process the executor needs. Exists so the reconciliation rules — which is where
/// the resource-accounting bugs live — can be tested deterministically without spawning anything.
/// </summary>
public interface IManagedProcess
{
    int Pid { get; }

    /// <summary>Start time, paired with the pid to survive pid recycling across a Relay restart.</summary>
    DateTime StartTime { get; }

    bool HasExited { get; }

    /// <summary>Only meaningful once <see cref="HasExited"/> is true.</summary>
    int ExitCode { get; }

    /// <summary>Terminate the whole tree, not just the direct child — jobs launch mpirun.</summary>
    void KillTree();

    Task WaitForExitAsync(CancellationToken ct = default);
}
