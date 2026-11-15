namespace Refund.Services.Storage;

/// <summary>What the storage service measures: a job's folder, a leftover folder in a space root, or the root listing.</summary>
public readonly record struct StorageTarget(int ProjectId, int SpaceId, int JobId, string Leftover = null)
{
    public static StorageTarget Job(int projectId, int spaceId, int jobId) => new(projectId, spaceId, jobId);
    public static StorageTarget Listing(int projectId, int spaceId) => new(projectId, spaceId, 0);
    public static StorageTarget LeftoverFolder(int projectId, int spaceId, string name) => new(projectId, spaceId, 0, name);
    public bool IsJob => JobId > 0;
    public bool IsListing => JobId == 0 && Leftover == null;
}

/// <summary>Priority classes, most urgent first.</summary>
public enum StorageClass
{
    /// <summary>Someone asked for it in the storage overlay.</summary>
    Requested,
    /// <summary>The job's status changed or its files were cleaned; measured after a short delay.</summary>
    Changed,
    /// <summary>Never measured: the 1.5-year backlog.</summary>
    CatchUp,
    /// <summary>A running job whose measurement is due for a refresh.</summary>
    Running,
    /// <summary>A settled job measured long ago.</summary>
    Stale,
    /// <summary>Space root listings and leftover folders.</summary>
    Background,
}

/// <summary>
/// Decides what to measure next. Strict classes; within catch-up, every job type is sampled a few times first, then the
/// type with the largest mean size so far goes first, and within a type the job with the most output items (a free size
/// proxy) and then the newest. The backlog's big consumers show up in the first hour instead of the last.
/// Thread-safe; a target is handed to one worker at a time.
/// </summary>
public sealed class StorageScheduler
{
    public static readonly TimeSpan ChangeDelay = TimeSpan.FromSeconds(30);
    public const int SamplesPerType = 3;

    private readonly object _gate = new();
    private readonly Dictionary<StorageTarget, StorageClass> _queued = new();
    private readonly Queue<StorageTarget> _requested = new();
    private readonly Dictionary<StorageTarget, DateTime> _changed = new();
    private readonly Dictionary<string, List<CatchUpEntry>> _catchUp = new();
    private readonly Dictionary<string, int> _sampled = new();
    private readonly Dictionary<string, (long Count, double Allocated, double Entries)> _typeStats = new();
    private readonly Dictionary<StorageTarget, DateTime> _running = new();
    private readonly Queue<StorageTarget> _stale = new();
    private readonly Queue<StorageTarget> _background = new();
    private readonly HashSet<StorageTarget> _inProgress = new();
    private readonly HashSet<StorageTarget> _again = new();

    private readonly record struct CatchUpEntry(StorageTarget Target, long Proxy, long CreatedTicks) : IComparable<CatchUpEntry>
    {
        // Ascending, so the most promising entry is last and pops cheaply.
        public int CompareTo(CatchUpEntry other) =>
            Proxy != other.Proxy ? Proxy.CompareTo(other.Proxy) : CreatedTicks.CompareTo(other.CreatedTicks);
    }

    /// <summary>Queues a target; a more urgent class wins over a queued less urgent one.</summary>
    public void Enqueue(StorageTarget target, StorageClass cls, DateTime now, string typeGuid = null, long proxy = 0, DateTime created = default)
    {
        if (cls == StorageClass.Running)
        {
            RefreshRunning(target, now);
            return;
        }
        lock (_gate)
        {
            if (_inProgress.Contains(target))
            {
                // Measured right now with possibly stale contents: once more when done.
                if (cls <= StorageClass.Changed) _again.Add(target);
                return;
            }
            if (_queued.TryGetValue(target, out var current))
            {
                if (current == StorageClass.Changed && cls == StorageClass.Changed)
                {
                    _changed[target] = now + ChangeDelay; // debounce: wait for the job to settle
                    return;
                }
                if (current <= cls) return;
            }
            _queued[target] = cls;
            switch (cls)
            {
                case StorageClass.Requested: _requested.Enqueue(target); break;
                case StorageClass.Changed: _changed[target] = now + ChangeDelay; break;
                case StorageClass.CatchUp:
                {
                    string type = typeGuid ?? "";
                    if (!_catchUp.TryGetValue(type, out var list)) _catchUp[type] = list = [];
                    var entry = new CatchUpEntry(target, proxy, created.Ticks);
                    int at = list.BinarySearch(entry);
                    list.Insert(at < 0 ? ~at : at, entry);
                    break;
                }
                case StorageClass.Stale: _stale.Enqueue(target); break;
                case StorageClass.Background: _background.Enqueue(target); break;
            }
        }
    }

    /// <summary>Adds catch-up entries in bulk (startup), sorting each type's list once.</summary>
    public void SeedCatchUp(IEnumerable<(StorageTarget Target, string TypeGuid, long Proxy, DateTime Created)> entries)
    {
        lock (_gate)
        {
            foreach (var (target, type, proxy, created) in entries)
            {
                if (_queued.ContainsKey(target)) continue;
                _queued[target] = StorageClass.CatchUp;
                if (!_catchUp.TryGetValue(type ?? "", out var list)) _catchUp[type ?? ""] = list = [];
                list.Add(new(target, proxy, created.Ticks));
            }
            foreach (var list in _catchUp.Values) list.Sort();
        }
    }

    /// <summary>Schedules the next refresh of a running job.</summary>
    public void RefreshRunning(StorageTarget target, DateTime due)
    {
        lock (_gate) _running[target] = due;
    }

    public void StopRunning(StorageTarget target)
    {
        lock (_gate) _running.Remove(target);
    }

    /// <summary>Records a measured job's size, which steers catch-up towards big job types.</summary>
    public void Learn(string typeGuid, long allocated, long entries)
    {
        lock (_gate)
        {
            var s = _typeStats.GetValueOrDefault(typeGuid ?? "");
            _typeStats[typeGuid ?? ""] = (s.Count + 1, s.Allocated + allocated, s.Entries + entries);
        }
    }

    /// <summary>Forgets a target (deleted job, disconnected space).</summary>
    public void Remove(StorageTarget target)
    {
        lock (_gate)
        {
            _queued.Remove(target);
            _changed.Remove(target);
            _running.Remove(target);
            _again.Remove(target);
        }
    }

    public void RemoveSpace(int projectId, int spaceId) => RemoveWhere(t => t.ProjectId == projectId && t.SpaceId == spaceId);
    public void RemoveProject(int projectId) => RemoveWhere(t => t.ProjectId == projectId);

    private void RemoveWhere(Func<StorageTarget, bool> match)
    {
        lock (_gate)
        {
            foreach (var t in _queued.Keys.Where(match).ToList()) _queued.Remove(t);
            foreach (var t in _changed.Keys.Where(match).ToList()) _changed.Remove(t);
            foreach (var t in _running.Keys.Where(match).ToList()) _running.Remove(t);
            _again.RemoveWhere(t => match(t));
        }
    }

    /// <summary>
    /// Hands out the most urgent target that is due, or returns false with how long until something is due.
    /// Call <see cref="Done"/> when finished with it.
    /// </summary>
    public bool TryTake(DateTime now, out StorageTarget target, out StorageClass cls, out TimeSpan wait)
    {
        lock (_gate)
        {
            wait = TimeSpan.FromMinutes(1);
            if (TakeFrom(_requested, StorageClass.Requested, out target)) { cls = StorageClass.Requested; return Hand(target); }

            // Changed: the earliest due.
            StorageTarget best = default; DateTime bestDue = DateTime.MaxValue; bool found = false;
            foreach (var (t, due) in _changed)
                if (due < bestDue && !_inProgress.Contains(t)) { best = t; bestDue = due; found = true; }
            if (found)
            {
                if (bestDue <= now)
                {
                    _changed.Remove(best);
                    _queued.Remove(best);
                    target = best; cls = StorageClass.Changed;
                    return Hand(target);
                }
                wait = Min(wait, bestDue - now);
            }

            if (TakeCatchUp(out target)) { cls = StorageClass.CatchUp; return Hand(target); }

            // Running jobs whose refresh is due.
            found = false; bestDue = DateTime.MaxValue;
            foreach (var (t, due) in _running)
                if (due < bestDue && !_inProgress.Contains(t) && !_queued.ContainsKey(t)) { best = t; bestDue = due; found = true; }
            if (found)
            {
                if (bestDue <= now)
                {
                    _running[best] = DateTime.MaxValue; // until the measurement schedules the next one
                    target = best; cls = StorageClass.Running;
                    return Hand(target);
                }
                wait = Min(wait, bestDue - now);
            }

            if (TakeFrom(_stale, StorageClass.Stale, out target)) { cls = StorageClass.Stale; return Hand(target); }
            if (TakeFrom(_background, StorageClass.Background, out target)) { cls = StorageClass.Background; return Hand(target); }

            target = default; cls = default;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>Releases a target; returns whether it must be measured again (it changed while being measured).</summary>
    public bool Done(StorageTarget target, DateTime now)
    {
        lock (_gate)
        {
            _inProgress.Remove(target);
            if (!_again.Remove(target)) return false;
            _queued[target] = StorageClass.Changed;
            _changed[target] = now + ChangeDelay;
            return true;
        }
    }

    private bool Hand(StorageTarget target)
    {
        _inProgress.Add(target);
        return true;
    }

    private bool TakeFrom(Queue<StorageTarget> queue, StorageClass cls, out StorageTarget target)
    {
        while (queue.TryDequeue(out target))
        {
            if (!_queued.TryGetValue(target, out var current) || current != cls) continue; // superseded or removed
            _queued.Remove(target);
            return true;
        }
        return false;
    }

    private bool TakeCatchUp(out StorageTarget target)
    {
        while (true)
        {
            string type = PickType();
            if (type == null) { target = default; return false; }
            var list = _catchUp[type];
            var entry = list[^1];
            list.RemoveAt(list.Count - 1);
            if (list.Count == 0) _catchUp.Remove(type);
            if (!_queued.TryGetValue(entry.Target, out var current) || current != StorageClass.CatchUp) continue;
            _queued.Remove(entry.Target);
            _sampled[type] = _sampled.GetValueOrDefault(type) + 1;
            target = entry.Target;
            return true;
        }
    }

    // Types sampled fewer than SamplesPerType times go first (no estimate yet), then the largest mean size.
    private string PickType()
    {
        string best = null;
        bool bestUnsampled = false;
        double bestMean = -1;
        foreach (var (type, list) in _catchUp)
        {
            if (list.Count == 0) continue;
            bool unsampled = _sampled.GetValueOrDefault(type) < SamplesPerType && _typeStats.GetValueOrDefault(type).Count < SamplesPerType;
            double mean = _typeStats.TryGetValue(type, out var s) && s.Count > 0 ? s.Allocated / s.Count : 0;
            if (best == null || (unsampled && !bestUnsampled) || (unsampled == bestUnsampled && mean > bestMean))
            {
                best = type; bestUnsampled = unsampled; bestMean = mean;
            }
        }
        return best;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>Queued targets per class.</summary>
    public IReadOnlyDictionary<StorageClass, int> Pending
    {
        get
        {
            lock (_gate)
                return _queued.Values.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>A rough count of entries still to visit: queued jobs times their type's mean entries.</summary>
    public double EstimatedEntries()
    {
        lock (_gate)
        {
            double global = _typeStats.Values.Sum(s => s.Count) is var n and > 0 ? _typeStats.Values.Sum(s => s.Entries) / n : 0;
            double sum = 0;
            foreach (var (type, list) in _catchUp)
            {
                int queued = list.Count(e => _queued.TryGetValue(e.Target, out var c) && c == StorageClass.CatchUp);
                double mean = _typeStats.TryGetValue(type, out var s) && s.Count > 0 ? s.Entries / s.Count : global;
                sum += queued * mean;
            }
            return sum;
        }
    }

    public bool HasCatchUp
    {
        get { lock (_gate) return _queued.ContainsValue(StorageClass.CatchUp); }
    }
}
