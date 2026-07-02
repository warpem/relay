using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Fs.Picking.BoxNetInference2D;

[GenerateReadOnly]
public class BoxNetInference2D : WarpJobGpu, IClusterJob
{
    public override int2 CardSquareCount { get; set; } = new int2(2, 1);
    public override string TypeGuid => "2ae4d014-7b22-40df-b6cf-dbff8d0e4243";
    public override string TypeCategory => "Frame-series.Picking.BoxNet";
    public override string TypeName => "BoxNet particle picking";
    public override string TypeNameShort => "BoxNetInference";
    public override string TypeDescription => "Particle picking and segmentation in 2D micrographs using BoxNet";
    public override bool IsIterative => false;
    public override Type ExpandedViewType => null;

    #region Results paths
    
    
    public string ResAverageFile(string name) => Path.Combine(DirectoryPath, "average", $"{Path.GetFileNameWithoutExtension(name)}.mrc");
    public string ResParticlesStar(string name) => Path.Combine(DirectoryPath, "matching", $"{Path.GetFileNameWithoutExtension(name)}_mm_1e-4to1e-5.star");
    public string ResMaskTiff(string name) => Path.Combine(DirectoryPath, "mask", $"{Path.GetFileNameWithoutExtension(name)}.tif");
    
    #endregion
    
    #region Visualizations
    
    public string VisThumbnail(string name) => Path.Combine(DirectoryPath, 
                                                            "thumbnails", 
                                                            $"{name.Substring(0, name.LastIndexOf("."))}.png");
    
    #endregion

    public BoxNetInference2D()
    {
        var portInMicrographs = new PortIn(
            job: this,
            resourceType: typeof(MicrographSet),
            name: "Micrographs",
            alias: "Micrographs",
            minItems: 1,
            maxItems: int.MaxValue
        );

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInMicrographs.Name] = portInMicrographs,
        });

        var portOutPositionSet = new PortOut(
            job: this,
            resourceType: typeof(ParticleSet),
            name: "Positions",
            alias: "Positions",
            resourceDelegate: GetPositionsResource
        );

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutPositionSet.Name] = portOutPositionSet,
        });
    }

    private Resource GetPositionsResource(int iter)
    {
        return new ParticleSet()
        {
            ParticlesMultiStarDirectory = Path.Combine(DirectoryPath, Movie.MatchingDirName),
            ToMultiStarPath = path => Path.Combine(DirectoryPath, 
                                                   Movie.MatchingDirName, 
                                                   $"{Warp.Tools.Helper.PathToName(path)}.star"),
        };
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