using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Preprocessing.MotionAndCTF2D;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Preprocessing.ExtractParticles2D;

[GenerateReadOnly]
public class ExtractParticles2D : WarpJobGpu, IClusterJob
{
    public override int2 CardSquareCount { get; set; } = new int2(2, 1);
    public override string TypeGuid => "21f9daa1-3e5a-4f04-94b4-63204b1c74b5";
    public override string TypeCategory => "2D.ExtractParticles";
    public override string TypeName => "Extract particles";
    public override string TypeNameShort => "Extract particles";
    public override string TypeDescription => "Extract and export particles from 2D micrographs";
    public override bool IsIterative => false;
    public override Type ExpandedViewType => typeof(ExtractParticles2DExpandedView);

    [RelayProperty]
    public int NItemsProcessed { get; set; } = 0;
    [RelayProperty]
    public int NItemsTotal { get; set; } = 0;
    
    #region Parameters

    [UiFieldGroup("Extraction Settings", 0)]
    [UiDecimalNullable("angpix_out", "Output pixel size",
                       helpText: "Pixel size the extracted particles will be scaled to; leave empty to use binned pixel size from input settings",
                       min: 0,
                       stepSize: 0.1,
                       unit: "Å")]
    [RelayProperty]
    public decimal? AngpixOut { get; set; } = null;

    [UiDecimal("box", "Box size",
               helpText: "Particle box size in pixels",
               min: 2,
               stepSize: 2,
               unit: "px")]
    [RelayProperty]
    public decimal BoxSize { get; set; } = 128;

    [UiDecimal("diameter", "Particle diameter",
               helpText: "Particle diameter in Angstrom",
               min: 1,
               unit: "Å")]
    [RelayProperty]
    public decimal Diameter { get; set; } = 100;

    [UiFieldGroup("Advanced Options", 1)]
    [UiBool("dont_invert", "Don't invert contrast",
            "Don't invert contrast, e.g. for negative stain data")]
    [RelayProperty]
    public bool DontInvert { get; set; } = false;

    [UiBool("dont_normalize", "Don't normalize",
            "Don't normalize background (RELION will complain!)")]
    [RelayProperty]
    public bool DontNormalize { get; set; } = false;

    [UiBool("dont_center", "Don't recenter",
            "Don't re-center particles based on refined shifts")]
    [RelayProperty]
    public bool DontCenter { get; set; } = false;

    [UiBool("flip_phases", "Pre-flip phases",
            "Pre-flip phases in bigger box to avoid signal loss due to delocalization")]
    [RelayProperty]
    public bool PreflipPhases { get; set; } = false;

    [UiDecimal("skip_first_frames", "Skip first frames",
               helpText: "Skip first N frames",
               min: 0,
               stepSize: 1)]
    [RelayProperty]
    public decimal SkipFirst { get; set; } = 0;

    [UiDecimal("skip_last_frames", "Skip last frames",
               helpText: "Skip last N frames",
               min: 0,
               stepSize: 1)]
    [RelayProperty]
    public decimal SkipLast { get; set; } = 0;
    
    #endregion
    
    #region Results paths
    
    public string ResParticlesStar(string name) => Path.Combine(DirectoryPath, "matching", $"{Path.GetFileNameWithoutExtension(name)}_mm_1e-4to1e-5.star");
    public string ResAverageFile(string name) => Path.Combine(DirectoryPath, "average", $"{Path.GetFileNameWithoutExtension(name)}.mrc");
    #endregion
    
    #region Visualizations
    
    public string VisThumbnail(string name) => Path.Combine(DirectoryPath, 
                                                            "thumbnails", 
                                                            $"{name.Substring(0, name.LastIndexOf("."))}.png");
    
    #endregion

    public ExtractParticles2D()
    {
        var portInMicrographs = new PortIn(
            job: this,
            resourceType: typeof(MicrographSet),
            name: "Micrographs",
            alias: "Micrographs",
            minItems: 1,
            maxItems: 1
        );
        
        var portInPositionSet = new PortIn(
            job: this,
            resourceType: typeof(ParticleSet),
            name: "Positions",
            alias: "Positions",
            minItems: 0,
            maxItems: 1
        );
        
        var portInParticleSet = new PortIn(
            job: this,
            resourceType: typeof(ParticleSet),
            name: "Particles",
            alias: "Particles",
            minItems: 0,
            maxItems: 1
        );

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInMicrographs.Name] = portInMicrographs,
            [portInPositionSet.Name] = portInPositionSet,
            [portInParticleSet.Name] = portInParticleSet,
        });

        var portOutParticleSet = new PortOut(
            job: this,
            resourceType: typeof(ParticleSet),
            name: "Particles",
            alias: "Particles",
            resourceDelegate: GetParticlesResource
        );

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutParticleSet.Name] = portOutParticleSet,
        });
    }

    private Resource GetParticlesResource(int iter)
    {
        ParticleSet result = new();
        
        if (PortsIn["Particles"].Edges.Any())
            result = PortsIn["Particles"].Edges.First().Source.GetResource() as ParticleSet;

        result.ParticlesSingleStarPath = ResParticlesStar("");

        return result;
    }

    public override Action TrackProgressLogs()
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        
        if (!File.Exists(PathStdOut))
            return null;
        
        var LogLines = File.ReadAllText(PathStdOut).Split('\n');
        if (LogLines.Length == 0)
            return null;
        
        // Find progress line if it exists
        int ProgressLine = -1;
        for (int i = 0; i < LogLines.Length; i++)
            if (LogLines[i].Contains("Connected to") && i < LogLines.Length - 1)
            {
                ProgressLine = i + 1;
                break;
            }
        
        // Take care of progress bars
        for (int i = 0; i < LogLines.Length; i++)
            if (LogLines[i].Contains('\r'))
                LogLines[i] = LogLines[i].Substring(LogLines[i].LastIndexOf('\r') + 1);
        
        // Parse line looking like "2/4, 00:10 remaining"
        int ItemsProcessed = 0;
        int ItemsTotal = 0;
        if (ProgressLine != -1)
        {
            var ProgressParts = LogLines[ProgressLine].Split(['/', ',']);
            if (ProgressParts.Length >= 2)
            {
                ItemsProcessed = int.Parse(ProgressParts[0]);
                ItemsTotal = int.Parse(ProgressParts[1]);
            }
        }
        
        JobTools.WriteLogFile(string.Join('\n', LogLines), LogFilePath(0));
        
        if (LogsAvailableIteration < 0 ||
            NItemsProcessed != ItemsProcessed ||
            NItemsTotal != ItemsTotal)
            return () => 
            {
                NItemsProcessed = ItemsProcessed;
                NItemsTotal = ItemsTotal;
                LogsAvailableIteration = 0;
            };

        return null;
    }

    public override Action TrackProgressResults()
    {
        if (VisAvailableIteration < 0 && !File.Exists(VisCard(0)) && NItemsProcessed > 1)
        {
            var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonFsItem>>(File.ReadAllText(ResProcessedItemsJson));
            if (processedItems.Count < 2)
                return null;

            Movie m1 = new Movie(Path.Combine(DirectoryPath, processedItems[0].Path));
            Movie m2 = new Movie(Path.Combine(DirectoryPath, processedItems[1].Path));
            
            BakeryWrapper.BoxNetInference2DJobCard(m1.AveragePath, ResParticlesStar(m1.RootName),
                                                   m2.AveragePath, ResParticlesStar(m2.RootName),
                                                   VisCard(0));
            
            return () => VisAvailableIteration = 0;
        }
        
        return null;
    }

    public string ComposeCommand() => throw new NotImplementedException();
}