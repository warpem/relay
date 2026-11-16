using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

namespace Refund.Services.Diagnostics;

/// <summary>One log event as the Logs page shows it.</summary>
public sealed record RecentLogEvent(long Sequence, DateTimeOffset Time, LogEventLevel Level, string Source, string Message, string Exception);

/// <summary>Keeps the newest log events in memory for the Logs page (a ring buffer; older events stay in the log files).</summary>
public sealed class RecentLogSink(int capacity = 5000) : ILogEventSink
{
    private readonly RecentLogEvent[] _events = new RecentLogEvent[capacity];
    private readonly object _gate = new();
    private long _count;

    public int Capacity => _events.Length;

    /// <summary>An event was added. Handlers run on the logging thread and must be quick.</summary>
    public event Action Added;

    public void Emit(LogEvent logEvent)
    {
        string source = logEvent.Properties.TryGetValue("SourceContext", out var value) && value is ScalarValue { Value: string context } ? context : "";
        lock (_gate)
        {
            _events[_count % _events.Length] = new(_count, logEvent.Timestamp, logEvent.Level, source, Render(logEvent), logEvent.Exception?.ToString());
            _count++;
        }
        try { Added?.Invoke(); } catch { /* A listener must never break logging. */ }
    }

    // Like RenderMessage, but text values appear as written rather than quoted ("1/1/1" reads as 1/1/1).
    private static string Render(LogEvent logEvent)
    {
        var output = new StringWriter();
        foreach (var token in logEvent.MessageTemplate.Tokens)
        {
            if (token is PropertyToken property && logEvent.Properties.TryGetValue(property.PropertyName, out var value) && value is ScalarValue { Value: string text } && property.Format == null)
                output.Write(text);
            else token.Render(logEvent.Properties, output);
        }
        return output.ToString();
    }

    /// <summary>The kept events, oldest first.</summary>
    public RecentLogEvent[] Snapshot()
    {
        lock (_gate)
        {
            long first = Math.Max(0, _count - _events.Length);
            var result = new RecentLogEvent[_count - first];
            for (long i = first; i < _count; i++) result[i - first] = _events[i % _events.Length];
            return result;
        }
    }
}
