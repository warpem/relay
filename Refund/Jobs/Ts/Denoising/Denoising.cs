using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Denoising;

/// <summary>
/// Job that reconstructs tomograms from tilt series.
/// This is based on the WarpTools ReconstructTiltseries command.
/// </summary>
[GenerateReadOnly]
public class Denoising : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "ef2ac481-e330-4f3c-9238-8f5e270e2d58";
    
    public override string TypeCategory => "Tilt-series.Reconstruction.Denoising";

    public override string TypeName => "Denoise tomograms";

    public override string TypeNameShort => "Denoise";

    public override string TypeDescription => "Denoise tomograms using a pre-trained model or training one from scratch";

    public override Type ExpandedViewType => typeof(DenoisingExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => 8;

    public override int MemoryGb => 48;

    public override int GpuCount => 1;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTomogramSet = "Tomograms";
    public const string PortInModel = "Model";
    public const string PortOutTomogramSet = "Tomograms";
    public const string PortOutModel = "Model";
    
    #region Parameters

    /// <summary>
    /// Mask out voxels that aren't contained in some of the tilt images (due to excessive sample shifts); 
    /// don't use if you intend to run template matching
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Training", 1)]
    [UiBool("", "Perform training",
            "Train a new model from scratch (if no model is provided) or fine-tune an existing model (if a model is provided).")]
    public bool PerformTraining { get; set; } = true;
    
    [RelayProperty]
    [UiInt("iterations", "Training iterations", min: 100, max: int.MaxValue, stepSize: 100,
           helpText: "",
           ConditionalOnField = nameof(PerformTraining), ConditionalOnValue = true)]
    public int TrainingIterations { get; set; } = 10000;

    public override int NGpus { get; set; } = 1;
    public override int MemoryPerWorker { get; set; } = 12;
    public override int PerDevice { get; set; } = 1;

    #endregion
    
    private const string MODEL_NAME = "model";
    public string ResModelPath => Path.Combine(DirectoryPath, MODEL_NAME + ".pt");

    public Denoising()
    {
        var portInTomogramSet = new PortIn(this, typeof(TomogramSet), PortInTomogramSet, "Tomograms", 1, 1);
        var portInModel = new PortIn(this, typeof(NoiseNet3D), PortInTomogramSet, "Pre-trained model", 0, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTomogramSet] = portInTomogramSet,
            [PortInModel] = portInModel
        });

        var portOutTomogramSet = new PortOut(this, typeof(TomogramSet), PortOutTomogramSet, "Denoised tomograms", GetTomogramResource);
        var portOutModel = new PortOut(this, typeof(NoiseNet3D), PortOutModel, "Trained model", GetModelResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTomogramSet] = portOutTomogramSet,
            [PortOutModel] = portOutModel
        });
    }

    private TomogramSet GetTomogramResource(int iter)
    {
        if (!PortsIn[PortInTomogramSet].IsConnected)
            return null;

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();

        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram input not found.");
        
        tomogramSet.TomogramDenoisedDirectory = WarpHelper.PathCombine(DirectoryPath, "denoised");
        tomogramSet.ToTomogramDenoisedPath = (name) => WarpHelper.PathCombine(DirectoryPath, "denoised",
                                                                             TiltSeries.ToTomogramWithPixelSize(name, tomogramSet.PixelSize) + ".mrc");

        return tomogramSet;
    }

    private NoiseNet3D GetModelResource(int iter)
    {
        return new NoiseNet3D(ResModelPath);
    }

    /// <summary>
    /// Gets the name of the Warp command used for tomogram reconstruction.
    /// </summary>
    public override string CommandName => "Noise2Map";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        NoiseNet3D model = null;
        if (PortsIn[PortInModel].IsConnected)
            model = PortsIn[PortInModel].GetSingleResource<NoiseNet3D>();

        if (model != null && !string.IsNullOrWhiteSpace(model.ModelPath) && File.Exists(model.ModelPath))
            if (PerformTraining)
                result["start_model"] = Space.GetRelativePath(model.ModelPath);
            else
                result["old_model"] = Space.GetRelativePath(model.ModelPath);
        
        if (result.ContainsKey("start_model"))
            result["learningrate_start"] = "1e-5";

        // We want to use the narrow/shallow model here
        result["mini_model"] = "";

        result["dont_flatten_spectrum"] = "";
        result["dont_augment"] = "";
        
        if (!PortsIn[PortInTomogramSet].IsConnected)
            throw new InvalidOperationException("Tomogram input not found.");
        TomogramSet tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();
        
        if (string.IsNullOrWhiteSpace(tomogramSet.TomogramHalfMap1Directory) ||
            string.IsNullOrWhiteSpace(tomogramSet.TomogramHalfMap2Directory))
            throw new InvalidOperationException("Tomograms don't have half-maps.");
        
        result["observation1"] = Space.GetRelativePath(tomogramSet.TomogramHalfMap1Directory);
        result["observation2"] = Space.GetRelativePath(tomogramSet.TomogramHalfMap2Directory);

        result["save_model_name"] = Path.Combine(Space.GetRelativePath(DirectoryPath), MODEL_NAME);
        
        result.Remove("strict");

        return result;
    }

    public override void Stage()
    {
        base.Stage();

        if (!PortsIn[PortInTomogramSet].IsConnected)
            throw new InvalidOperationException("Tomogram input not found.");
    }
    
    public override Action TrackProgressResults()
    {
        var baseUpdate = base.TrackProgressResults();
        
        if (VisAvailableIteration < 0 && !File.Exists(VisCard(0)) && NItemsProcessed > 1)
        {
            var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(File.ReadAllText(ResProcessedItemsJson));

            if (processedItems.Count == 0)
                return null;

            TomogramSet tomogramSet = GetTomogramResource(0);

            BakeryWrapper.TsReconstructJobCard(tomogramSet.ToTomogramThumbnailPath(processedItems[0].Path), 
                                               tomogramSet.ToTomogramThumbnailPath(processedItems[1].Path), 
                                               VisCard(0));

            return () =>
            {
                baseUpdate?.Invoke();
                VisAvailableIteration = 0;
            };
        }

        return baseUpdate;
    }
}