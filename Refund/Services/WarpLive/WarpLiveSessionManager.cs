using System.Collections.Concurrent;
namespace Refund.Services.WarpLive;

/// <summary>One catalog/coordinator per session, shared by every authorized viewer.</summary>
public sealed class WarpLiveSessionManager : IDisposable
{
    private readonly ConcurrentDictionary<string,Lazy<WarpLiveSession>> _sessions=new();
    private readonly Serilog.ILogger _logger;
    public WarpLiveSessionManager(Serilog.ILogger logger=null) => _logger=logger??Serilog.Log.ForContext<WarpLiveSessionManager>();
    public WarpLiveSession GetOrCreate(WarpLiveSessionIdentity identity,WarpLiveSettings initialSettings=null) => _sessions.GetOrAdd(identity.Key,_=>new Lazy<WarpLiveSession>(()=>new WarpLiveSession(identity,_logger,initialSettings),LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    public Task RunAsync(WarpLiveSessionIdentity identity,WarpLiveSettings settings,CancellationToken token) => GetOrCreate(identity,settings).RunAsync(settings,token);
    public void Remove(WarpLiveSessionIdentity identity){if(_sessions.TryRemove(identity.Key,out var value)&&value.IsValueCreated)value.Value.Dispose();}
    public void Dispose(){foreach(var session in _sessions.Values)if(session.IsValueCreated)session.Value.Dispose();_sessions.Clear();}
}
