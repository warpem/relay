using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs;
using Refund.Jobs.Fs.Extraction.ExtractParticles2D;
using Warp;

namespace Refund.Tests.Jobs;

public class ExtractParticles2DTests
{
    private const string Root = "/tmp/relay-extract2d-test";

    private static ExtractParticles2D Make()
    {
        JobRegistry.EnsurePopulated();
        return new ExtractParticles2D { Id = 7, Space = new Space { RootDirectory = Root }, BoxSize = 200, Diameter = 120 };
    }

    private static void Connect<T>(PortIn input, Func<T> resource) where T : Resource
    {
        var source = new PortOut(input.Job, typeof(T), "source", "Source", _ => resource());
        input.Edges.Add(new Edge { Source = source, Target = input });
    }

    private static MicrographSet Micrographs() => new()
    {
        DataSetFs = new DataSetFs { DataDirectory = Root + "/12/processing/input", FileSearchPattern = "*.tiff", PixelSize = 0.928M, BinFactor = 1 },
        HasMovies = true, HasMotion = true, HasCtf = true, HasMetadata = true,
        MetadataDirectory = Root + "/12/processing",
        InputListPath = Root + "/21/movies.txt"
    };

    private static ParticleSet Refined() => new()
    {
        ParticlesSingleStarPath = Root + "/16/beam/it025/run_it025_data.star",
        HasData = true, HasPositions = true, HasAngles = true, HasShifts = true, HasCtf = true,
        CoordPixelSize = 0.928M, DataDimensionality = ParticleType.Image, ItemCount = 780600
    };

    [Fact]
    public void Command_ExtractsFromTheMoviesWithTheMicrographsMetadata()
    {
        var job = Make();
        Connect(job.PortsIn[ExtractParticles2D.PortInMicrographs], Micrographs);
        Connect(job.PortsIn[ExtractParticles2D.PortInParticles], Refined);

        Assert.Equal("WarpTools fs_export_particles", job.CommandName);
        var args = job.ComposeCommandArguments();

        Assert.Equal("7/processing.settings", args["settings"]);
        Assert.Equal("21/movies.txt", args["input_data"]);
        Assert.Equal("12/processing", args["input_processing"]);
        Assert.Equal("7", args["output_processing"]);
        Assert.Equal("16/beam/it025/run_it025_data.star", args["input"]);
        Assert.Equal("0.928", args["angpix_coords"]);
        Assert.Equal("7/particles.star", args["output"]);
        Assert.Equal("", args["averages"]);
        Assert.Equal("200", args["box"]);
        Assert.Equal("120", args["diameter"]);
        Assert.Equal("", args["strict"]);
        Assert.False(args.ContainsKey("angpix_out"));
        Assert.False(args.ContainsKey("keep_ctf"));
        Assert.False(args.ContainsKey("dont_center"));

        job.AngpixOut = 1.5M;
        job.KeepCtf = true;
        args = job.ComposeCommandArguments();
        Assert.Equal("1.5", args["angpix_out"]);
        Assert.Equal("", args["keep_ctf"]);
    }

    [Fact]
    public void Command_TakesAFolderOfPositions()
    {
        var job = Make();
        Connect(job.PortsIn[ExtractParticles2D.PortInMicrographs], Micrographs);
        Connect(job.PortsIn[ExtractParticles2D.PortInPositions], () => new ParticleSet
        {
            ParticlesMultiStarDirectory = Root + "/5/matching", HasPositions = true, CoordPixelSize = 3.712M
        });

        var args = job.ComposeCommandArguments();

        Assert.Equal("5/matching", args["input"]);
        Assert.Equal("3.712", args["angpix_coords"]);
        // Without a list, WarpTools takes every movie in the import settings' folder.
        var micrographs = Micrographs();
        micrographs.InputListPath = "";
        job.PortsIn[ExtractParticles2D.PortInMicrographs].Edges.Clear();
        Connect(job.PortsIn[ExtractParticles2D.PortInMicrographs], () => micrographs);
        Assert.False(job.ComposeCommandArguments().ContainsKey("input_data"));
    }

    [Fact]
    public void Validation_NeedsExactlyOneParticleInputAndMovieMetadata()
    {
        var job = Make();
        Connect(job.PortsIn[ExtractParticles2D.PortInMicrographs], Micrographs);
        Assert.Contains(ExtractParticles2D.PortInParticles, job.ValidateInputs().Keys);

        Connect(job.PortsIn[ExtractParticles2D.PortInParticles], Refined);
        Assert.Empty(job.ValidateInputs());

        Connect(job.PortsIn[ExtractParticles2D.PortInPositions], Refined);
        Assert.Contains(ExtractParticles2D.PortInParticles, job.ValidateInputs().Keys);
        job.PortsIn[ExtractParticles2D.PortInPositions].Edges.Clear();

        job.BoxSize = 201;
        Assert.Contains(nameof(ExtractParticles2D.BoxSize), job.ValidateInputs().Keys);
        job.BoxSize = 200;

        job.AngpixOut = 0.5M;
        Assert.Contains(nameof(ExtractParticles2D.AngpixOut), job.ValidateInputs().Keys);
        job.AngpixOut = null;

        var noMetadata = Micrographs();
        noMetadata.MetadataDirectory = "";
        job.PortsIn[ExtractParticles2D.PortInMicrographs].Edges.Clear();
        Connect(job.PortsIn[ExtractParticles2D.PortInMicrographs], () => noMetadata);
        Assert.Contains(ExtractParticles2D.PortInMicrographs, job.ValidateInputs().Keys);
    }

    [Fact]
    public void Validation_RejectsNormalizedCoordinates()
    {
        var job = Make();
        Connect(job.PortsIn[ExtractParticles2D.PortInMicrographs], Micrographs);
        Connect(job.PortsIn[ExtractParticles2D.PortInPositions], () => new ParticleSet
        {
            ParticlesSingleStarPath = Root + "/picks.star", HasPositions = true, HasNormalizedCoords = true
        });

        Assert.Contains(ExtractParticles2D.PortInPositions, job.ValidateInputs().Keys);
    }

    [Fact]
    public void Output_IsReCenteredParticlesFromTheMicrographs()
    {
        var job = Make();
        var micrographs = Micrographs();
        Connect(job.PortsIn[ExtractParticles2D.PortInMicrographs], () => micrographs);
        Connect(job.PortsIn[ExtractParticles2D.PortInParticles], Refined);

        var particles = Assert.IsType<ParticleSet>(job.PortsOut[ExtractParticles2D.PortOutParticles].GetResource());

        Assert.Equal(job.ResParticlesStar, particles.ParticlesSingleStarPath);
        Assert.True(particles.HasData);
        Assert.True(particles.HasAngles);
        Assert.True(particles.HasCtf);
        Assert.False(particles.HasShifts);
        Assert.Null(particles.ItemCount);
        Assert.Equal(0.928M, particles.CoordPixelSize);
        Assert.Equal(120, particles.Diameter);
        Assert.Same(micrographs, particles.PickedInMicrographs);

        job.DontCenter = true;
        particles = Assert.IsType<ParticleSet>(job.PortsOut[ExtractParticles2D.PortOutParticles].GetResource());
        Assert.True(particles.HasShifts);
    }

    [Fact]
    public void Workers_GetCoresForDecodingMovies()
    {
        var job = Make();
        job.NGpus = 2;
        job.PerDevice = 2;
        Assert.Equal(16, job.CoreCount);

        var worker = ((IPooledJob)job).GetWorkerResourceValues("/tmp/worker-logs");
        Assert.Equal("8", worker["n_cores"]);
    }

    [Fact]
    public void Stage_WritesSettingsWhoseProcessingFolderIsTheJob()
    {
        string root = Path.Combine(Path.GetTempPath(), "relay-extract2d-" + Guid.NewGuid().ToString("N"));
        try
        {
            JobRegistry.EnsurePopulated();
            var job = new ExtractParticles2D { Id = 3, Space = new Space { RootDirectory = root } };
            Connect(job.PortsIn[ExtractParticles2D.PortInMicrographs], Micrographs);
            Connect(job.PortsIn[ExtractParticles2D.PortInParticles], Refined);

            job.Stage();

            var options = new OptionsWarp();
            options.Load(job.ResSettings);
            Assert.Equal(job.DirectoryPath, options.Import.ProcessingFolder);
            Assert.Equal(0.928M, options.Import.PixelSize);
            Assert.Equal("*.tiff", options.Import.Extension);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }
}
