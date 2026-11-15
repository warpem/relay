using Refund.DataModel;

namespace Refund.Services.Storage.Walking;

/// <summary>
/// Scratch space for measuring one folder tree, reused across walks by one thread so that walking allocates almost
/// nothing per entry: only the names of tracked folders and of extensions it hasn't seen before become strings.
/// </summary>
internal sealed class WalkState
{
    /// <summary>Folders this many levels below the root (and above) are tracked individually.</summary>
    public const int MaxTrackedDepth = 3;
    public const int MaxTracked = 2048;
    /// <summary>Deeper folders are not entered (counted as errors).</summary>
    public const int MaxDepth = 256;
    private const int MaxExtensionIds = 4096, MaxExtensionLength = 12, MaxIterations = 16384;

    // Tracked folders: node 0 is the root; a node's parent always has a lower index.
    private int[] _parent = new int[64];
    private string[] _name = new string[64];
    private int[] _depth = new int[64];
    private long[] _alloc = new long[64], _entries = new long[64];
    private int _nodes;

    // Extensions: ids are kept across walks (bounded); per-walk sums are reset through the touched list.
    private readonly Dictionary<string, int> _extensionIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _extensionLookup;
    private readonly List<string> _extensions = [""];
    private long[] _extAlloc = new long[64], _extEntries = new long[64];
    private readonly List<int> _extTouched = [];

    // Iterations, allocated on the first walk with a rule.
    private long[] _itAlloc, _itEntries;
    private readonly List<int> _itTouched = [];
    private UsagePart _regenerable;

    private HashSet<(ulong Dev, ulong Ino)> _hardLinks;
    private long _bytes, _allocated, _files, _folders, _links, _errors, _newest;
    private long _visited;

    public IntermediateRule Rule { get; private set; }
    public CancellationToken Cancellation { get; private set; }
    public StorageThrottle Throttle { get; private set; }

    /// <summary>Entries visited so far in the current walk (read by other threads for progress).</summary>
    public long Visited => Interlocked.Read(ref _visited);

    public WalkState()
    {
        _extensionIds[""] = 0;
        _extensionLookup = _extensionIds.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public void Begin(IntermediateRule rule, StorageThrottle throttle, CancellationToken cancellation)
    {
        Rule = rule; Throttle = throttle; Cancellation = cancellation;
        _nodes = 1; _parent[0] = -1; _name[0] = ""; _depth[0] = 0; _alloc[0] = 0; _entries[0] = 0;
        foreach (int id in _extTouched) { _extAlloc[id] = 0; _extEntries[id] = 0; }
        _extTouched.Clear();
        if (_itAlloc != null)
            foreach (int it in _itTouched) { _itAlloc[it] = 0; _itEntries[it] = 0; }
        _itTouched.Clear();
        if (rule != null && _itAlloc == null) { _itAlloc = new long[MaxIterations]; _itEntries = new long[MaxIterations]; }
        _regenerable = default;
        _hardLinks?.Clear();
        _bytes = _allocated = _files = _folders = _links = _errors = _newest = 0;
        Interlocked.Exchange(ref _visited, 0);
    }

    /// <summary>
    /// Counts a folder found in <paramref name="parentNode"/> at <paramref name="depth"/> (0: directly in the root) and
    /// returns the node its contents belong to: its own node while it is shallow enough, else the nearest tracked ancestor.
    /// </summary>
    public int Folder(int parentNode, int depth, ReadOnlySpan<char> name, int tag, long allocated, long mtimeTicks)
    {
        int node = parentNode;
        if (depth < MaxTrackedDepth && _nodes < MaxTracked)
        {
            node = _nodes++;
            EnsureNodes(_nodes);
            _parent[node] = parentNode; _name[node] = name.ToString(); _depth[node] = depth + 1;
            _alloc[node] = 0; _entries[node] = 0;
        }
        _folders++;
        Add(node, tag, ReadOnlySpan<char>.Empty, 0, allocated, mtimeTicks, isFile: false);
        return node;
    }

    public void File(int node, int tag, ReadOnlySpan<char> name, long bytes, long allocated, long mtimeTicks)
    {
        _files++;
        Add(node, tag, name, bytes, allocated, mtimeTicks, isFile: true);
    }

    /// <summary>A symbolic link: counted as an entry, never followed.</summary>
    public void Link(int node, int tag, long allocated, long mtimeTicks)
    {
        _links++;
        Add(node, tag, ReadOnlySpan<char>.Empty, 0, allocated, mtimeTicks, isFile: false);
    }

    /// <summary>A further name for a hard-linked file already counted: one more name, no more bytes.</summary>
    public void ExtraHardLink(int node)
    {
        _files++;
        _entries[node]++;
        Interlocked.Increment(ref _visited);
    }

    public void Error()
    {
        _errors++;
        Interlocked.Increment(ref _visited);
    }

    /// <summary>Whether this is the first name seen for a hard-linked file (its bytes count once).</summary>
    public bool FirstHardLink(ulong dev, ulong ino) => (_hardLinks ??= []).Add((dev, ino));

    private void Add(int node, int tag, ReadOnlySpan<char> name, long bytes, long allocated, long mtimeTicks, bool isFile)
    {
        _bytes += bytes; _allocated += allocated;
        _alloc[node] += allocated; _entries[node]++;
        if (mtimeTicks > _newest) _newest = mtimeTicks;

        if (isFile)
        {
            int id = ExtensionId(name);
            if (id >= _extAlloc.Length) { Array.Resize(ref _extAlloc, Math.Max(id + 1, _extAlloc.Length * 2)); Array.Resize(ref _extEntries, _extAlloc.Length); }
            if (_extEntries[id] == 0 && _extAlloc[id] == 0) _extTouched.Add(id);
            _extAlloc[id] += allocated; _extEntries[id]++;
        }

        if (tag >= 0 && tag < MaxIterations && _itAlloc != null)
        {
            if (_itEntries[tag] == 0) _itTouched.Add(tag);
            _itAlloc[tag] += allocated; _itEntries[tag]++;
        }
        else if (tag == IntermediateRule.Regenerable)
            _regenerable += new UsagePart(allocated, 1);

        Interlocked.Increment(ref _visited);
    }

    private int ExtensionId(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        // No extension: none at all, a leading dot only (".bashrc"), or something that isn't one ("run.it.0012345678901")
        if (dot <= 0 || dot == name.Length - 1 || name.Length - dot > MaxExtensionLength + 1)
            return 0;
        Span<char> ext = stackalloc char[MaxExtensionLength + 1];
        int n = 0;
        foreach (char c in name[dot..])
        {
            if (c != '.' && !char.IsAsciiLetterOrDigit(c)) return 0;
            ext[n++] = char.ToLowerInvariant(c);
        }
        var key = ext[..n];
        if (_extensionLookup.TryGetValue(key, out int id)) return id;
        if (_extensions.Count >= MaxExtensionIds) return 0;
        id = _extensions.Count;
        string text = key.ToString();
        _extensions.Add(text);
        _extensionIds[text] = id;
        return id;
    }

    private void EnsureNodes(int count)
    {
        if (count <= _parent.Length) return;
        int size = Math.Min(MaxTracked, _parent.Length * 2);
        Array.Resize(ref _parent, size); Array.Resize(ref _name, size); Array.Resize(ref _depth, size);
        Array.Resize(ref _alloc, size); Array.Resize(ref _entries, size);
    }

    /// <summary>Totals of the finished walk.</summary>
    public UsageTotals Totals => new(_bytes, _allocated, _files, _folders, _links, _errors);

    public DateTime? NewestWrite => _newest > 0 ? new DateTime(_newest, DateTimeKind.Utc) : null;

    /// <summary>The finished walk as a measurement.</summary>
    public JobStorage ToStorage(DateTime measuredAt, int durationMs, JobStatus status, DateTime? runStartedAt) => new()
    {
        MeasuredAt = measuredAt, DurationMs = durationMs, Status = status, RunStartedAt = runStartedAt, Exists = true,
        Total = Totals, NewestWrite = NewestWrite, Folders = TopFolders(), Kinds = TopKinds(), Iterations = IterationList(),
        Regenerable = _regenerable,
    };

    private UsageFolder[] TopFolders()
    {
        if (_nodes <= 1) return JobStorage.NoFolders;
        // Subtree sums: children have higher indices than their parents.
        for (int i = _nodes - 1; i > 0; i--) { _alloc[_parent[i]] += _alloc[i]; _entries[_parent[i]] += _entries[i]; }
        long totalAlloc = Math.Max(1, _alloc[0]), totalEntries = Math.Max(1, _entries[0]);
        // Folders holding at least 1% of the job's bytes or entries, biggest first; a parent never ranks below its child.
        var candidates = new List<(double Score, int Node)>();
        for (int i = 1; i < _nodes; i++)
        {
            double score = Math.Max(_alloc[i] / (double)totalAlloc, _entries[i] / (double)totalEntries);
            if (score >= 0.01) candidates.Add((score, i));
        }
        candidates.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.Node.CompareTo(b.Node));
        int count = Math.Min(JobStorage.MaxFolders, candidates.Count);
        var result = new UsageFolder[count];
        for (int k = 0; k < count; k++)
        {
            int node = candidates[k].Node;
            result[k] = new(PathOf(node), new(_alloc[node], _entries[node]));
        }
        return result;
    }

    private string PathOf(int node)
    {
        if (_parent[node] <= 0) return _name[node];
        return PathOf(_parent[node]) + "/" + _name[node];
    }

    private UsageKind[] TopKinds()
    {
        if (_extTouched.Count == 0) return JobStorage.NoKinds;
        _extTouched.Sort((a, b) => _extAlloc[b] != _extAlloc[a] ? _extAlloc[b].CompareTo(_extAlloc[a]) : _extEntries[b].CompareTo(_extEntries[a]));
        int count = Math.Min(JobStorage.MaxKinds, _extTouched.Count);
        var result = new UsageKind[count];
        for (int k = 0; k < count; k++)
        {
            int id = _extTouched[k];
            result[k] = new(_extensions[id], new(_extAlloc[id], _extEntries[id]));
        }
        return result;
    }

    private UsageIteration[] IterationList()
    {
        if (_itTouched.Count == 0) return JobStorage.NoIterations;
        _itTouched.Sort();
        var result = new UsageIteration[_itTouched.Count];
        for (int k = 0; k < result.Length; k++)
        {
            int it = _itTouched[k];
            result[k] = new(it, new(_itAlloc[it], _itEntries[it]));
        }
        return result;
    }
}
