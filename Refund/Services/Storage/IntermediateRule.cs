using System.Globalization;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Refinement.Classes2D.BeamClass2D;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Refund.Jobs.Refinement.Classes3D.Class3DSelect;
using Refund.Jobs.Refinement.InitialModel.BeamInit3D;
using Refund.Jobs.Refinement.InitialModel.InitialReference3D;
using Refund.Jobs.Refinement.Refinement3D.BeamRefine3D;

namespace Refund.Services.Storage;

/// <summary>
/// How an iterative job type names its per-iteration files. Stateless and thread-safe: the storage walker tags every
/// entry while measuring, and cleaning uses the same tags to decide what to remove, so the two always agree.
/// </summary>
/// <remarks>
/// Tags: an iteration number (0 or more, as numbered on disk), <see cref="None"/>, <see cref="Regenerable"/>, or a
/// private state (at most <see cref="PrivateStates"/>) for folders whose contents the rule must look at. A folder tagged
/// with an iteration or Regenerable passes the tag on to everything inside it. Depth 0 is the job folder itself.
/// </remarks>
public abstract class IntermediateRule
{
    public const int None = -1, Regenerable = -2;
    protected const int PrivateStates = -16;

    public static bool IsIteration(int tag) => tag >= 0;
    /// <summary>Whether a folder with this tag hands it down to everything inside.</summary>
    public static bool Covers(int tag) => tag >= 0 || tag == Regenerable;
    /// <summary>Whether a folder with this tag holds entries the rule tags individually.</summary>
    public static bool IsPrivate(int tag) => tag <= PrivateStates;

    /// <summary>Tag of a folder found at <paramref name="depth"/> in a folder tagged <paramref name="parentTag"/>.</summary>
    public abstract int FolderTag(int parentTag, int depth, ReadOnlySpan<char> name);

    /// <summary>Tag of a file (or link) found at <paramref name="depth"/> in a folder tagged <paramref name="folderTag"/>.</summary>
    public abstract int FileTag(int folderTag, int depth, ReadOnlySpan<char> name);

    /// <summary>On-disk iterations the job itself still needs (what its outputs and continuations read).</summary>
    protected abstract IEnumerable<int> KeptByJob(Job job, IReadOnlyCollection<int> onDisk);

    /// <summary>Whether the highest iteration on disk is always kept (a safety net for unusual iteration counts).</summary>
    protected virtual bool KeepsHighest(Job job) => true;

    /// <summary>On-disk number of a logical iteration (what the UI and Class3DSelect count).</summary>
    public virtual int ToDisk(Job job, int logical) => logical;

    /// <summary>Logical iteration of an on-disk one.</summary>
    public virtual int ToLogical(Job job, int onDisk) => onDisk;

    /// <summary>
    /// The on-disk iterations to keep: what the job's outputs resolve to, the highest on disk, and every iteration a
    /// Class3DSelect child picked (its outputs keep pointing into that iteration).
    /// </summary>
    public HashSet<int> KeepSet(ReadOnlyJob job, IReadOnlyCollection<int> onDisk) => KeepSet(job.Mutable, onDisk);

    internal HashSet<int> KeepSet(Job job, IReadOnlyCollection<int> onDisk)
    {
        var keep = new HashSet<int>(KeptByJob(job, onDisk).Where(i => i >= 0));
        if (onDisk.Count > 0 && KeepsHighest(job))
            keep.Add(onDisk.Max());
        foreach (var child in job.GetChildren())
            if (child is Class3DSelect select && select.SelectedIteration >= 0)
                keep.Add(ToDisk(job, select.SelectedIteration));
        return keep;
    }

    /// <summary>Logical iterations whose result files survive cleaning with this keep set.</summary>
    public int[] KeptLogical(ReadOnlyJob job, IReadOnlySet<int> keep) =>
        keep.Select(i => ToLogical(job.Mutable, i)).Distinct().Order().ToArray();

    // ---- Name parsing shared by the rules ----

    /// <summary>Parses digits at the start of <paramref name="s"/>; returns the number and how many digits it used.</summary>
    protected static bool Digits(ReadOnlySpan<char> s, int min, out int value, out int length)
    {
        length = 0; value = 0;
        while (length < s.Length && length < 7 && char.IsAsciiDigit(s[length])) length++;
        if (length < min) return false;
        value = int.Parse(s[..length], NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>RELION's per-iteration names: run_itNNN_* and, after a continuation, run_ctN_itNNN_*.</summary>
    protected static int RelionIteration(ReadOnlySpan<char> name)
    {
        if (!name.StartsWith("run_")) return None;
        var rest = name[4..];
        if (rest.StartsWith("ct"))
        {
            if (!Digits(rest[2..], 1, out _, out int n) || rest.Length <= 2 + n || rest[2 + n] != '_') return None;
            rest = rest[(3 + n)..];
        }
        if (!rest.StartsWith("it") || !Digits(rest[2..], 3, out int it, out int len)) return None;
        return rest.Length > 2 + len && rest[2 + len] == '_' ? it : None;
    }

    /// <summary>"itNNN" folder names (Beam).</summary>
    protected static int IterationFolder(ReadOnlySpan<char> name) =>
        name.StartsWith("it") && Digits(name[2..], 3, out int it, out int len) && len == name.Length - 2 ? it : None;
}

/// <summary>The rules for every iterative job type that writes per-iteration files.</summary>
public static class IntermediateRules
{
    public static readonly IntermediateRule Class2D = new RelionRule(pool: false,
        kept: j => [((Class2D)j).VisAvailableIteration * ((Class2D)j).ResultsEveryNIterations],
        toDisk: (j, i) => i * ((Class2D)j).ResultsEveryNIterations,
        toLogical: (j, i) => i / Math.Max(1, ((Class2D)j).ResultsEveryNIterations));

    /// <summary>Class3D and its variants; Class3DContinue resumes from run_it{V}_optimiser.star, which V keeps.</summary>
    public static readonly IntermediateRule Class3D = new RelionRule(pool: true, kept: j => [j.VisAvailableIteration]);

    /// <summary>Refine3D: a normal finish publishes run_* files; after an abort its ports use run_it{V}_*.</summary>
    public static readonly IntermediateRule Refine3D = new RelionRule(pool: true, kept: j => [j.VisAvailableIteration]);

    public static readonly IntermediateRule InitialReference = new RelionRule(pool: false,
        kept: j => [j.VisAvailableIteration * ((InitialReference)j).ResultsEveryNIterations],
        toDisk: (j, i) => i * ((InitialReference)j).ResultsEveryNIterations,
        toLogical: (j, i) => i / Math.Max(1, ((InitialReference)j).ResultsEveryNIterations));

    /// <summary>BeamRefine3D and BeamClass3D: one beam/itNNN folder per iteration.</summary>
    public static readonly IntermediateRule BeamIterations = new BeamFolderRule();

    public static readonly IntermediateRule BeamInit3D = new BeamInitRule();

    public static readonly IntermediateRule BeamClass2D = new BeamSnapshotRule();

    /// <summary>RELION jobs: run_itNNN_* in the job folder; pooled runs leave regenerable worker files.</summary>
    private sealed class RelionRule(bool pool, Func<Job, IEnumerable<int>> kept,
        Func<Job, int, int> toDisk = null, Func<Job, int, int> toLogical = null) : IntermediateRule
    {
        public override int FolderTag(int parentTag, int depth, ReadOnlySpan<char> name) =>
            pool && depth == 0 && (name.SequenceEqual("pool") || name.SequenceEqual("worker_logs")) ? Regenerable : None;

        public override int FileTag(int folderTag, int depth, ReadOnlySpan<char> name)
        {
            if (depth != 0) return None;
            if (pool && name.SequenceEqual("worker_submit.sh")) return Regenerable;
            return RelionIteration(name);
        }

        protected override IEnumerable<int> KeptByJob(Job job, IReadOnlyCollection<int> onDisk) => kept(job);
        public override int ToDisk(Job job, int logical) => toDisk?.Invoke(job, logical) ?? logical;
        public override int ToLogical(Job job, int onDisk) => toLogical?.Invoke(job, onDisk) ?? onDisk;
    }

    private const int InsideBeam = -16, InsideSnapshots = -17;

    /// <summary>beam/itNNN/ holds everything of iteration NNN; beam/.itNNN.staging-* are abandoned partial iterations.</summary>
    private sealed class BeamFolderRule : IntermediateRule
    {
        public override int FolderTag(int parentTag, int depth, ReadOnlySpan<char> name)
        {
            if (depth == 0) return name.SequenceEqual("beam") ? InsideBeam : None;
            if (parentTag != InsideBeam || depth != 1) return None;
            if (name.StartsWith(".it") && name.Contains(".staging", StringComparison.Ordinal)) return Regenerable;
            return IterationFolder(name);
        }

        public override int FileTag(int folderTag, int depth, ReadOnlySpan<char> name) => None;

        protected override IEnumerable<int> KeptByJob(Job job, IReadOnlyCollection<int> onDisk)
        {
            var beam = (BeamParticleJob)job;
            return [Math.Max(beam.LastCommittedIteration, beam.VisAvailableIteration)];
        }
    }

    /// <summary>beam/itUUU_initial_model_class*.mrc snapshots, numbered by update; the final maps have plain names.</summary>
    private sealed class BeamInitRule : IntermediateRule
    {
        public override int FolderTag(int parentTag, int depth, ReadOnlySpan<char> name) =>
            depth == 0 && name.SequenceEqual("beam") ? InsideBeam : None;

        public override int FileTag(int folderTag, int depth, ReadOnlySpan<char> name)
        {
            if (folderTag != InsideBeam || depth != 1 || !name.StartsWith("it")) return None;
            if (!Digits(name[2..], 3, out int update, out int len)) return None;
            return name[(2 + len)..].StartsWith("_initial_model_class") ? update : None;
        }

        // Once the final maps exist, the outputs no longer read any snapshot.
        protected override bool KeepsHighest(Job job) => !((BeamInit3D)job).FinalAssignmentAvailable;

        protected override IEnumerable<int> KeptByJob(Job job, IReadOnlyCollection<int> onDisk)
        {
            var init = (BeamInit3D)job;
            if (init.FinalAssignmentAvailable) return [];
            return [ToDisk(job, Math.Max(init.LastCommittedIteration, init.VisAvailableIteration))];
        }

        public override int ToDisk(Job job, int logical) => ((BeamInit3D)job).UpdateForIteration(logical);
        public override int ToLogical(Job job, int onDisk) => ((BeamInit3D)job).IterationForUpdate(onDisk);
    }

    /// <summary>beam/snapshots/itNNN/ holds the class images and model of iteration NNN.</summary>
    private sealed class BeamSnapshotRule : IntermediateRule
    {
        public override int FolderTag(int parentTag, int depth, ReadOnlySpan<char> name)
        {
            if (depth == 0) return name.SequenceEqual("beam") ? InsideBeam : None;
            if (depth == 1 && parentTag == InsideBeam) return name.SequenceEqual("snapshots") ? InsideSnapshots : None;
            if (depth == 2 && parentTag == InsideSnapshots) return IterationFolder(name);
            return None;
        }

        public override int FileTag(int folderTag, int depth, ReadOnlySpan<char> name) => None;

        protected override IEnumerable<int> KeptByJob(Job job, IReadOnlyCollection<int> onDisk)
        {
            var beam = (BeamClass2D)job;
            return [beam.FinalAssignmentIteration, beam.VisAvailableIteration];
        }
    }
}
