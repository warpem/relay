using Refund.DataModel;

namespace Refund.Tests.DataModel;

public class JobTaxonomyTests
{
    // PopulateTypes() is not idempotent (DefaultValues is never cleared), so a second
    // PopulateStatic() in the same process throws. Populate once, and tolerate the
    // registry already being populated by another test or by app startup.
    private static readonly object PopulateLock = new();

    private static void EnsurePopulated()
    {
        lock (PopulateLock)
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
    }

    // class short name -> expected TypeCategory (the locked taxonomy)
    private static readonly Dictionary<string, string> Expected = new()
    {
        // Frame-series
        ["ImportDataSetFs"]        = "Frame-series.Import.Frame series",
        ["Motion2D"]               = "Frame-series.Motion & CTF.Motion",
        ["CTF2D"]                  = "Frame-series.Motion & CTF.CTF",
        ["MotionAndCTF2D"]         = "Frame-series.Motion & CTF.Motion and CTF",
        ["BoxNetInference2D"]      = "Frame-series.Picking.BoxNet",
        ["ExtractParticles2D"]     = "Frame-series.Extraction.Extract particles",

        // Tilt-series
        ["ImportDataSetTs"]        = "Tilt-series.Import.Tilt series",
        ["AlignAretomo"]           = "Tilt-series.Alignment.AreTomo",
        ["AlignEtomo"]             = "Tilt-series.Alignment.Etomo patch tracking",
        ["AlignMiss"]              = "Tilt-series.Alignment.MISS patch tracking",
        ["AutoLevel"]              = "Tilt-series.Alignment.Auto-level",
        ["PeakAlign"]              = "Tilt-series.Alignment.Peak alignment",
        ["StackTilts"]             = "Tilt-series.Alignment.Stack tilts",
        ["ImportAlignments"]       = "Tilt-series.Alignment.Import alignments",
        ["Ctf"]                    = "Tilt-series.CTF.CTF",
        ["ReconstructTomograms"]   = "Tilt-series.Reconstruction.Tomograms",
        ["ReconstructMap"]         = "Tilt-series.Reconstruction.Map",
        ["Denoising"]              = "Tilt-series.Reconstruction.Denoising",
        ["TemplateMatch"]          = "Tilt-series.Picking.Template matching",
        ["ExtractParticles"]       = "Tilt-series.Extraction.Extract particles",
        ["DeselectTilts"]          = "Tilt-series.Selection.Deselect tilts",
        ["SelectTomograms"]        = "Tilt-series.Selection.Select tomograms",
        ["SelectParticles"]        = "Tilt-series.Selection.Select particles",

        // Refinement
        ["InitialReference"]       = "Refinement.Initial model.Initial reference",
        ["Class2D"]                = "Refinement.2D classes.Classify 2D",
        ["Class2DSelect"]          = "Refinement.2D classes.Select 2D classes",
        ["Class3D"]                = "Refinement.3D classes.Classify 3D",
        ["Class3DContinue"]        = "Refinement.3D classes.Continue 3D",
        ["Class3DSupervised"]      = "Refinement.3D classes.Supervised 3D",
        ["Class3DSelect"]          = "Refinement.3D classes.Select 3D classes",
        ["Refine3D"]               = "Refinement.3D refinement.Refine 3D",
        ["CreateMask"]             = "Refinement.Masks.Create mask",
        ["PostProcess"]            = "Refinement.Post-process.Post-process",

        // M
        ["CreatePopulation"]       = "M.Create population",
        ["CreateDataSource"]       = "M.Create data source",
        ["CreateSpecies"]          = "M.Create species",
        ["GetSpecies"]             = "M.Get species",
        ["GetTiltSeries"]          = "M.Get tilt series",
        ["ModifySpecies"]          = "M.Modify species",
        ["EstimateWeights"]        = "M.Estimate weights",
        ["Refine"]                 = "M.Refine",

        // Common
        ["ImportMap"]              = "Common.Import.Map",
        ["ImportMask"]             = "Common.Import.Mask",
        ["ImportParticles"]        = "Common.Import.Particles",
        ["ImportParticlePositions"]= "Common.Import.Particle positions",
        ["ThresholdStatistics"]    = "Common.Tools.Threshold statistics",
        ["Note"]                   = "Common.Notes.Note",
        ["Vibe"]                   = "Common.Notes.Vibe",
    };

    [Fact]
    public void EveryJobType_HasExpectedTypeCategory()
    {
        EnsurePopulated();

        var actual = Job.Types.Values.ToDictionary(
            t => t.Name,
            t => ((Job)Activator.CreateInstance(t)!).TypeCategory);

        Assert.Equal(Expected.Count, actual.Count); // no job added/removed unexpectedly

        foreach (var (name, category) in Expected)
        {
            Assert.True(actual.ContainsKey(name), $"Missing job type: {name}");
            Assert.Equal(category, actual[name]);
        }
    }

    [Fact]
    public void MenuHierarchy_HasFiveTopLevelGroups()
    {
        EnsurePopulated();

        var top = Job.TypeHierarchy.Subgroups
                     .Select(g => g.Name)
                     .OrderBy(x => x, StringComparer.Ordinal)
                     .ToArray();

        Assert.Equal(
            new[] { "Common", "Frame-series", "M", "Refinement", "Tilt-series" },
            top);
    }
}
