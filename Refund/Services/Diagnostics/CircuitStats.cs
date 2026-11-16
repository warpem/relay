namespace Refund.Services.Diagnostics;

/// <summary>Blazor circuits (open browser pages) as the State page counts them; fed by Relay's circuit handler.</summary>
public sealed class CircuitStats
{
    // The user is read when shown: sign-in finishes after the circuit opens. The reader must not keep the circuit alive.
    private sealed record Circuit(Func<string> User, DateTime Opened, bool Connected);
    private readonly Dictionary<string, Circuit> _circuits = [];
    private readonly object _gate = new();
    private long _opened, _closed;

    public void Opened(string id, Func<string> user) { lock (_gate) { _circuits[id] = new(user, DateTime.UtcNow, true); _opened++; } }
    public void Connection(string id, bool up) { lock (_gate) if (_circuits.TryGetValue(id, out var c)) _circuits[id] = c with { Connected = up }; }
    /// <returns>How long the circuit was open.</returns>
    public TimeSpan Closed(string id)
    {
        lock (_gate)
        {
            _closed++;
            return _circuits.Remove(id, out var c) ? DateTime.UtcNow - c.Opened : TimeSpan.Zero;
        }
    }

    public sealed record Snapshot(int Connected, int Disconnected, long OpenedSinceStart, long ClosedSinceStart, IReadOnlyList<(string User, int Count)> PerUser);

    public Snapshot Current()
    {
        lock (_gate)
            return new(_circuits.Values.Count(c => c.Connected), _circuits.Values.Count(c => !c.Connected), _opened, _closed,
                _circuits.Values.GroupBy(c => c.User() ?? "(not signed in)").Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ToList());
    }
}
