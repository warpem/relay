using Microsoft.AspNetCore.Components.Server.Circuits;
using Refund.Services.Core.Session;
using Refund.Services.Diagnostics;
using Serilog;

namespace Relay.Services;

/// <summary>
/// Counts Blazor circuits (open browser pages) for the State page and logs each one's lifetime. One instance per circuit.
/// </summary>
public sealed class CircuitTracker(CircuitStats stats, RelaySession session) : CircuitHandler
{
    private static readonly Serilog.ILogger Logger = Log.ForContext<CircuitTracker>();
    private string UserName => session.User?.Username ?? "(not signed in)";

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Weak: the singleton stats must not keep this circuit's session (and so the circuit) alive.
        var weak = new WeakReference<RelaySession>(session);
        stats.Opened(circuit.Id, () => weak.TryGetTarget(out var s) ? s.User?.Username : null);
        Logger.Debug("Circuit {Circuit} opened", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        stats.Connection(circuit.Id, up: true);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        stats.Connection(circuit.Id, up: false);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var open = stats.Closed(circuit.Id);
        Logger.Information("Circuit closed for {User} after {Minutes:N1} min", UserName, open.TotalMinutes);
        return Task.CompletedTask;
    }
}
