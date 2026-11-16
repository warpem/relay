using System.Diagnostics;
using System.Text.Json;
namespace Refund.Services.Warp;

/// <summary>
/// One catalog/coordinator per session, shared by every authorized viewer. A session is loaded while something uses it
/// (a <see cref="WarpSessionLease"/>: an open Warp view, a run, an export, a command) and, when unloading is on, unloaded
/// once it has been idle for the timeout; loading it again restores its checkpoint in well under a second. Cards read
/// <see cref="GetSummary"/>, which never loads a session.
/// </summary>
public sealed class WarpSessionManager : IDisposable
{
    private sealed class Entry(Lazy<WarpSession> session)
    {
        public readonly Lazy<WarpSession> Session=session;
        public int Leases;
        public DateTime LastUsed=DateTime.UtcNow;
    }
    /// <summary>A loaded session as the State page lists it.</summary>
    public sealed record LoadedSession(string Key,WarpSessionIdentity Identity,int Movies,int Leases,bool Running,bool Exporting,DateTime LastUsed);

    private readonly object _gate=new();
    private readonly Dictionary<string,Entry> _sessions=[];
    private readonly Dictionary<string,Task> _unloading=[];
    private readonly Dictionary<string,(DateTime Checked,DateTime Written,WarpSessionSummary Summary)> _savedSummaries=[];
    private readonly Dictionary<string,DateTime> _summaryRaised=[];
    private readonly HashSet<string> _summaryPending=[];
    private readonly HashSet<string> _summarizing=[];
    private readonly SemaphoreSlim _summaryPass=new(1,1);
    private readonly Serilog.ILogger _logger;
    private readonly TimeSpan? _idleUnload;
    private readonly CancellationTokenSource _lifetime=new();
    private readonly Task _unloadLoop;
    private bool _disposed;

    /// <summary>A session's summary changed (its key); raised at most once a second per session.</summary>
    public event Action<string> SummaryChanged;

    /// <param name="idleUnload">Unload sessions nothing has used for this long; null keeps them loaded.</param>
    public WarpSessionManager(Serilog.ILogger logger=null,TimeSpan? idleUnload=null)
    {
        _logger=logger??Serilog.Log.ForContext<WarpSessionManager>();
        _idleUnload=idleUnload;
        _unloadLoop=idleUnload.HasValue?Task.Run(UnloadLoopAsync):Task.CompletedTask;
    }

    public TimeSpan? IdleUnload=>_idleUnload;

    /// <summary>Loads the session if needed and keeps it loaded until the lease is disposed.</summary>
    public WarpSessionLease Acquire(WarpSessionIdentity identity,WarpSettings initialSettings=null)
    {
        var entry=Resolve(identity,initialSettings,lease:true);
        try { return new(this,identity.Key,entry,entry.Session.Value); }
        catch { Release(entry); throw; }
    }

    /// <summary>Loads the session if needed without keeping it loaded; it may be unloaded once idle.</summary>
    public WarpSession GetOrCreate(WarpSessionIdentity identity,WarpSettings initialSettings=null) => Resolve(identity,initialSettings,lease:false).Session.Value;

    public Task RunAsync(WarpSessionIdentity identity,WarpSettings settings,CancellationToken token) => RunLeasedAsync(identity,settings,token);
    private async Task RunLeasedAsync(WarpSessionIdentity identity,WarpSettings settings,CancellationToken token)
    {
        using var lease=Acquire(identity,settings);
        await lease.Session.RunAsync(settings,token);
    }

    private Entry Resolve(WarpSessionIdentity identity,WarpSettings initialSettings,bool lease)
    {
        while (true)
        {
            Task unloading;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed,this);
                // A session being unloaded is saving its checkpoint; restoring now would read an older one.
                if (!_unloading.TryGetValue(identity.Key,out unloading))
                {
                    if (!_sessions.TryGetValue(identity.Key,out var entry))
                        _sessions[identity.Key]=entry=new(new Lazy<WarpSession>(()=>Load(identity,initialSettings),LazyThreadSafetyMode.ExecutionAndPublication));
                    if (lease) entry.Leases++;
                    entry.LastUsed=DateTime.UtcNow;
                    return entry;
                }
            }
            unloading.GetAwaiter().GetResult();
        }
    }

    private WarpSession Load(WarpSessionIdentity identity,WarpSettings initialSettings)
    {
        var watch=Stopwatch.StartNew();
        var session=new WarpSession(identity,_logger,initialSettings);
        _logger.Information("Loaded Warp session {Session}: {Movies:N0} movies in {Milliseconds} ms",identity.Key,session.Movies.Count,watch.ElapsedMilliseconds);
        session.Changed+=()=>RaiseSummaryChanged(identity.Key);
        return session;
    }

    internal void Release(object entryObject)
    {
        var entry=(Entry)entryObject;
        lock (_gate) { entry.Leases=Math.Max(0,entry.Leases-1); entry.LastUsed=DateTime.UtcNow; }
    }

    /// <summary>
    /// Unloads sessions with no lease that are neither processing nor exporting and were last used before
    /// <paramref name="now"/> minus <paramref name="idle"/> (default: the manager's timeout). Their checkpoints are saved
    /// first. Returns how many were unloaded.
    /// </summary>
    public int UnloadIdle(DateTime now,TimeSpan? idle=null)
    {
        var limit=idle??_idleUnload;
        if (!limit.HasValue) return 0;
        int count=0;
        lock (_gate)
        {
            if (_disposed) return 0;
            foreach (var key in _sessions.Keys.ToArray())
                if (TryUnload(key,now,limit.Value)) count++;
        }
        return count;
    }

    // Caller holds _gate. Starts unloading the session if nothing uses it; true if it did.
    private bool TryUnload(string key,DateTime now,TimeSpan idle)
    {
        if (!_sessions.TryGetValue(key,out var entry)||entry.Leases>0||now-entry.LastUsed<idle) return false;
        if (!entry.Session.IsValueCreated) { _sessions.Remove(key); return false; }
        var session=entry.Session.Value;
        if (session.Running||session.Exporting) return false;
        _sessions.Remove(key);
        // Until the unload has saved the summary file, cards get the session's last live summary.
        _savedSummaries[key]=(DateTime.UtcNow,default,session.Summary);
        var idleFor=now-entry.LastUsed;
        _unloading[key]=Task.Run(()=>Unload(key,session,idleFor));
        return true;
    }

    private void Unload(string key,WarpSession session,TimeSpan idle)
    {
        try
        {
            session.Dispose();
            _logger.Information("Unloaded idle Warp session {Session} after {Minutes:N0} min unused",key,idle.TotalMinutes);
        }
        catch (Exception ex) { _logger.Error(ex,"Unloading Warp session {Session} failed",key); }
        finally { lock (_gate) _unloading.Remove(key); RaiseSummaryChanged(key); }
    }

    private async Task UnloadLoopAsync()
    {
        using var timer=new PeriodicTimer(TimeSpan.FromMinutes(1));
        try { while (await timer.WaitForNextTickAsync(_lifetime.Token)) UnloadIdle(DateTime.UtcNow); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.Error(ex,"Warp session unloading stopped"); }
    }

    /// <summary>Whether <paramref name="session"/> is the one loaded for its key (not replaced by a clear or unloaded).</summary>
    public bool IsCurrent(WarpSession session)
    {
        if (session==null) return false;
        lock (_gate) return _sessions.TryGetValue(session.Identity.Key,out var entry)&&entry.Session.IsValueCreated&&ReferenceEquals(entry.Session.Value,session);
    }

    /// <summary>The sessions now in memory.</summary>
    public IReadOnlyList<LoadedSession> LoadedSessions
    {
        get
        {
            List<(string Key,Entry Entry)> entries;
            lock (_gate) entries=_sessions.Where(p=>p.Value.Session.IsValueCreated).Select(p=>(p.Key,p.Value)).ToList();
            return entries.Select(e=>
            {
                var s=e.Entry.Session.Value;
                int leases;DateTime used;lock (_gate) { leases=e.Entry.Leases; used=e.Entry.LastUsed; }
                return new LoadedSession(e.Key,s.Identity,s.Movies.Count,leases,s.Running,s.Exporting,used);
            }).ToList();
        }
    }

    /// <summary>
    /// What a session's card shows, without loading it: live while it is loaded, else its saved summary (re-read when the
    /// file changes). Null while there is none yet; sessions saved before summaries existed get one written in the
    /// background, one at a time, which raises <see cref="SummaryChanged"/>.
    /// </summary>
    public WarpSessionSummary GetSummary(WarpSessionIdentity identity)
    {
        Entry entry;
        lock (_gate) _sessions.TryGetValue(identity.Key,out entry);
        if (entry is { Session.IsValueCreated: true }) return entry.Session.Value.Summary;
        string path=Path.Combine(identity.SessionDirectory,WarpSession.SummaryFile);
        var now=DateTime.UtcNow;
        lock (_gate)
            if (_savedSummaries.TryGetValue(identity.Key,out var cached)&&now-cached.Checked<TimeSpan.FromSeconds(5)) return cached.Summary;
        WarpSessionSummary summary=null;
        DateTime written=File.Exists(path)?File.GetLastWriteTimeUtc(path):default;
        lock (_gate)
            if (_savedSummaries.TryGetValue(identity.Key,out var cached)&&cached.Written==written&&written!=default) { _savedSummaries[identity.Key]=(now,written,cached.Summary); return cached.Summary; }
        if (written!=default)
            try { summary=JsonSerializer.Deserialize<WarpSessionSummary>(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _logger.Warning(ex,"Cannot read Warp session summary {Session}",identity.Key); }
        else if (File.Exists(Path.Combine(identity.SessionDirectory,"warp-mini.json"))) Summarize(identity);
        lock (_gate) _savedSummaries[identity.Key]=(now,written,summary);
        return summary;
    }

    // Loads a session that has a checkpoint but no summary, writes its summary and lets it go again.
    private void Summarize(WarpSessionIdentity identity)
    {
        lock (_gate) if (!_summarizing.Add(identity.Key)) return;
        _ = Task.Run(async ()=>
        {
            await _summaryPass.WaitAsync();
            try
            {
                bool wasLoaded;lock (_gate) wasLoaded=_sessions.ContainsKey(identity.Key);
                var session=GetOrCreate(identity);
                session.WriteSummary();
                _logger.Information("Wrote the missing summary of Warp session {Session}",identity.Key);
                if (!wasLoaded) lock (_gate) TryUnload(identity.Key,DateTime.UtcNow,TimeSpan.Zero);
            }
            catch (Exception ex) { _logger.Warning(ex,"Cannot summarize Warp session {Session}",identity.Key); }
            finally { _summaryPass.Release(); lock (_gate) { _summarizing.Remove(identity.Key); _savedSummaries.Remove(identity.Key); } RaiseSummaryChanged(identity.Key); }
        });
    }

    // At most once a second per session; a change within the second is delivered once at its end, never dropped.
    private void RaiseSummaryChanged(string key)
    {
        var now=DateTime.UtcNow;
        TimeSpan wait;
        lock (_gate)
        {
            if (_summaryPending.Contains(key)) return;
            wait=_summaryRaised.TryGetValue(key,out var last)?TimeSpan.FromSeconds(1)-(now-last):TimeSpan.Zero;
            if (wait>TimeSpan.Zero) _summaryPending.Add(key); else _summaryRaised[key]=now;
        }
        if (wait<=TimeSpan.Zero) { Notify(key); return; }
        _ = Task.Delay(wait).ContinueWith(_ =>
        {
            lock (_gate) { _summaryPending.Remove(key); _summaryRaised[key]=DateTime.UtcNow; }
            Notify(key);
        },TaskScheduler.Default);
    }
    private void Notify(string key)
    {
        try { SummaryChanged?.Invoke(key); }
        catch (Exception ex) { _logger.Warning(ex,"A Warp summary listener failed"); }
    }

    public void Remove(WarpSessionIdentity identity)
    {
        Entry entry;
        lock (_gate)
        {
            _sessions.Remove(identity.Key,out entry);
            _savedSummaries.Remove(identity.Key);
        }
        if (entry is { Session.IsValueCreated: true }) entry.Session.Value.Dispose();
    }

    public void Dispose()
    {
        List<Entry> entries;Task[] unloading;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed=true;
            entries=_sessions.Values.ToList();_sessions.Clear();
            unloading=_unloading.Values.ToArray();
        }
        _lifetime.Cancel();
        try { _unloadLoop.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        Task.WaitAll(unloading);
        foreach (var entry in entries) if (entry.Session.IsValueCreated) entry.Session.Value.Dispose();
    }
}

/// <summary>Keeps a Warp session loaded until disposed.</summary>
public sealed class WarpSessionLease : IDisposable
{
    private WarpSessionManager _manager;
    private readonly object _entry;
    internal WarpSessionLease(WarpSessionManager manager,string key,object entry,WarpSession session) { _manager=manager; _entry=entry; Key=key; Session=session; }
    public string Key { get; }
    public WarpSession Session { get; }
    public void Dispose() => Interlocked.Exchange(ref _manager,null)?.Release(_entry);
}
