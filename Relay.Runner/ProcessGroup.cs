using System.Runtime.InteropServices;

namespace Relay.Runner;

// Shared with the live owner so it can contain a payload if the runner itself dies.
// These IDs are never used to recover ownership after Relay restarts.
internal static class ProcessGroup
{
    internal static bool Kill(int? processGroup) =>
        processGroup is { } group && group > 1 && Signal(-group, SigKill) == 0;

    internal static bool IsEmpty(int processGroup)
    {
        if (processGroup <= 1)
            return false;

        try
        {
            return Signal(-processGroup, 0) != 0 &&
                   Marshal.GetLastWin32Error() == NoSuchProcess;
        }
        catch
        {
            return false;
        }
    }

    private const int SigKill = 9;
    private const int NoSuchProcess = 3;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Signal(int pid, int signal);

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    internal static extern int Get(int pid);
}
