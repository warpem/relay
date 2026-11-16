namespace Refund.Services.Diagnostics;

/// <summary>Where this run of Relay logs: its in-memory recent events and the folder of its log files.</summary>
public static class RelayLog
{
    public static RecentLogSink Sink { get; } = new();

    /// <summary>This run's log folder (one file per source), set at startup; empty when unknown.</summary>
    public static string Directory { get; set; } = "";
}
