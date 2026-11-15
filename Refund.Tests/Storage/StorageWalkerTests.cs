using System.Diagnostics;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.Jobs.Common.Notes.Note;
using Refund.Services.Storage;
using Refund.Services.Storage.Walking;

namespace Refund.Tests.Storage;

public sealed class StorageWalkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-storage-" + Guid.NewGuid().ToString("N"));

    public StorageWalkerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }
        Directory.Delete(_root, true);
    }

    public static TheoryData<bool> Walkers()
    {
        var data = new TheoryData<bool> { false };
        if (LinuxFs.IsSupported) data.Add(true);
        return data;
    }

    private string Write(string relative, int bytes)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static JobStorage Measure(string folder, bool native, IntermediateRule rule = null)
    {
        var state = new WalkState();
        state.Begin(rule, null, CancellationToken.None);
        bool ok = native ? LinuxWalker.Walk(folder, state) : ManagedWalker.Walk(folder, state);
        Assert.True(ok);
        return state.ToStorage(DateTime.UtcNow, 0, JobStatus.Finished, null);
    }

    [Theory, MemberData(nameof(Walkers))]
    public void CountsFilesFoldersHiddenFilesAndLinksWithoutFollowingThem(bool native)
    {
        string job = Path.Combine(_root, "job");
        string outside = Path.Combine(_root, "raw");
        Write("raw/movie.tif", 50_000);
        Write("raw/sub/more.tif", 50_000);
        Write("job/a/1.mrc", 1000);
        Write("job/a/b/.hidden", 10);
        Write("job/c.star", 20);
        File.CreateSymbolicLink(Path.Combine(job, "movie.tif"), Path.Combine(outside, "movie.tif"));
        Directory.CreateSymbolicLink(Path.Combine(job, "input"), outside);
        File.CreateSymbolicLink(Path.Combine(job, "dangling"), Path.Combine(_root, "nowhere"));

        var s = Measure(job, native);

        Assert.Equal(3, s.Total.Files);
        Assert.Equal(2, s.Total.Folders);
        Assert.Equal(3, s.Total.Links);
        Assert.Equal(1030, s.Total.Bytes);
        Assert.Equal(0, s.Total.Errors);
        Assert.Equal(8, s.Total.Entries);
        Assert.Contains(s.Kinds, k => k.Extension == ".mrc" && k.Usage.Entries == 1);
        Assert.Contains(s.Kinds, k => k.Extension == ".star");
        // A dot file has no extension.
        Assert.Contains(s.Kinds, k => k.Extension == "" && k.Usage.Entries == 1);
        Assert.NotNull(s.NewestWrite);
        Assert.True(s.Total.Allocated >= (native ? 0 : 1030));
    }

    [Theory, MemberData(nameof(Walkers))]
    public void TracksTheBiggestFoldersAsNestedPaths(bool native)
    {
        Write("job/processing/average/1.mrc", 100_000);
        Write("job/processing/average/2.mrc", 100_000);
        Write("job/processing/deep/er/still/x.bin", 50_000);
        Write("job/logs/run.log", 10);
        for (int i = 0; i < 30; i++) Write($"job/tiny{i}/x", 1);

        var s = Measure(Path.Combine(_root, "job"), native);
        var paths = s.Folders.Select(f => f.Path).ToList();

        Assert.True(s.Folders.Length <= JobStorage.MaxFolders);
        Assert.Equal("processing", paths[0]);
        Assert.Contains("processing/average", paths);
        // Only three levels are tracked; deeper contents count towards "processing/deep/er".
        Assert.Contains("processing/deep/er", paths);
        Assert.DoesNotContain(paths, p => p.Split('/').Length > WalkState.MaxTrackedDepth);
        var processing = s.Folders.Single(f => f.Path == "processing");
        Assert.True(processing.Usage.Allocated >= s.Folders.Single(f => f.Path == "processing/average").Usage.Allocated);
        // A parent never ranks below its child.
        Assert.True(paths.IndexOf("processing") < paths.IndexOf("processing/average"));
    }

    [Theory, MemberData(nameof(Walkers))]
    public void CountsUnreadableFoldersAsErrors(bool native)
    {
        if (OperatingSystem.IsWindows()) return;
        Write("job/locked/secret.bin", 100);
        Write("job/open.bin", 10);
        File.SetUnixFileMode(Path.Combine(_root, "job/locked"), UnixFileMode.None);
        try
        {
            if (Directory.EnumerateFileSystemEntries(Path.Combine(_root, "job/locked")).Any())
                return; // running as root: permissions don't apply
        }
        catch (UnauthorizedAccessException) { }

        var s = Measure(Path.Combine(_root, "job"), native);

        Assert.Equal(1, s.Total.Files);
        Assert.Equal(1, s.Total.Folders);
        Assert.True(s.Total.Errors >= 1);
    }

    [Fact]
    public void ANativeWalkCountsHardLinkedBytesOnceAndAgreesWithTheManagedWalker()
    {
        if (!LinuxFs.IsSupported) return;
        string a = Write("job/a.bin", 8192);
        Write("job/sub/b.bin", 100);
        File.CreateSymbolicLink(Path.Combine(_root, "job/link"), a);
        var link = Process.Start("ln", [a, Path.Combine(_root, "job/sub/a-again.bin")]);
        link!.WaitForExit();

        var native = Measure(Path.Combine(_root, "job"), true);
        var managed = Measure(Path.Combine(_root, "job"), false);

        Assert.Equal(3, native.Total.Files);
        Assert.Equal(8192 + 100, native.Total.Bytes);
        Assert.Equal(native.Total.Entries, managed.Total.Entries);
        Assert.Equal(native.Total.Folders, managed.Total.Folders);
        Assert.Equal(native.Total.Links, managed.Total.Links);
    }

    [Fact]
    public void AMissingFolderOrAFolderLinkIsNotWalked()
    {
        var state = new WalkState();
        state.Begin(null, null, CancellationToken.None);
        Assert.False(ManagedWalker.Walk(Path.Combine(_root, "missing"), state));
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "linked"), Path.Combine(_root, "real"));
        Assert.False(ManagedWalker.Walk(Path.Combine(_root, "linked"), state));
        if (LinuxFs.IsSupported)
            Assert.False(LinuxWalker.Walk(Path.Combine(_root, "linked"), state));
    }

    [Theory, MemberData(nameof(Walkers))]
    public void TagsRelionIterationsAndPoolFiles(bool native)
    {
        for (int it = 0; it <= 5; it++)
        {
            Write($"job/run_it{it:D3}_data.star", 1000);
            Write($"job/run_it{it:D3}_class001.mrc", 4000);
        }
        Write("job/run_ct2_it006_model.star", 10);
        Write("job/run_class001.mrc", 4000);
        Write("job/pool/worker1/out.txt", 500);
        Write("job/worker_submit.sh", 50);
        Write("job/inputs/particles.star", 300);

        var s = Measure(Path.Combine(_root, "job"), native, IntermediateRules.Class3D);

        Assert.Equal(Enumerable.Range(0, 7), s.Iterations.Select(i => i.Iteration));
        Assert.Equal(2, s.Iterations[0].Usage.Entries);
        // pool/, pool/worker1/, out.txt and worker_submit.sh
        Assert.Equal(4, s.Regenerable.Entries);
        var keep = new HashSet<int> { 5, 6 };
        Assert.Equal(5 * 2 + 4, s.Cleanable(keep).Entries);
    }

    [Theory]
    [InlineData("run_it025_data.star", 25)]
    [InlineData("run_it000_optimiser.star", 0)]
    [InlineData("run_it1234_model.star", 1234)]
    [InlineData("run_ct3_it012_model.star", 12)]
    [InlineData("run_data.star", -1)]
    [InlineData("run_it25_data.star", -1)]
    [InlineData("run_it025", -1)]
    [InlineData("other_it025_x", -1)]
    public void ParsesRelionIterationNames(string name, int expected) =>
        Assert.Equal(expected, IntermediateRules.Class2D.FileTag(IntermediateRule.None, 0, name));

    [Fact]
    public void BeamRulesTagIterationFoldersAndStaging()
    {
        var beam = IntermediateRules.BeamIterations;
        int inside = beam.FolderTag(IntermediateRule.None, 0, "beam");
        Assert.True(IntermediateRule.IsPrivate(inside));
        Assert.Equal(12, beam.FolderTag(inside, 1, "it012"));
        Assert.Equal(IntermediateRule.Regenerable, beam.FolderTag(inside, 1, ".it013.staging-abc"));
        Assert.Equal(IntermediateRule.None, beam.FolderTag(inside, 1, "warp_metadata"));
        Assert.Equal(IntermediateRule.None, beam.FolderTag(IntermediateRule.None, 0, "it012"));

        var snapshots = IntermediateRules.BeamClass2D;
        int b = snapshots.FolderTag(IntermediateRule.None, 0, "beam");
        int s = snapshots.FolderTag(b, 1, "snapshots");
        Assert.Equal(7, snapshots.FolderTag(s, 2, "it007"));
        Assert.Equal(IntermediateRule.None, snapshots.FolderTag(b, 1, "subclasses"));

        var init = IntermediateRules.BeamInit3D;
        int i = init.FolderTag(IntermediateRule.None, 0, "beam");
        Assert.Equal(150, init.FileTag(i, 1, "it150_initial_model_class001.mrc"));
        Assert.Equal(IntermediateRule.None, init.FileTag(i, 1, "initial_model_class001.mrc"));
    }

    [Fact]
    public void AMeasurementSurvivesSavingWithItsJobAndStaysSmall()
    {
        var storage = new JobStorage
        {
            MeasuredAt = new DateTime(2026, 11, 14, 10, 0, 0, DateTimeKind.Utc), DurationMs = 1234, Status = JobStatus.Finished,
            RunStartedAt = new DateTime(2026, 11, 10, 8, 30, 0, DateTimeKind.Utc), Exists = true,
            Total = new(123_456_789_012, 123_500_000_000, 50_000, 120, 3, 1), NewestWrite = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc),
            Folders = [new("processing", new(100_000_000_000, 40_000)), new("processing/with space", new(5, 1))],
            Kinds = [new(".mrc", new(90_000_000_000, 10_000)), new("", new(10, 2))],
            Iterations = Enumerable.Range(0, 25).Select(i => new UsageIteration(i, new(1_000_000_000L + i, 6))).ToArray(),
            Regenerable = new(5000, 3),
        };
        var job = new Note { Storage = storage };
        var json = new JsonObject();
        job.WriteToJson(json);
        var read = new Note();
        read.ReadFromJson(json);

        var r = read.Storage!;
        Assert.Equal(storage.MeasuredAt, r.MeasuredAt);
        Assert.Equal(storage.RunStartedAt, r.RunStartedAt);
        Assert.Equal(storage.Total, r.Total);
        Assert.Equal(storage.NewestWrite, r.NewestWrite);
        Assert.Equal(storage.Folders, r.Folders);
        Assert.Equal(storage.Kinds, r.Kinds);
        Assert.Equal(storage.Iterations, r.Iterations);
        Assert.Equal(storage.Regenerable, r.Regenerable);
        Assert.Equal(storage.Status, r.Status);

        // Indented the way space.relay is written: about a kilobyte for a 25-iteration job.
        string text = json["Storage"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        Assert.True(text.Length < 1500, $"{text.Length} characters");
        var big = new Note { Storage = new JobStorage { Exists = true, Iterations = Enumerable.Range(0, 200).Select(i => new UsageIteration(i, new(1_000_000_000, 6))).ToArray() } };
        var bigJson = new JsonObject();
        big.WriteToJson(bigJson);
        Assert.True(bigJson["Storage"]!.ToJsonString().Length < 4000);
    }

    [Fact]
    public void ClearingAndTemplatesDoNotCarryAMeasurement()
    {
        var job = new Note { Storage = new JobStorage { Exists = true }, KeptIterations = [3], HasBeenCleaned = true };
        var copy = new Note();
        copy.AdoptState(job);
        Assert.Null(copy.Storage);

        job.ClearProperties();
        Assert.Null(job.Storage);
        Assert.Empty(job.KeptIterations);
        Assert.False(job.HasBeenCleaned);
    }

    [Fact]
    public void ALeftoverListSurvivesSavingWithItsSpace()
    {
        var storage = new SpaceStorage
        {
            ListedAt = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc), LooseFiles = new(100, 4096, 2, 0, 0, 0),
            Leftovers = [new("123", 123, new(1, 2, 3, 4, 5, 6), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc)),
                         new("my old stuff", null, new(9, 9, 9, 9, 9, 0), null, new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc))],
        };
        var space = new Space { Storage = storage };
        var json = new JsonObject();
        space.WriteToJson(json);
        var read = new Space();
        read.ReadFromJson(json);

        Assert.Equal(storage.ListedAt, read.Storage!.ListedAt);
        Assert.Equal(storage.LooseFiles, read.Storage.LooseFiles);
        Assert.Equal(storage.Leftovers, read.Storage.Leftovers);
        Assert.True(read.Storage.Leftovers[0].IsDeletable);
        Assert.False(read.Storage.Leftovers[1].IsDeletable);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("012", null)]
    [InlineData("0", null)]
    [InlineData("12a", null)]
    [InlineData("1234567890", null)]
    public void OnlyPlainNumbersAreJobFolders(string name, int? id) => Assert.Equal(id, StorageLeftovers.JobIdOf(name));
}
