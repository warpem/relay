using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.InitialModel.InitialReference3D;
using Refund.Jobs.Refinement.Refinement3D.Refine3D;
using Refund.Utils;
using Refund.Utils.Star;
using Xunit.Abstractions;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public sealed class ParticleMergeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-merge-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public ParticleMergeTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
        JobRegistry.EnsurePopulated();
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string name, string content)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private ParticleSet Spa(string name, string rows = "1@stack.mrcs 7 1\n", bool reordered = false, bool opticsLast = false)
    {
        const string optics = "data_optics\nloop_\n_rlnOpticsGroup\n_rlnOpticsGroupName\n_rlnImagePixelSize\n7 opticsGroup1 1.5\n";
        string particles = "data_particles\nloop_\n" + (reordered
            ? "_rlnRandomSubset\n_rlnImageName\n_rlnOpticsGroup\n" : "_rlnImageName\n_rlnOpticsGroup\n_rlnRandomSubset\n") + rows;
        return new ParticleSet { HasData = true, ParticlesSingleStarPath = Write(name, opticsLast ? particles + optics : optics + particles) };
    }

    private ParticleSet Tomo(string name, string tomoName, string tilt = "0", string opticsId = "7")
    {
        string particles = $"""
            data_general
            _rlnTomoSubTomosAre2DStacks 1
            data_optics
            loop_
            _rlnOpticsGroup
            _rlnOpticsGroupName
            _rlnImagePixelSize
            {opticsId} opticsGroup1 1.5
            data_particles
            loop_
            _rlnImageName
            _rlnOpticsGroup
            _rlnTomoName
            _rlnTomoParticleId
            _rlnTomoParticleName
            1@stack.mrcs {opticsId} {tomoName} 1 {tomoName}/1
            """;
        string tomograms = $"""
            data_global
            loop_
            _rlnTomoName
            _rlnOpticsGroupName
            _rlnTomoFrameCount
            {tomoName} opticsGroup1 1
            data_{tomoName}
            loop_
            _rlnTomoYTilt
            _rlnOpticsGroup
            {tilt} {opticsId}
            """;
        return new ParticleSet
        {
            HasData = true, DataDimensionality = ParticleType.Tiltseries,
            ParticlesSingleStarPath = Write(name + "-particles.star", particles),
            TomogramsStarPath = Write(name + "-tomograms.star", tomograms),
            OptimisationSetStarPath = Write(name + "-optimisation.star", "data_\n_rlnTomoTrajectoriesFile unused-motion.star\n")
        };
    }

    private T Connect<T>(T job, params ParticleSet[] inputs) where T : Job
    {
        job.Id = 100;
        job.Space = new Space { RootDirectory = _root };
        int id = 1;
        foreach (var resource in inputs)
        {
            var producer = new Class2D { Id = id++, Space = job.Space };
            var port = job.PortsIn["Particles"];
            var source = new PortOut(producer, typeof(ParticleSet), "Particles", "Particles", _ => resource);
            port.Edges.Add(new Edge { Source = source, Target = port });
        }
        foreach (var input in job.PortsIn.Values.Where(p => p.ResourceType == typeof(MapList)))
        {
            var source = new PortOut(job, typeof(MapList), "Map", "Map", _ => new MapList([new Map(averageVolumePath: Path.Combine(_root, "map.mrc"))]));
            input.Edges.Add(new Edge { Source = source, Target = input });
        }
        return job;
    }

    private static List<Dictionary<string, string>> Rows(string path, string table)
    {
        var file = new StreamingStar(path, default);
        var block = file.Blocks.Single(b => b.Name == table);
        var rows = new List<Dictionary<string, string>>();
        file.ReadRows(block, row => rows.Add(block.Columns.Select((c, i) => (c, Value: StreamingStar.Value(row[i])))
            .ToDictionary(p => p.c, p => p.Value)), default);
        return rows;
    }

    [Fact]
    public void AppendsEveryRowReordersColumnsAndRemapsOnlyOptics()
    {
        var a = Spa("a.star", "1@stack.mrcs 7 1\n1@stack.mrcs 7 1\n", opticsLast: true);
        var b = Spa("b.star", "2 1@stack.mrcs 7\n", reordered: true);
        var job = Connect(new Class2D(), a, b);
        a.ItemCount = 2; b.ItemCount = 1;
        var description = ParticleInputs.Describe(job.PortsIn["Particles"]);
        Assert.Equal(3, description.ItemCount);
        Assert.False(File.Exists(description.ParticlesSingleStarPath));
        Assert.Empty(job.ValidatePortInputs());
        job.Stage(CancellationToken.None);
        var rows = Rows(description.ParticlesSingleStarPath, "particles");
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("1@stack.mrcs", r["rlnImageName"]));
        Assert.Equal(new[] { "1", "1", "2" }, rows.Select(r => r["rlnOpticsGroup"]));
        Assert.Equal(new[] { "1", "1", "2" }, rows.Select(r => r["rlnRandomSubset"]));
        var optics = Rows(description.ParticlesSingleStarPath, "optics");
        Assert.Equal(2, optics.Select(r => r["rlnOpticsGroupName"]).Distinct().Count());
        Assert.Equal("100/inputs/Particles/particles.star", job.ComposeCommandArguments()["i"]);
        var output = (ParticleSet)job.PortsOut["Particles"].GetResource();
        Assert.NotEqual(a.ParticlesSingleStarPath, output.ParticlesSingleStarPath);
        Assert.EndsWith("a.star", a.ParticlesSingleStarPath); // output descriptions must not mutate inputs
    }

    [Fact]
    public void AllMultiInputConsumersAndWorkersUseMergedPaths()
    {
        Job[] jobs = [new Class2D(), new Class3D(), new Class3DSupervised(), new Class3D { UseWorkerPool = true },
            new Refine3D(), new Refine3D { UseWorkerPool = true }, new InitialReference()];
        foreach (var job in jobs)
        {
            Connect(job, Spa("a.star"), Spa("b.star"));
            Assert.Equal("100/inputs/Particles/particles.star", job.ComposeCommandArguments()["i"]);
            if (job is IPooledJob pooled && job is IPoolStatus { IsPooled: true })
                Assert.Contains("100/inputs/Particles/particles.star", pooled.GetWorkerCommand(0));
        }
    }

    [Fact]
    public void SingleInputDoesNotReadOrCopyFiles()
    {
        var resource = new ParticleSet { HasData = true, ParticlesSingleStarPath = Path.Combine(_root, "nonexistent.star") };
        var job = Connect(new Class2D(), resource);
        job.Stage(CancellationToken.None);
        Assert.Equal("nonexistent.star", job.ComposeCommandArguments()["i"]);
        Assert.False(Directory.Exists(ParticleInputs.DirectoryFor(job.PortsIn["Particles"])));
    }

    [Fact]
    public void MetadataErrorsIdentifyBothSourcesWithoutReadingFiles()
    {
        var a = new ParticleSet { HasData = true, ParticlesSingleStarPath = "/absent/a.star" };
        var b = new ParticleSet { HasData = true, ParticlesSingleStarPath = "/absent/b.star", DataDimensionality = ParticleType.Tomogram };
        var job = Connect(new Class3D(), a, b);
        var errors = job.ValidatePortInputs()["Particles"];
        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Contains("particle representation", e));
        Assert.Contains(errors, e => e.StartsWith("J1"));
        Assert.Contains(errors, e => e.StartsWith("J2"));
        job.PortsIn["Particles"].IsActiveDelegate = _ => false;
        Assert.Empty(job.ValidatePortInputs());
    }

    [Theory]
    [InlineData("_rlnRandomSubset", "_customColumn", "columns")]
    [InlineData("1@stack.mrcs 7 1", "1@stack.mrcs 99 1", "undefined optics")]
    [InlineData("1@stack.mrcs 7 1", "1@stack.mrcs 7", "incomplete row")]
    public void FileErrorsOccurAtStagingAndNeverPublishPartialOutput(string oldText, string replacement, string error)
    {
        var a = Spa("a.star"); var b = Spa("b.star");
        File.WriteAllText(b.ParticlesSingleStarPath, File.ReadAllText(b.ParticlesSingleStarPath).Replace(oldText, replacement));
        var job = Connect(new Class2D(), a, b);
        Assert.Empty(job.ValidatePortInputs());
        var ex = Assert.Throws<InvalidDataException>(() => job.Stage(CancellationToken.None));
        Assert.Contains(error, ex.ToString());
        Assert.Contains("J2", ex.Message);
        Assert.False(Directory.Exists(ParticleInputs.DirectoryFor(job.PortsIn["Particles"])));
        Assert.Empty(Directory.GetDirectories(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void TomogramDefinitionsAndOpticsReferencesAreMergedTogether()
    {
        var job = Connect(new Refine3D(), Tomo("a", "1"), Tomo("b", "2"));
        job.Stage(CancellationToken.None);
        var input = ParticleInputs.Describe(job.PortsIn["Particles"]);
        Assert.Equal(new[] { "1", "2" }, Rows(input.ParticlesSingleStarPath, "particles").Select(r => r["rlnOpticsGroup"]));
        Assert.Equal(new[] { "1", "2" }, Rows(input.ParticlesSingleStarPath, "particles").Select(r => r["rlnTomoName"]));
        var global = Rows(input.TomogramsStarPath, "global");
        Assert.Equal(new[] { "opticsGroup1", "opticsGroup1_relay2" }, global.Select(r => r["rlnOpticsGroupName"]));
        Assert.Single(Rows(input.TomogramsStarPath, "1"));
        Assert.Equal("2", Assert.Single(Rows(input.TomogramsStarPath, "2"))["rlnOpticsGroup"]);
        string optimisation = File.ReadAllText(input.OptimisationSetStarPath);
        Assert.Contains("100/inputs/Particles/particles.star", optimisation);
        Assert.Contains("100/inputs/Particles/tomograms.star", optimisation);
        Assert.DoesNotContain("Trajectories", optimisation);
        Assert.Equal("100/inputs/Particles/optimisation_set.star", job.ComposeCommandArguments()["ios"]);
        var output = (ParticleSet)job.PortsOut["Particles"].GetResource();
        Assert.Equal(input.TomogramsStarPath, output.TomogramsStarPath);
    }

    [Fact]
    public void SharedTomogramsReuseDefinitionsButNeverRemoveParticleRows()
    {
        var job = Connect(new Class3D(), Tomo("a", "ts1"), Tomo("b", "ts1", opticsId: "42"));
        job.Stage(CancellationToken.None);
        var input = ParticleInputs.Describe(job.PortsIn["Particles"]);
        Assert.Single(Rows(input.TomogramsStarPath, "global"));
        Assert.Single(Rows(input.ParticlesSingleStarPath, "optics"));
        var rows = Rows(input.ParticlesSingleStarPath, "particles");
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("ts1/1", row["rlnTomoParticleName"]));
        Assert.All(rows, row => Assert.Equal("1", row["rlnOpticsGroup"]));
    }

    [Fact]
    public void ConflictingTomogramsFailWithTheirNameAndSources()
    {
        var job = Connect(new Class3D(), Tomo("a", "ts1"), Tomo("b", "ts1", tilt: "30"));
        var ex = Assert.Throws<InvalidDataException>(() => job.Stage(CancellationToken.None));
        Assert.Contains("tomogram 'ts1'", ex.Message);
        Assert.Contains("J1", ex.Message);
        Assert.Contains("J2", ex.Message);
    }

    [Fact]
    public void ScalarConflictsAreNotSilentlyDropped()
    {
        var a = Tomo("a", "ts1"); var b = Tomo("b", "ts2");
        File.WriteAllText(b.ParticlesSingleStarPath, File.ReadAllText(b.ParticlesSingleStarPath).Replace("Are2DStacks 1", "Are2DStacks 0"));
        var job = Connect(new Class3D(), a, b);
        Assert.Contains("conflicting scalar", Assert.Throws<InvalidDataException>(() => job.Stage(CancellationToken.None)).Message);
    }

    [Fact]
    public void LexerPreservesQuotedMultilineAndUnknownValues()
    {
        const string text = """
            data_
            loop_
            _rlnImageName
            _custom
            '1@stack with spaces.mrcs' "data_not_a_block"
            "2@stack.mrcs"
            ;a long
            text field # not a comment
            ;
            # a comment
            '3@ü.mrcs' 'loop_'
            """;
        var a = new ParticleSet { HasData = true, ParticlesSingleStarPath = Write("a.star", text) };
        var b = new ParticleSet { HasData = true, ParticlesSingleStarPath = Write("b.star", text) };
        var job = Connect(new Class2D(), a, b);
        job.Stage(CancellationToken.None);
        var rows = Rows(ParticleInputs.Describe(job.PortsIn["Particles"]).ParticlesSingleStarPath, "particles");
        Assert.Equal(6, rows.Count);
        Assert.Equal("1@stack with spaces.mrcs", rows[0]["rlnImageName"]);
        Assert.Equal("a long\ntext field # not a comment", rows[1]["custom"]);
        Assert.Equal("3@ü.mrcs", rows[2]["rlnImageName"]);
    }

    [Fact]
    public void CancellationLeavesNoPublishedBundle()
    {
        var job = Connect(new Class2D(), Spa("a.star"), Spa("b.star"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => job.Stage(cancellation.Token));
        Assert.False(Directory.Exists(ParticleInputs.DirectoryFor(job.PortsIn["Particles"])));
    }

    [Theory]
    [InlineData(65534, false)]
    [InlineData(65535, false)]
    [InlineData(65536, false)]
    [InlineData(131073, false)]
    [InlineData(65534, true)]
    [InlineData(65535, true)]
    [InlineData(65536, true)]
    [InlineData(131073, true)]
    public void ByteRewritingPreservesValuesAcrossBufferBoundaries(int width, bool reorder)
    {
        string padding = new('x', width);
        string[][] rows =
        [
            [padding + "ü😀", "1.2300e+02"],
            ["'" + padding + "ü😀'", "\"loop_\""],
            [";" + padding + "\r\nβ # not a comment\r\n;", "\"quote'within\""],
            ["'_unknown'", "';literal'"]
        ];
        using var source = new StringWriter();
        source.WriteLine("#" + new string('c', 65537)); // A comment spanning read buffers.
        source.WriteLine("data_particles\nloop_\n_rlnImageName\n_custom");
        foreach (var row in rows.Take(3)) StreamingStar.WriteRow(source, row);
        source.Write("'_unknown' ;literal\n"); // Must remain a literal when moved to the first column.
        string path = Write("boundaries.star", source.ToString());
        var file = new StreamingStar(path, default);
        var block = Assert.Single(file.Blocks);
        Assert.Equal(rows.Length, block.RowCount);
        int index = 0;
        file.ReadRows(block, row => Assert.Equal(rows[index++], row), default);

        int[] order = reorder ? [1, 0] : [0, 1];
        using var actual = new MemoryStream();
        file.RewriteRows(block, actual, order, new StreamingStar.RewriteValue[2], default);
        using var expected = new StringWriter();
        foreach (var row in rows) StreamingStar.WriteRow(expected, order.Select(c => row[c]));
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(expected.ToString()), actual.ToArray());
    }

    [Fact]
    public void ByteRewritingStillRejectsInvalidUtf8InUninterpretedColumns()
    {
        string path = Write("invalid.star", "data_particles\nloop_\n_rlnImageName\n_custom\n1@stack.mrcs ");
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write([0xc3, 0x28, 0x0a]);
        var file = new StreamingStar(path, default);
        Assert.Throws<System.Text.DecoderFallbackException>(() => file.RewriteRows(file.Blocks[0], Stream.Null,
            [0, 1], new StreamingStar.RewriteValue[2], default));
    }

    [Fact]
    public void ManyColumnMergeDoesNotAllocatePerCell()
    {
        string path = Path.Combine(_root, "many-columns.star");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("data_optics\nloop_\n_rlnOpticsGroup\n_rlnOpticsGroupName\n7 optics1");
            writer.WriteLine("data_particles\nloop_\n_rlnImageName\n_rlnOpticsGroup");
            for (int c = 2; c < 40; c++) writer.WriteLine("_custom" + c);
            string row = "1@stack.mrcs 7 " + string.Join(' ', Enumerable.Repeat("123.456789", 38));
            for (int r = 0; r < 20_000; r++) writer.WriteLine(row);
        }
        var resource = new ParticleSet { HasData = true, ParticlesSingleStarPath = path };
        var job = Connect(new Class2D(), resource, resource);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        job.Stage(CancellationToken.None);
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        _output.WriteLine($"40-column merge allocated {allocated:N0} bytes.");
        // 1.6 million cells would allocate tens of MB if decoded to individual strings.
        Assert.InRange(allocated, 0, 8 * 1024 * 1024);
        Assert.Equal(40_000, StarItemCounter.CountParticles(ParticleInputs.Describe(job.PortsIn["Particles"]).ParticlesSingleStarPath));
    }

    [Fact]
    public void OldSingleResourceAccessorCannotSilentlyDropParticleInputs()
    {
        var job = Connect(new Class2D(), Spa("a.star"), Spa("b.star"));
        Assert.Throws<InvalidOperationException>(() => job.PortsIn["Particles"].GetSingleResource<ParticleSet>());
    }

    [Fact]
    public void ContinuationsRetainOriginalMergedPathsWithoutCopyingInputs()
    {
        var previous = Connect(new Class3D(), Tomo("a", "ts1"), Tomo("b", "ts2"));
        previous.Stage(CancellationToken.None);
        File.WriteAllText(Path.Combine(previous.DirectoryPath, "run_it001_data.star"), "result");
        var continuation = new Class3DContinue { Id = 101, Space = previous.Space };
        var input = continuation.PortsIn[Class3DContinue.PortInOptimizer];
        var source = previous.PortsOut.Values.Single(p => p.ResourceType == typeof(ContinuableClass3D));
        input.Edges.Add(new Edge { Source = source, Target = input });
        continuation.Stage(CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(continuation.DirectoryPath, "run_it001_data.star")));
        Assert.False(Directory.Exists(Path.Combine(continuation.DirectoryPath, "inputs")));
        var output = (ParticleSet)continuation.PortsOut["Particles"].GetResource();
        Assert.Equal(ParticleInputs.Describe(previous.PortsIn["Particles"]).TomogramsStarPath, output.TomogramsStarPath);
    }

    [Fact]
    public void StreamingReaderCancelsDuringRowsAndDetectsChangedSources()
    {
        string path = Write("rows.star", "data_particles\nloop_\n_rlnImageName\n" + string.Concat(Enumerable.Repeat("1@stack.mrcs\n", 20000)));
        var file = new StreamingStar(path, default);
        using var cancellation = new CancellationTokenSource();
        int rows = 0;
        Assert.Throws<OperationCanceledException>(() => file.ReadRows(file.Blocks[0], _ =>
        {
            rows++;
            cancellation.Cancel();
        }, cancellation.Token));
        Assert.InRange(rows, 1, 10000);
        File.AppendAllText(path, "2@stack.mrcs\n");
        Assert.Throws<IOException>(file.EnsureUnchanged);
    }

    [Fact]
    public void ExtraTablesAndScalarSettingsArePreservedWithoutInterpretingCustomColumns()
    {
        const string content = "data_general\n_customSetting 'KEEP'\ndata_particles\nLOOP_\n_rlnImageName\n_custom\n1@a ;literal\nstop_\ndata_extra\nloop_\n_arbitrary\n ;first\n'LOOP_'\n";
        var a = new ParticleSet { HasData = true, ParticlesSingleStarPath = Write("a.star", content) };
        var b = new ParticleSet { HasData = true, ParticlesSingleStarPath = Write("b.star", content) };
        var job = Connect(new Class2D(), a, b);
        job.Stage(CancellationToken.None);
        string merged = ParticleInputs.Describe(job.PortsIn["Particles"]).ParticlesSingleStarPath;
        Assert.Equal(new[] { ";first", "LOOP_", ";first", "LOOP_" }, Rows(merged, "extra").Select(row => row["arbitrary"]));
        Assert.All(Rows(merged, "particles"), row => Assert.Equal(";literal", row["custom"]));
        Assert.Contains("_customSetting 'KEEP'", File.ReadAllText(merged));
    }

    [Fact]
    public void LargeParticleLoopsDoNotAccumulateRows()
    {
        // Set RELAY_MERGE_BENCHMARK_ROWS=2200000 for a >2 GB input, without constructing it in memory.
        int count = int.TryParse(Environment.GetEnvironmentVariable("RELAY_MERGE_BENCHMARK_ROWS"), out int configured) ? configured : 20_000;
        string path = Path.Combine(_root, "large.star");
        string row = "1@stack.mrcs " + new string('x', 1000) + "\n";
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("data_particles\nloop_\n_rlnImageName\n_custom");
            for (int i = 0; i < count; i++) writer.Write(row);
        }
        var resource = new ParticleSet { HasData = true, ParticlesSingleStarPath = path };
        var job = Connect(new Class2D(), resource, resource);
        GC.Collect();
        long baseline = GC.GetTotalMemory(true), peak = baseline;
        using var timer = new Timer(_ => { long memory = GC.GetTotalMemory(false); InterlockedExtensions.Max(ref peak, memory); }, null, 0, 10);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        job.Stage(CancellationToken.None);
        watch.Stop();
        timer.Change(Timeout.Infinite, Timeout.Infinite);
        string output = ParticleInputs.Describe(job.PortsIn["Particles"]).ParticlesSingleStarPath;
        Assert.Equal(count * 2L, StarItemCounter.CountParticles(output));
        _output.WriteLine($"Input: {new FileInfo(path).Length:N0} bytes; output: {new FileInfo(output).Length:N0} bytes; merge: {watch.Elapsed}; sampled managed heap growth: {peak - baseline:N0} bytes.");
        // A row-retaining implementation needs multiple times the input size. Allow GC buffers,
        // but cap the observed heap independently of the optional multi-GB input size.
        Assert.InRange(peak - baseline, 0, 256L * 1024 * 1024);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref long location, long value)
        {
            long previous;
            do { previous = Interlocked.Read(ref location); if (previous >= value) return; }
            while (Interlocked.CompareExchange(ref location, value, previous) != previous);
        }
    }
}
