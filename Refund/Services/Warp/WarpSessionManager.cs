using System.Collections.Concurrent;
namespace Refund.Services.Warp;

/// <summary>One catalog/coordinator per session, shared by every authorized viewer.</summary>
public sealed class WarpSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string,Lazy<WarpSession>> _sessions=new();
    private readonly Serilog.ILogger _logger;
    public WarpSessionManager(Serilog.ILogger logger=null) => _logger=logger??Serilog.Log.ForContext<WarpSessionManager>();
    public WarpSession GetOrCreate(WarpSessionIdentity identity,WarpSettings initialSettings=null) => _sessions.GetOrAdd(identity.Key,_=>new Lazy<WarpSession>(()=>new WarpSession(identity,_logger,initialSettings),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    public Task RunAsync(WarpSessionIdentity identity,WarpSettings settings,CancellationToken token) => GetOrCreate(identity,settings).RunAsync(settings,token);
    public void Remove(WarpSessionIdentity identity){if(_sessions.TryRemove(identity.Key,out var value)&&value.IsValueCreated)value.Value.Dispose();}
    public void Dispose(){foreach(var session in _sessions.Values)if(session.IsValueCreated)session.Value.Dispose();_sessions.Clear();}
}
