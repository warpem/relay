using Refund.JobResources;
using Refund.Jobs.Ts.Picking.TemplateMatch;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class TemplateMatchCommandTests
{
    public TemplateMatchCommandTests() => JobRegistry.EnsurePopulated();

    [Fact]
    public void NewJobs_UseMultistartDefaultsAndOmitRemovedOptions()
    {
        using var data = new TemplateMatchTestData();
        // Saved legacy values must never reintroduce flags removed from WarpTools.
        data.Job.OptimizePoses = true;
        data.Job.OptimizePosesSteps = 5;
        data.Job.Whiten = true;
        data.Job.DontNormalizeScores = true;
        data.Job.SubVolumeSize = 256;
        var arguments = data.Job.ComposeCommandArguments();

        Assert.Equal("100", arguments["template_diameter"]);
        Assert.Equal("8", arguments["match_topk"]);
        Assert.Equal("32", arguments["refine_starts"]);
        Assert.Equal("8000", arguments["npeaks"]);
        Assert.Equal("4", arguments["batch_angles"]);
        Assert.Equal("-1", arguments["max_missing_tilts"]);
        foreach (string flag in new[] { "optimize_poses", "optimize_poses_steps", "whiten", "dont_normalize", "subvolume_size",
                                      "refine_iterations", "refine_merge_fraction", "refine_max_shift", "refine_noise_patches", "decoy_templates" })
            Assert.False(arguments.ContainsKey(flag), flag);
    }

    [Fact]
    public void PoseRefinementPixelSize_IsAvailableWithoutLegacyToggle()
    {
        using var data = new TemplateMatchTestData();
        data.Job.OptimizePoses = false;
        data.Job.OptimizePosesAngpix = 4;
        Assert.Equal("4", data.Job.ComposeCommandArguments()["optimize_poses_angpix"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnvelopeDiagnostics_RequireAmplitudeAndBfactorFitting(bool enabled)
    {
        using var data = new TemplateMatchTestData();
        data.Job.RefineFitBfactor = enabled;
        data.Job.RefineFitHighpass = 40;
        data.Job.RefineExportTiltSpectra = true;
        data.Job.MaxMissingTilts = 0;
        var arguments = data.Job.ComposeCommandArguments();

        Assert.Equal(enabled, arguments.ContainsKey("refine_fit_bfactor"));
        Assert.Equal(enabled, arguments.ContainsKey("refine_fit_highpass"));
        Assert.Equal(enabled, arguments.ContainsKey("refine_export_tilt_spectra"));
        if (enabled) Assert.Equal("40", arguments["refine_fit_highpass"]);
        Assert.Equal("0", arguments["max_missing_tilts"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrelationVolume_UsesMatchingDirectoryAndSelectedTemplate(bool flip)
    {
        using var data = new TemplateMatchTestData();
        data.WriteDecision(flip);
        var tomograms = Assert.IsType<TomogramSet>(data.Job.PortsOut[TemplateMatch.PortOutTomogramSet].GetResource());
        Assert.Equal(Path.Combine(data.Job.DirectoryPath, "matching"), tomograms.TomogramCorrVolumeDirectory);
        Assert.Equal(Path.Combine(data.Job.DirectoryPath, "matching",
            $"TS_01_10.00Apx_reference{(flip ? "_flipx" : "")}_corr.mrc"),
            tomograms.ToTomogramCorrVolumePath("TS_01.tomostar"));
    }
}
