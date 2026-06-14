using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Warp.Tools;

namespace Refund.Jobs.Fs.MotionCtf.Motion2D;

[GenerateReadOnly]
public class Motion2D : WarpJobGpu, IClusterJob
{
    public override int2 CardSquareCount { get; set; } = new int2(2, 1);
    public override string TypeGuid => "d0aa29ff-0397-4c7e-b239-fe9426b8b10e";
    public override string TypeCategory => "Frame-series.Motion & CTF.Motion";
    public override string TypeName => "Motion correction";
    public override string TypeNameShort => "Motion2D";
    public override string TypeDescription => "Motion correction on 2D images";
    public override bool IsIterative => false;
    public override Type ExpandedViewType => null;

    #region Parameters
    
    #region Fitting
    
    [UiFieldGroup("Fitting parameters", 0)]
    [UiDecimal("range_min", "Minimum resolution",
        helpText: "Minimum resolution in Angstrom to consider in motion fit",
        min: 1,
        max: 1,
        unit: "Å")]
    public decimal MotionRangeMin { get; set; } = 500;

    [UiDecimal("range_max", "Maximum resolution",
        helpText: "Maximum resolution in Angstrom to consider in motion fit",
        min: 1,
        unit: "Å")]
    public decimal MotionRangeMax { get; set; } = 10;

    [UiDecimal("bfac", "B-factor",
        helpText: "Downweight higher spatial frequencies using a B-factor",
        stepSize: 10,
        unit: "Å²")]
    public decimal MotionBfactor { get; set; } = -500;

    [UiInt("grid", "Model grid",
        helpText: "Resolution of the motion model grid in X, Y, and temporal dimensions, separated by 'x': e.g. 5x5x40; empty = auto")]
    public int3 MotionGridDims { get; set; } = new int3(1);
    
    #endregion

    #region Output controls
    
    [UiFieldGroup("Output", order: 1)]
    [UiBool("out_averages", "Export averages",
        helpText: "Export aligned averages")]
    public bool OutAverages { get; set; } = true;

    [UiBool("out_average_halves", "Export halves",
        helpText: "Export aligned averages of odd and even frames separately, e.g. for denoiser training")]
    public bool OutAverageHalves { get; set; }

    [UiDecimal("out_skip_first", "Skip first N frames",
        helpText: "Skip first N frames when exporting averages",
        min: 0,
        stepSize: 1)]
    public decimal OutSkipFirst { get; set; } = 0;

    [UiDecimal("out_skip_last", "Skip last N frames",
        helpText: "Skip last N frames when exporting averages",
        min: 0,
        stepSize: 1)]
    public decimal OutSkipLast { get; set; } = 0;

    [UiDecimal("out_thumbnails", "Thumbnail size",
        helpText: "Export thumbnails, scaled so that the long edge has this length in pixels",
        min: 2,
        stepSize: 2)]
    public decimal OutThumbnails { get; set; } = 256;
    
    #endregion
    
    #endregion

    public Motion2D()
    {
        var portInDataSet = new PortIn(
            job: this,
            resourceType: typeof(DataSetFs),
            name: "Dataset",
            alias: "Dataset",
            minItems: 1,
            maxItems: int.MaxValue
        );

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInDataSet.Name] = portInDataSet,
        });

        var portOutMicrographSet = new PortOut(
            job: this,
            resourceType: typeof(MicrographSet),
            name: "Micrographs",
            alias: "Micrographs",
            resourceDelegate: GetMicrographsResource
        );

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutMicrographSet.Name] = portOutMicrographSet,
        });
    }

    private Resource GetMicrographsResource(int iter)
    {
        throw new NotImplementedException();
    }

    public override Action TrackProgressLogs() => null;

    public string ComposeCommand() => throw new NotImplementedException();
}