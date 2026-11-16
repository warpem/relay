using Refund.Services.Diagnostics;
using Serilog;
using Serilog.Events;

namespace Refund.Tests.Diagnostics;

public sealed class RecentLogSinkTests
{
    [Fact]
    public void KeepsTheNewestEventsInOrderWithTheirDetails()
    {
        var sink = new RecentLogSink(capacity: 3);
        int added = 0;
        sink.Added += () => added++;
        using var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var source = logger.ForContext("SourceContext", "Refund.Services.Warp.WarpSessionManager");
        for (int i = 0; i < 4; i++) source.Information("Event {Number}", i);
        source.Error(new InvalidOperationException("broken"), "Failed {What}", "unload");

        var events = sink.Snapshot();
        Assert.Equal(5, added);
        Assert.Equal(["Event 2", "Event 3", "Failed unload"], events.Select(e => e.Message)); // Text values read as written.
        Assert.Equal([2L, 3L, 4L], events.Select(e => e.Sequence));
        Assert.Equal("Refund.Services.Warp.WarpSessionManager", events[^1].Source);
        Assert.Equal(LogEventLevel.Error, events[^1].Level);
        Assert.Contains("broken", events[^1].Exception);
        Assert.Null(events[0].Exception);
    }

    [Fact]
    public void AFailingListenerDoesNotBreakLogging()
    {
        var sink = new RecentLogSink();
        sink.Added += () => throw new InvalidOperationException("listener");
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        logger.Information("still logged");
        Assert.Equal("still logged", Assert.Single(sink.Snapshot()).Message);
    }
}
