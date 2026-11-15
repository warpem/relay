using System.Buffers.Binary;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;

namespace Refund.Services.Storage;

/// <summary>Broad pipeline stages, the treemap's "Stage" colours. Fewer than four hues stay distinguishable side by side.</summary>
public enum StorageStage { Preprocessing, Particles, Refinement, Other }

public static class StorageStages
{
    public static readonly string[] Names = ["Preprocessing", "Particles", "Refinement", "Other"];

    /// <summary>The stage of a job type, from its category ("Tilt-series.Reconstruction.Tomograms").</summary>
    public static StorageStage Of(string typeCategory)
    {
        var parts = (typeCategory ?? "").Split('.');
        string top = parts.Length > 0 ? parts[0] : "", group = parts.Length > 1 ? parts[1] : "", leaf = parts.Length > 2 ? parts[2] : "";
        if (top.Contains("Beam") || top == "M" || top == "Refinement") return StorageStage.Refinement;
        if (top == "Common")
            return group == "Import" && leaf.StartsWith("Particle") ? StorageStage.Particles
                : group == "Tools" && leaf == "Expand symmetry" ? StorageStage.Refinement : StorageStage.Other;
        if (group is "Picking" or "Extraction") return StorageStage.Particles;
        if (group == "Export") return leaf.Contains("particles") ? StorageStage.Particles : StorageStage.Preprocessing;
        if (group == "Selection") return leaf == "Select particles" ? StorageStage.Particles : StorageStage.Preprocessing;
        if (group == "Reconstruction" && leaf == "Map") return StorageStage.Refinement;
        if (top is "Frame-series" or "Tilt-series") return StorageStage.Preprocessing;
        return StorageStage.Other;
    }
}

/// <summary>One job in the storage overlay.</summary>
public sealed class StorageRow
{
    public required ReadOnlyJob Job { get; init; }
    public required ReadOnlySpace Space { get; init; }
    /// <summary>Folders (and factory instance) the job sits in, from the space's first view that shows it.</summary>
    public required string[] Folders { get; init; }
    public ReadOnlyUser Person { get; init; }
    public JobStorage Storage { get; init; }
    public UsagePart Cleanable { get; init; }
    public int UsedBy { get; init; }
    public StorageStage Stage { get; init; }
    public string TypeName { get; init; }

    public long Allocated => Storage?.Total.Allocated ?? 0;
    public long Entries => Storage?.Total.Entries ?? 0;
    public bool Measured => Storage != null;
    public DateTime? Modified => Storage?.NewestWrite;
    public string Location => Folders.Length == 0 ? Space.QualifiedName : $"{Space.QualifiedName} › {string.Join(" › ", Folders)}";
}

/// <summary>A leftover folder in a space the user can see.</summary>
public sealed record StorageLeftoverRow(ReadOnlySpace Space, SpaceLeftover Leftover);

/// <summary>Which jobs the overlay shows.</summary>
public sealed class StorageFilter
{
    public HashSet<string> Types { get; set; } = [];
    public int? PersonId { get; set; }
    public HashSet<StorageStatus> Statuses { get; set; } = [];
    /// <summary>Only jobs whose newest file is older than this many months (0: any).</summary>
    public int OlderThanMonths { get; set; }
    public string Search { get; set; } = "";
    public int? ProjectId { get; set; }
    public int? SpaceId { get; set; }
    /// <summary>Folder path within the space (aliases), when zoomed into a folder.</summary>
    public string[] Folders { get; set; } = [];

    public StorageFilter Copy() => new()
    {
        Types = [.. Types], PersonId = PersonId, Statuses = [.. Statuses], OlderThanMonths = OlderThanMonths, Search = Search,
        ProjectId = ProjectId, SpaceId = SpaceId, Folders = [.. Folders],
    };
}

/// <summary>Status groups for filtering.</summary>
public enum StorageStatus { Finished, FailedOrAborted, Active, NeverRun }

/// <summary>Totals of a set of rows.</summary>
public readonly record struct StorageTotals(int Jobs, int Measured, long Allocated, long Entries, UsagePart Cleanable);

/// <summary>A line of the "by type" or "by person" breakdown.</summary>
public sealed record StorageBreakdownLine(string Key, string Name, int Jobs, long Allocated, long Entries, UsagePart Cleanable, string Group = "");

/// <summary>
/// Builds what the storage overlay shows from the live model: rows for every job the user can see, filtered, with totals,
/// breakdowns and the treemap's payload.
/// </summary>
public static class StorageView
{
    public static List<StorageRow> Rows(DataManager dataManager, ReadOnlyUser user)
    {
        var rows = new List<StorageRow>();
        foreach (var project in dataManager.GetUserProjects(user))
            foreach (var space in project.Spaces)
            {
                var jobs = space.Jobs;
                var folders = FolderPaths(space);
                var children = new Dictionary<int, int>();
                foreach (var job in jobs)
                    foreach (var parent in job.Mutable.GetParents())
                        children[parent.Id] = children.GetValueOrDefault(parent.Id) + 1;
                foreach (var job in jobs)
                {
                    if (job.Mutable.IsBlueprint) continue;
                    rows.Add(new StorageRow
                    {
                        Job = job, Space = space, Folders = folders.GetValueOrDefault(job.Id, []),
                        Person = JobActivity.Describe(job)?.Author, Storage = job.Storage, Cleanable = DataManager.Cleanable(job),
                        UsedBy = children.GetValueOrDefault(job.Id), Stage = StorageStages.Of(job.Mutable.TypeCategory), TypeName = job.TypeName,
                    });
                }
            }
        return rows;
    }

    public static List<StorageLeftoverRow> Leftovers(DataManager dataManager, ReadOnlyUser user) =>
        dataManager.GetUserProjects(user).SelectMany(p => p.Spaces)
            .SelectMany(s => (s.Storage?.Leftovers ?? SpaceStorage.NoLeftovers).Select(l => new StorageLeftoverRow(s, l)))
            .OrderByDescending(l => l.Leftover.Total.Allocated).ToList();

    // Folder aliases (outermost first) of each job, from the first view that shows it.
    private static Dictionary<int, string[]> FolderPaths(ReadOnlySpace space)
    {
        var result = new Dictionary<int, string[]>();
        foreach (var view in space.Views)
            foreach (var job in view.Jobs)
            {
                if (result.ContainsKey(job.Id)) continue;
                var chain = new List<string>();
                var instance = job.FactoryInstanceId is { } id ? view.FindFactoryInstance(id) : null;
                var folder = instance != null
                    ? view.Folders.FirstOrDefault(f => f.Items.OfType<ReadOnlyFactoryInstance>().Any(i => i.Id == instance.Id))
                    : view.FindFolderContainingJob(job.Id);
                if (instance != null) chain.Add(string.IsNullOrWhiteSpace(instance.Alias) ? $"Factory {instance.Id}" : instance.Alias);
                for (var f = folder; f != null; f = f.Parent)
                    chain.Insert(0, string.IsNullOrWhiteSpace(f.Alias) ? $"Folder {f.Id}" : f.Alias);
                result[job.Id] = chain.ToArray();
            }
        return result;
    }

    public static StorageStatus StatusOf(JobStatus status) => status switch
    {
        JobStatus.Finished => StorageStatus.Finished,
        JobStatus.Failed or JobStatus.Aborted or JobStatus.Interrupted => StorageStatus.FailedOrAborted,
        JobStatus.Building => StorageStatus.NeverRun,
        _ => StorageStatus.Active,
    };

    public static IEnumerable<StorageRow> Apply(IEnumerable<StorageRow> rows, StorageFilter f, DateTime now)
    {
        var olderThan = f.OlderThanMonths > 0 ? now.AddMonths(-f.OlderThanMonths) : (DateTime?)null;
        string search = f.Search?.Trim() ?? "";
        foreach (var r in rows)
        {
            if (f.ProjectId is int p && r.Space.Project.Id != p) continue;
            if (f.SpaceId is int s && r.Space.Id != s) continue;
            if (f.Folders.Length > 0 && (r.Folders.Length < f.Folders.Length || !r.Folders.Take(f.Folders.Length).SequenceEqual(f.Folders))) continue;
            if (f.Types.Count > 0 && !f.Types.Contains(r.Job.TypeGuid)) continue;
            if (f.PersonId is int person && r.Person?.Id != person) continue;
            if (f.Statuses.Count > 0 && !f.Statuses.Contains(StatusOf(r.Job.Status))) continue;
            if (olderThan is { } limit && (r.Modified is not { } modified || modified > limit)) continue;
            if (search.Length > 0 && !Matches(r, search)) continue;
            yield return r;
        }
    }

    private static bool Matches(StorageRow r, string search) =>
        r.Job.QualifiedName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (r.Job.Alias ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (r.TypeName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
        $"J{r.Job.Id}".Equals(search, StringComparison.OrdinalIgnoreCase) ||
        r.Job.Id.ToString() == search;

    public static StorageTotals Totals(IEnumerable<StorageRow> rows)
    {
        int jobs = 0, measured = 0; long allocated = 0, entries = 0; UsagePart cleanable = default;
        foreach (var r in rows)
        {
            jobs++;
            if (r.Measured) measured++;
            allocated += r.Allocated; entries += r.Entries; cleanable += r.Cleanable;
        }
        return new(jobs, measured, allocated, entries, cleanable);
    }

    public static List<StorageBreakdownLine> ByType(IEnumerable<StorageRow> rows) =>
        rows.GroupBy(r => r.Job.TypeGuid)
            .Select(g => new StorageBreakdownLine(g.Key, g.First().TypeName, g.Count(), g.Sum(r => r.Allocated), g.Sum(r => r.Entries),
                g.Aggregate(default(UsagePart), (s, r) => s + r.Cleanable), TypeGroup(g.First().Job.Mutable.TypeCategory)))
            .OrderByDescending(l => l.Allocated).ThenByDescending(l => l.Entries).ToList();

    /// <summary>The top level of a type category, without decoration ("💥 Beam" becomes "Beam").</summary>
    public static string TypeGroup(string category) =>
        ((category ?? "").Split('.')[0]).Replace("💥", "").Trim() is { Length: > 0 } g ? g : "Other";

    public static List<StorageBreakdownLine> ByPerson(IEnumerable<StorageRow> rows) =>
        rows.GroupBy(r => r.Person?.Id ?? 0)
            .Select(g => new StorageBreakdownLine(g.Key.ToString(), g.First().Person?.Name is { Length: > 0 } n ? n : g.First().Person?.Username ?? "Unknown",
                g.Count(), g.Sum(r => r.Allocated), g.Sum(r => r.Entries), g.Aggregate(default(UsagePart), (s, r) => s + r.Cleanable)))
            .OrderByDescending(l => l.Allocated).ThenByDescending(l => l.Entries).ToList();
}

/// <summary>What a treemap tile stands for.</summary>
public enum TreemapKind : byte { Root, Project, Space, Folder, Job, JobFolder, Files, Leftovers, Leftover, Others }

/// <summary>A treemap tile the overlay can act on (select a job, zoom into a group).</summary>
public sealed record TreemapTarget(TreemapKind Kind, int? ProjectId, int? SpaceId, string[] Folders, StorageRow Row, string Name);

/// <summary>
/// The treemap's data, packed for the browser: per node (32 bytes) parent index, allocated bytes, entries, days since
/// the newest write (-1: unknown), colour keys and kind; plus a label per node. Nodes too small to see at the canvas
/// size are folded into one "smaller items" node per parent, for either metric.
/// </summary>
public sealed class TreemapPayload
{
    public const int NodeBytes = 32;
    public byte[] Nodes { get; init; } = [];
    public string[] Labels { get; init; } = [];
    public string[] Details { get; init; } = [];
    public IReadOnlyList<TreemapTarget> Targets { get; init; } = [];
    public int Count => Labels.Length;

    private sealed class Node
    {
        public TreemapKind Kind;
        public string Label, Detail;
        public long Allocated, Entries;
        public double AgeDays = -1;
        public byte Stage = (byte)StorageStage.Other, Person, Age = 255;
        public TreemapTarget Target;
        public int Count;
        public readonly List<Node> Children = [];
        public Dictionary<string, Node> Named;

        public Node Child(string key, Func<Node> create)
        {
            Named ??= [];
            if (!Named.TryGetValue(key, out var child)) { Named[key] = child = create(); Children.Add(child); }
            return child;
        }
    }

    /// <summary>Colour slot of owners outside the top three.</summary>
    public const byte OtherOwner = 3;

    /// <summary>
    /// The three owners with the most storage among <paramref name="rows"/>, as colour slots. Ranked over everything the
    /// viewer can see rather than the current filter, so filtering never repaints anyone.
    /// </summary>
    public static Dictionary<int, byte> OwnerSlots(IEnumerable<StorageRow> rows) =>
        rows.Where(r => r.Person != null).GroupBy(r => r.Person.Id)
            .Select(g => (Id: g.Key, Allocated: g.Sum(r => r.Allocated)))
            .OrderByDescending(o => o.Allocated).ThenBy(o => o.Id).Take(3)
            .Select((o, i) => (o.Id, (byte)i)).ToDictionary(o => o.Id, o => o.Item2);

    /// <summary>Age bins, in days: under a month, 1–3 months, 3–6 months, 6–12 months, over a year.</summary>
    public static readonly double[] AgeBins = [30.44, 91.3, 182.6, 365.25];
    public static byte AgeBin(double days) => days < 0 ? (byte)255 : (byte)AgeBins.Count(b => days >= b);

    /// <param name="rows">Rows already filtered to the scope.</param>
    /// <param name="scope">The zoom root: null for everything, else the project, space or folder path shown.</param>
    /// <param name="owners">Colour slot (0–2) of the owners with the most storage; everyone else is "other" (3).</param>
    /// <param name="minShare">Smallest share of the root a node may have before it is folded (about 3 px² of the canvas).</param>
    public static TreemapPayload Build(IReadOnlyList<StorageRow> rows, IEnumerable<StorageLeftoverRow> leftovers, StorageFilter scope,
        IReadOnlyDictionary<int, byte> owners, DateTime now, double minShare)
    {
        var root = new Node { Kind = TreemapKind.Root, Label = "All projects", Target = new(TreemapKind.Root, null, null, [], null, "All projects") };
        foreach (var r in rows)
        {
            if (!r.Measured || r.Entries == 0) continue;
            var project = root.Child($"p{r.Space.Project.Id}", () => new Node
            {
                Kind = TreemapKind.Project, Label = r.Space.Project.QualifiedName,
                Target = new(TreemapKind.Project, r.Space.Project.Id, null, [], null, r.Space.Project.QualifiedName),
            });
            var space = project.Child($"s{r.Space.Id}", () => new Node
            {
                Kind = TreemapKind.Space, Label = r.Space.QualifiedName,
                Target = new(TreemapKind.Space, r.Space.Project.Id, r.Space.Id, [], null, r.Space.QualifiedName),
            });
            var parent = space;
            for (int i = 0; i < r.Folders.Length; i++)
            {
                var path = r.Folders[..(i + 1)];
                parent = parent.Child("f" + r.Folders[i], () => new Node
                {
                    Kind = TreemapKind.Folder, Label = path[^1],
                    Target = new(TreemapKind.Folder, r.Space.Project.Id, r.Space.Id, path, null, path[^1]),
                });
            }
            parent.Children.Add(JobNode(r, owners, now));
        }

        foreach (var l in leftovers)
        {
            if (scope.Folders.Length > 0 || l.Leftover.Total.Entries == 0) continue;
            if (scope.ProjectId is int pid && l.Space.Project.Id != pid) continue;
            if (scope.SpaceId is int sid && l.Space.Id != sid) continue;
            var project = root.Child($"p{l.Space.Project.Id}", () => new Node
            {
                Kind = TreemapKind.Project, Label = l.Space.Project.QualifiedName,
                Target = new(TreemapKind.Project, l.Space.Project.Id, null, [], null, l.Space.Project.QualifiedName),
            });
            var space = project.Child($"s{l.Space.Id}", () => new Node
            {
                Kind = TreemapKind.Space, Label = l.Space.QualifiedName,
                Target = new(TreemapKind.Space, l.Space.Project.Id, l.Space.Id, [], null, l.Space.QualifiedName),
            });
            var group = space.Child("leftovers", () => new Node { Kind = TreemapKind.Leftovers, Label = "Folders without a job" });
            double age = l.Leftover.NewestWrite is { } w ? Math.Max(0, (now - w).TotalDays) : -1;
            group.Children.Add(new Node
            {
                Kind = TreemapKind.Leftover, Label = l.Leftover.Name, Allocated = l.Leftover.Total.Allocated, Entries = l.Leftover.Total.Entries,
                AgeDays = age, Age = AgeBin(age),
                Detail = l.Leftover.JobId is int id ? $"Folder of deleted job {id}" : "Folder without a job",
            });
        }

        // Start at the zoomed scope.
        var start = root;
        if (scope.ProjectId is int p0 && start.Named?.GetValueOrDefault($"p{p0}") is { } pn) start = pn;
        if (scope.SpaceId is int s0 && start.Named?.GetValueOrDefault($"s{s0}") is { } sn) start = sn;
        foreach (var folder in scope.Folders)
            if (start.Named?.GetValueOrDefault("f" + folder) is { } fn) start = fn;

        Sum(start);
        var nodes = new List<(Node Node, int Parent)>();
        Flatten(start, -1, Math.Max(1, start.Allocated) * minShare, Math.Max(1, start.Entries) * minShare, nodes);

        var buffer = new byte[nodes.Count * NodeBytes];
        var labels = new string[nodes.Count];
        var details = new string[nodes.Count];
        var targets = new TreemapTarget[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            var (n, parent) = nodes[i];
            var span = buffer.AsSpan(i * NodeBytes, NodeBytes);
            BinaryPrimitives.WriteInt32LittleEndian(span, parent);
            BinaryPrimitives.WriteDoubleLittleEndian(span[4..], n.Children.Count == 0 ? n.Allocated : 0);
            BinaryPrimitives.WriteDoubleLittleEndian(span[12..], n.Children.Count == 0 ? n.Entries : 0);
            BinaryPrimitives.WriteSingleLittleEndian(span[20..], (float)n.AgeDays);
            span[24] = (byte)n.Kind; span[25] = n.Stage; span[26] = n.Person; span[27] = n.Age;
            labels[i] = n.Label ?? "";
            details[i] = n.Detail ?? "";
            targets[i] = n.Target;
        }
        return new TreemapPayload { Nodes = buffer, Labels = labels, Details = details, Targets = targets };
    }

    private static Node JobNode(StorageRow r, IReadOnlyDictionary<int, byte> owners, DateTime now)
    {
        var s = r.Storage;
        double age = s.NewestWrite is { } w ? Math.Max(0, (now - w).TotalDays) : -1;
        byte stage = (byte)r.Stage, person = r.Person is { } p && owners.TryGetValue(p.Id, out var slot) ? slot : OtherOwner, ageBin = AgeBin(age);
        string location = r.Location;
        var job = new Node
        {
            Kind = TreemapKind.Job, Label = r.Job.QualifiedName, Detail = $"{r.TypeName} · {location}",
            AgeDays = age, Stage = stage, Person = person, Age = ageBin,
            Target = new(TreemapKind.Job, r.Space.Project.Id, r.Space.Id, r.Folders, r, r.Job.QualifiedName),
        };
        // Tracked folders become nested tiles; what they don't cover is a "files" tile.
        var byPath = new Dictionary<string, Node>();
        Node Parent(string path)
        {
            int slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                if (byPath.TryGetValue(path[..slash], out var p)) return p;
                slash = path.LastIndexOf('/', slash - 1);
            }
            return job;
        }
        foreach (var f in s.Folders.OrderBy(f => f.Path.Count(c => c == '/')))
        {
            var node = new Node
            {
                Kind = TreemapKind.JobFolder, Label = f.Path[(f.Path.LastIndexOf('/') + 1)..], Detail = $"{r.Job.QualifiedName}/{f.Path}",
                Allocated = f.Usage.Allocated, Entries = f.Usage.Entries, AgeDays = age, Stage = stage, Person = person, Age = ageBin,
                Target = job.Target,
            };
            byPath[f.Path] = node;
            Parent(f.Path).Children.Add(node);
        }
        // RELION writes every iteration into the job folder itself: show what cleaning would remove as its own tile.
        if (byPath.Count == 0 && r.Cleanable.Entries > 0 && r.Cleanable.Allocated <= s.Total.Allocated)
            job.Children.Add(new Node
            {
                Kind = TreemapKind.JobFolder, Label = "Intermediate files", Detail = $"{r.Job.QualifiedName}: what cleaning intermediate files removes",
                Allocated = r.Cleanable.Allocated, Entries = r.Cleanable.Entries, AgeDays = age, Stage = stage, Person = person, Age = ageBin,
                Target = job.Target,
            });
        AddRest(job, s.Total.Allocated, s.Total.Entries);
        foreach (var node in byPath.Values) AddRest(node, node.Allocated, node.Entries);
        if (job.Children.Count == 0) { job.Allocated = s.Total.Allocated; job.Entries = s.Total.Entries; }
        return job;

        void AddRest(Node node, long allocated, long entries)
        {
            if (node.Children.Count == 0) return;
            long a = allocated - node.Children.Sum(c => c.Allocated), e = entries - node.Children.Sum(c => c.Entries);
            if (a > 0 || e > 0)
                node.Children.Add(new Node
                {
                    Kind = TreemapKind.Files, Label = "Other files", Detail = node.Detail ?? r.Job.QualifiedName, Allocated = Math.Max(0, a), Entries = Math.Max(0, e),
                    AgeDays = age, Stage = stage, Person = person, Age = ageBin, Target = job.Target,
                });
        }
    }

    private static void Sum(Node n)
    {
        if (n.Children.Count == 0) return;
        long a = 0, e = 0;
        foreach (var c in n.Children) { Sum(c); a += c.Allocated; e += c.Entries; }
        n.Allocated = a; n.Entries = e;
    }

    private static void Flatten(Node n, int parent, double minAllocated, double minEntries, List<(Node, int)> into)
    {
        int index = into.Count;
        into.Add((n, parent));
        if (n.Children.Count == 0) return;
        Node others = null;
        foreach (var c in n.Children.OrderByDescending(c => c.Allocated))
        {
            if (c.Allocated >= minAllocated || c.Entries >= minEntries)
                Flatten(c, index, minAllocated, minEntries, into);
            else
            {
                others ??= new Node { Kind = TreemapKind.Others, Label = "", Detail = n.Label, Stage = c.Stage, Person = c.Person, Age = c.Age, AgeDays = c.AgeDays, Target = n.Target };
                others.Allocated += c.Allocated; others.Entries += c.Entries;
                others.Count++;
            }
        }
        if (others != null)
        {
            others.Label = others.Count == 1 ? "1 smaller item" : $"{others.Count:N0} smaller items";
            into.Add((others, index));
        }
    }
}
