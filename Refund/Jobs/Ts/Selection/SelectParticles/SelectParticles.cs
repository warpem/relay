using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Serilog;
using Warp;
using Warp.Headers;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Selection.SelectParticles;

/// <summary>
/// Represents a filter setting for a single parameter
/// </summary>
[Serializable]
public class FilterSetting : RelayBase
{
    [RelayProperty]
    public decimal Min { get; set; }
    
    [RelayProperty]
    public decimal Max { get; set; }
    
    [RelayProperty]
    public bool Locked { get; set; } = true;
    
    public bool Passes(decimal value) => value >= Min && value <= Max;
    public bool Passes(float value) => value >= (float)Min && value <= (float)Max;
}

/// <summary>
/// Represents filter settings for particle selection
/// </summary>
[Serializable]
public class FilterCollection : RelayBase
{
    [RelayProperty]
    public Dictionary<string, FilterSetting> Filters { get; set; } = new();
    
    /// <summary>
    /// Gets a filter setting by key, creating it if it doesn't exist
    /// </summary>
    public FilterSetting GetOrCreateFilter(string key)
    {
        if (!Filters.TryGetValue(key, out FilterSetting filter))
        {
            filter = new FilterSetting();
            Filters[key] = filter;
        }
        return filter;
    }
    
    public bool ContainsKey(string key) => Filters.ContainsKey(key);
}

/// <summary>
/// Job that performs particle selection from tomograms.
/// This is based on the template matching job structure.
/// </summary>
[GenerateReadOnly]
public class SelectParticles : WarpJob, ILocalJob
{
    public override string TypeGuid => "dfb781a9-e18d-4a80-8eff-b5d50b56c994";
    
    public override string TypeCategory => "Tilt-series.Selection.Select particles";

    public override string TypeName => "Particle selection";

    public override string TypeNameShort => "Select Particles";

    public override string TypeDescription => "Manually curate picked particles in tomograms";

    public override JobQueueType QueueType => JobQueueType.Local;

    /// <summary>Runs locally on the CPU; requests no GPUs.</summary>
    public override int GpuCount => 0;

    public override Type ExpandedViewType => typeof(SelectParticlesExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);
    
    public override bool IsInteractive => true;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTomogramSet = "Tomograms";
    public const string PortInParticleSet = "Positions";
    public const string PortOutTomogramSet = "Tomograms";
    public const string PortOutPositionSet = "Positions";
    
    /// <summary>
    /// Filter key constants
    /// </summary>
    public const string FilterKeyScore = "score";
    public const string FilterKeyCoordX = "coordX";
    public const string FilterKeyCoordY = "coordY";
    public const string FilterKeyCoordZ = "coordZ";
    public const string FilterKeyAngleX = "angleX";
    public const string FilterKeyAngleY = "angleY";
    public static string[] FilterKeys =
    {
        FilterKeyScore, 
        FilterKeyCoordX, 
        FilterKeyCoordY, 
        FilterKeyCoordZ, 
        FilterKeyAngleX, 
        FilterKeyAngleY
    };
    
    #region Particle Filter Parameters
    
    /// <summary>
    /// Global filter settings for all tomograms
    /// </summary>
    [RelayProperty]
    [Clearable]
    public FilterCollection GlobalFilterSettings { get; set; } = new();
    
    /// <summary>
    /// Dictionary of per-tomogram filter settings
    /// Maps tomogram names to their custom filter settings
    /// </summary>
    [RelayProperty]
    [Clearable]
    public Dictionary<string, FilterCollection> TomogramFilterSettings { get; set; } = new();

    public FilterSetting? GetFilterSetting(string tomogramName, string key)
    {
        if (TomogramFilterSettings.ContainsKey(tomogramName) && 
            TomogramFilterSettings[tomogramName].ContainsKey(key))
            return TomogramFilterSettings[tomogramName].Filters[key];
        else if (GlobalFilterSettings.ContainsKey(key))
            return GlobalFilterSettings.Filters[key];
        
        return null;
    }

    public FilterSetting? GetUnlockedFilterSetting(string tomogramName, string key)
    {
        if (TomogramFilterSettings.ContainsKey(tomogramName) && 
            TomogramFilterSettings[tomogramName].ContainsKey(key) &&
            !TomogramFilterSettings[tomogramName].Filters[key].Locked)
            return TomogramFilterSettings[tomogramName].Filters[key];
        else if (GlobalFilterSettings.ContainsKey(key))
            return GlobalFilterSettings.Filters[key];
        
        return null;
    }
    
    #endregion

    /// <summary>
    /// Constructor
    /// </summary>
    public SelectParticles()
    {
        var portInTomogramSet = new PortIn(this, typeof(TomogramSet), PortInTomogramSet, "Tomograms", 1, 1);
        var portInPositionSet = new PortIn(this, typeof(ParticleSet), PortInParticleSet, "Particle positions", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTomogramSet] = portInTomogramSet,
            [PortInParticleSet] = portInPositionSet
        });

        var portOutTomogramSet = new PortOut(this, typeof(TomogramSet), PortOutTomogramSet, "Tomograms", GetTomogramSetResource);
        var portOutPositions = new PortOut(this, typeof(ParticleSet), PortOutPositionSet, "Particle positions", GetParticleSetResource);
        

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTomogramSet] = portOutTomogramSet,
            [PortOutPositionSet] = portOutPositions
        });
    }
    
    private TomogramSet GetTomogramSetResource(int iter)
    {
        if (!PortsIn[PortInTomogramSet].IsConnected)
            return null;

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();

        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");

        // Ensure the TiltSeries has metadata
        if (!tomogramSet.TiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt series must have metadata.");

        return tomogramSet;
    }

    /// <summary>
    /// Resource generator for the output PositionSet
    /// </summary>
    private ParticleSet GetParticleSetResource(int iter)
    {
        if (!PortsIn[PortInTomogramSet].IsConnected)
            return null;
        
        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();

        var result = PortsIn[PortInParticleSet].GetSingleResource<ParticleSet>();
        
        if (!result.IsSingleStar)
        {
            result.ParticlesMultiStarDirectory = Path.Combine(DirectoryPath, TiltSeries.MatchingDirName);

            result.ToMultiStarPath = path => Path.Combine(DirectoryPath,
                                                          TiltSeries.MatchingDirName,
                                                          $"{WarpHelper.PathToName(tomogramSet.ToTomogramPath(path))}_selected.star");
        }
        else
        {
            result.ParticlesSingleStarPath = Path.Combine(DirectoryPath, TiltSeries.MatchingDirName, "selected.star");
        }
        
        return result;
    }

    /// <summary>
    /// Prepares the job for execution.
    /// </summary>
    public override void Stage()
    {
        base.Stage();

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();
        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");
        
        var positionSet = PortsIn[PortInParticleSet].GetSingleResource<ParticleSet>();
        if (positionSet == null)
            throw new InvalidOperationException("Position set input not found.");

        Directory.CreateDirectory(DirectoryPath);
        
        File.Copy(tomogramSet.ProcessedItemsJson, ResProcessedItemsJson);
        if (File.Exists(tomogramSet.FailedItemsJson))
            File.Copy(tomogramSet.FailedItemsJson, ResFailedItemsJson);
        
        var json = File.ReadAllText(tomogramSet.ProcessedItemsJson);
        var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(json);
        
        var header = MapHeader.ReadFromFile(tomogramSet.ToTomogramPath(processedItems[0].Path));
        GlobalFilterSettings.Filters[FilterKeyCoordX] = new FilterSetting { Min = 0, Max = header.Dimensions.X - 1 };
        GlobalFilterSettings.Filters[FilterKeyCoordY] = new FilterSetting { Min = 0, Max = header.Dimensions.Y - 1 };
        GlobalFilterSettings.Filters[FilterKeyCoordZ] = new FilterSetting { Min = 0, Max = header.Dimensions.Z - 1 };
        GlobalFilterSettings.Filters[FilterKeyAngleX] = new FilterSetting { Min = -180, Max = 180 };
        GlobalFilterSettings.Filters[FilterKeyAngleY] = new FilterSetting { Min = 0, Max = 180 };

        float minScore = float.MaxValue;
        float maxScore = float.MinValue;

        if (positionSet.IsSingleStar)
        {
            float[] scores = Star.LoadFloat(positionSet.ParticlesSingleStarPath, "rlnAutopickFigureOfMerit");
            minScore = Math.Min(minScore, scores.Min());
            maxScore = Math.Max(maxScore, scores.Max());
        }
        foreach (var item in processedItems)
        {
            try
            {
                float[] scores = Star.LoadFloat(positionSet.ToMultiStarPath(item.Path), "rlnAutopickFigureOfMerit");
                minScore = Math.Min(minScore, scores.Min());
                maxScore = Math.Max(maxScore, scores.Max());
            }
            catch {}
        }
        GlobalFilterSettings.Filters[FilterKeyScore] = new FilterSetting
        {
            Min = (decimal)Math.Floor(minScore), 
            Max = (decimal)Math.Ceiling(maxScore)
        };
    }

    private void WriteLog(string message)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        File.AppendAllText(LogFilePath(0), message + Environment.NewLine);
    }

    public override Action TrackProgressLogs()
    {
        var baseResult = base.TrackProgressLogs();

        if (LogsAvailableIteration < 0 && File.Exists(LogFilePath(0)))
            return () =>
            {
                baseResult?.Invoke();
                LogsAvailableIteration = 0;
            };

        return baseResult;
    }

    public void RunLocal(CancellationToken token)
    {
        while (!IsInteractiveFinished && !token.IsCancellationRequested)
            Thread.Sleep(100);

        if (token.IsCancellationRequested)
            return;

        // Once interactive mode is finished, generate the final particle sets
        try
        {
            FinalizeParticleSelection();
            _isFinalized = true;
        }
        catch (Exception ex)
        {
            Log.ForContext<SelectParticles>().Error(ex, "Error finalizing particle selection");
            throw;
        }
    }
    
    /// <summary>
    /// Applies the filters to generate the final particle star files
    /// </summary>
    private void FinalizeParticleSelection()
    {
        // Get input resources
        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();
        var inputPositionSet = PortsIn[PortInParticleSet].GetSingleResource<ParticleSet>();
        var outputPositionSet = PortsOut[PortOutPositionSet].GetResource() as ParticleSet;

        if (tomogramSet == null || inputPositionSet == null || outputPositionSet == null)
        {
            throw new InvalidOperationException("Required resources not found");
        }

        // Create output directory if it doesn't exist
        if (!inputPositionSet.IsSingleStar)
            Directory.CreateDirectory(outputPositionSet.ParticlesMultiStarDirectory);

        // Prepare to process each tomogram
        var json = File.ReadAllText(tomogramSet.ProcessedItemsJson);
        var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(json);

        // Log global filter settings
        WriteLog("Global filter settings:");
        foreach (var key in FilterKeys)
        {
            if (GlobalFilterSettings.ContainsKey(key))
            {
                var f = GlobalFilterSettings.Filters[key];
                WriteLog($"  {key}: [{f.Min}, {f.Max}]");
            }
        }

        int exportedOverall = 0;

        // Process each tomogram
        if (inputPositionSet.IsSingleStar)
        {
            // If using a single star file, we only need to process the first item
            if (processedItems.Count > 0)
            {
                string inputStarPath = inputPositionSet.ParticlesSingleStarPath;
                string outputStarPath = outputPositionSet.ParticlesSingleStarPath;

                WriteLog($"Processing combined STAR file: {inputStarPath}");

                if (!File.Exists(inputStarPath))
                {
                    WriteLog("Combined particle file not found, aborting");
                    Log.ForContext<SelectParticles>().Warning("Combined particle file not found");
                    return;
                }

                // Filter particles and generate output star file
                exportedOverall += FilterCombinedParticles(processedItems.Select(i => i.Path).ToArray(),
                                                           inputStarPath,
                                                           outputStarPath);
            }
        }
        else
        {
            WriteLog($"Processing {processedItems.Count} tomograms (multi-star mode)");

            foreach (var item in processedItems)
            {
                string tomogramName = item.Path;
                string tomogramPath = tomogramSet.ToTomogramPath(tomogramName);
                string inputStarPath = inputPositionSet.ToMultiStarPath(tomogramName);
                string outputStarPath = outputPositionSet.ToMultiStarPath(tomogramName);

                if (!File.Exists(inputStarPath))
                {
                    WriteLog($"  {WarpHelper.PathToName(tomogramName)}: skipped (file not found)");
                    Log.ForContext<SelectParticles>().Warning("Particle file not found for tomogram {TomogramName}", tomogramName);
                    continue;
                }

                // Create output directory if it doesn't exist
                Directory.CreateDirectory(Path.GetDirectoryName(outputStarPath));

                // Filter particles and generate output star file
                exportedOverall += FilterParticlesForTomogram(tomogramName, tomogramPath, inputStarPath, outputStarPath);
            }
        }
        
        WriteLog($"Selected a total of {exportedOverall} particles across all tomograms");
    }
    
    /// <summary>
    /// Filters particles for a specific tomogram and writes them to an output star file
    /// </summary>
    private int FilterParticlesForTomogram(string tomogramName, string tomogramPath, string inputStarPath, string outputStarPath)
    {
        try
        {
            var tableIn = new Star(inputStarPath);

            Dictionary<string, FilterSetting> currentFilters = new();
            foreach (var key in FilterKeys)
                currentFilters[key] = GetUnlockedFilterSetting(tomogramName, key);

            // Get the star file data
            float3[] coordinates = tableIn.GetRelionCoordinates();

            float3[] angles = tableIn.HasColumn("rlnAngleRot") &&
                              tableIn.HasColumn("rlnAngleTilt") &&
                              tableIn.HasColumn("rlnAnglePsi") ?
                                  tableIn.GetRelionAngles() :
                                  new float3[coordinates.Length];

            float[] scores = tableIn.HasColumn("rlnAutopickFigureOfMerit") ?
                                 tableIn.GetFloatRobust("rlnAutopickFigureOfMerit") :
                                 new float[coordinates.Length];

            Predicate<int>[] filters =
            [
                r => scores[r] >= (float)currentFilters[FilterKeyScore].Min &&
                     scores[r] <= (float)currentFilters[FilterKeyScore].Max,

                r => coordinates[r].X >= (float)currentFilters[FilterKeyCoordX].Min &&
                     coordinates[r].X <= (float)currentFilters[FilterKeyCoordX].Max,

                r => coordinates[r].Y >= (float)currentFilters[FilterKeyCoordY].Min &&
                     coordinates[r].Y <= (float)currentFilters[FilterKeyCoordY].Max,

                r => coordinates[r].Z >= (float)currentFilters[FilterKeyCoordZ].Min &&
                     coordinates[r].Z <= (float)currentFilters[FilterKeyCoordZ].Max,

                r => angles[r].X >= (float)currentFilters[FilterKeyAngleX].Min &&
                     angles[r].X <= (float)currentFilters[FilterKeyAngleX].Max,

                r => angles[r].Y >= (float)currentFilters[FilterKeyAngleY].Min &&
                     angles[r].Y <= (float)currentFilters[FilterKeyAngleY].Max
            ];

            if (coordinates.Any() &&
                coordinates.Select(v => Math.Max(v.X, Math.Max(v.Y, v.Z))).Max() < 1.1f)
            {
                MapHeader header = MapHeader.ReadFromFile(tomogramPath);
                float3 vDims = new(header.Dimensions);

                for (int i = 0; i < coordinates.Length; i++)
                    coordinates[i] *= vDims;
            }

            // Wrap angles into expected ranges: X to [-180, 180], Y to [0, 180]
            if (angles != null)
            {
                for (int i = 0; i < angles.Length; i++)
                {
                    angles[i].X = ((angles[i].X % 360f) + 540f) % 360f - 180f;
                    angles[i].Y = ((angles[i].Y % 180f) + 180f) % 180f;
                }
            }

            // Apply filters to create indices of particles to keep
            List<int> filteredIndices = new List<int>();

            for (int i = 0; i < coordinates.Length; i++)
                if (filters.All(p => p(i)))
                    filteredIndices.Add(i);

            // Create a new star file with only the filtered particles
            var filteredStar = tableIn.CreateSubset(filteredIndices.ToArray());
            filteredStar.Save(outputStarPath);

            // Log per-tomogram filter details if custom settings exist
            bool hasCustomFilters = TomogramFilterSettings.ContainsKey(tomogramName);
            if (hasCustomFilters)
            {
                WriteLog($"  {WarpHelper.PathToName(tomogramName)}: {filteredIndices.Count}/{tableIn.RowCount} particles (custom filters)");
                foreach (var key in FilterKeys)
                {
                    var f = currentFilters[key];
                    if (f != null)
                        WriteLog($"    {key}: [{f.Min}, {f.Max}]");
                }
            }
            else
            {
                WriteLog($"  {WarpHelper.PathToName(tomogramName)}: {filteredIndices.Count}/{tableIn.RowCount} particles");
            }

            Log.ForContext<SelectParticles>().Information("Saved {ParticleCount} particles for tomogram {TomogramName}", filteredIndices.Count, tomogramName);
            
            return filteredIndices.Count;
        }
        catch (Exception ex)
        {
            Log.ForContext<SelectParticles>().Error(ex, "Error filtering particles for tomogram {TomogramName}", tomogramName);
            throw;
        }
    }
    
    /// <summary>
    /// Filters particles for a specific tomogram and writes them to an output star file
    /// </summary>
    private int FilterCombinedParticles(string[] tomogramNames, string inputStarPath, string outputStarPath)
    {
        try
        {
            var tableIn = new Star(inputStarPath);

            WriteLog($"Loaded {tableIn.RowCount} particles from combined STAR file");

            if (!tableIn.HasColumn("rlnTomoName"))
                throw new InvalidOperationException("Input star file does not contain 'rlnTomoName' column, can't match particles to tomograms.");
            int tomoColumnId = tableIn.GetColumnID("rlnTomoName");

            Dictionary<string, List<int>> tomogramToIndices = new();
            foreach (var tomogramName in tomogramNames)
                tomogramToIndices[tomogramName] = new List<int>();

            for (int r = 0; r < tableIn.RowCount; r++)
            {
                string tomogramName = tableIn.GetRowValue(r, tomoColumnId);
                if (tomogramToIndices.ContainsKey(tomogramName))
                    tomogramToIndices[tomogramName].Add(r);
            }

            if (tomogramToIndices.All(kvp => kvp.Value.Count == 0))
                throw new InvalidOperationException("No particles found in the STAR file for any of the specified tomograms.");

            WriteLog($"Split into {tomogramToIndices.Count} tomograms");

            Dictionary<string, Star> tomoTables = tomogramToIndices.ToDictionary(kvp => kvp.Key,
                                                                                 kvp => tableIn.CreateSubset(kvp.Value.ToArray()));

            Dictionary<string, Star> filteredTomoTables = new();
            int totalKept = 0;

            foreach (var tomogramName in tomogramToIndices.Keys)
            {
                var tomoTableIn = tomoTables[tomogramName];

                // Get the star file data
                float3[] coordinates = tomoTableIn.GetRelionCoordinates();

                float3[] angles = tomoTableIn.HasColumn("rlnAngleRot") &&
                                  tomoTableIn.HasColumn("rlnAngleTilt") &&
                                  tomoTableIn.HasColumn("rlnAnglePsi") ?
                                      tomoTableIn.GetRelionAngles() :
                                      new float3[coordinates.Length];

                float[] scores = tomoTableIn.HasColumn("rlnAutopickFigureOfMerit") ?
                                     tomoTableIn.GetFloatRobust("rlnAutopickFigureOfMerit") :
                                     new float[coordinates.Length];

                Dictionary<string, FilterSetting> currentFilters = new();
                foreach (var key in FilterKeys)
                    currentFilters[key] = GetUnlockedFilterSetting(tomogramName, key);

                Predicate<int>[] filters =
                [
                    r => scores[r] >= (float)currentFilters[FilterKeyScore].Min &&
                         scores[r] <= (float)currentFilters[FilterKeyScore].Max,

                    r => coordinates[r].X >= (float)currentFilters[FilterKeyCoordX].Min &&
                         coordinates[r].X <= (float)currentFilters[FilterKeyCoordX].Max,

                    r => coordinates[r].Y >= (float)currentFilters[FilterKeyCoordY].Min &&
                         coordinates[r].Y <= (float)currentFilters[FilterKeyCoordY].Max,

                    r => coordinates[r].Z >= (float)currentFilters[FilterKeyCoordZ].Min &&
                         coordinates[r].Z <= (float)currentFilters[FilterKeyCoordZ].Max,

                    r => angles[r].X >= (float)currentFilters[FilterKeyAngleX].Min &&
                         angles[r].X <= (float)currentFilters[FilterKeyAngleX].Max,

                    r => angles[r].Y >= (float)currentFilters[FilterKeyAngleY].Min &&
                         angles[r].Y <= (float)currentFilters[FilterKeyAngleY].Max
                ];

                // Wrap angles into expected ranges: X to [-180, 180], Y to [0, 180]
                if (angles != null)
                {
                    for (int i = 0; i < angles.Length; i++)
                    {
                        angles[i].X = ((angles[i].X % 360f) + 540f) % 360f - 180f;
                        angles[i].Y = ((angles[i].Y % 180f) + 180f) % 180f;
                    }
                }

                // Apply filters to create indices of particles to keep
                List<int> filteredIndices = new List<int>();

                for (int i = 0; i < coordinates.Length; i++)
                    if (filters.All(p => p(i)))
                        filteredIndices.Add(i);

                // Create a new star file with only the filtered particles
                filteredTomoTables[tomogramName] = tableIn.CreateSubset(filteredIndices.ToArray());
                totalKept += filteredIndices.Count;

                bool hasCustomFilters = TomogramFilterSettings.ContainsKey(tomogramName);
                if (hasCustomFilters)
                {
                    WriteLog($"  {WarpHelper.PathToName(tomogramName)}: {filteredIndices.Count}/{tomoTableIn.RowCount} particles (custom filters)");
                    foreach (var key in FilterKeys)
                    {
                        var f = currentFilters[key];
                        if (f != null)
                            WriteLog($"    {key}: [{f.Min}, {f.Max}]");
                    }
                }
                else
                {
                    WriteLog($"  {WarpHelper.PathToName(tomogramName)}: {filteredIndices.Count}/{tomoTableIn.RowCount} particles");
                }
            }

            var filteredStar = new Star(filteredTomoTables.Values.ToArray());
            filteredStar.Save(outputStarPath);

            WriteLog($"Selected {totalKept}/{tableIn.RowCount} particles total");
            WriteLog($"Saved to {outputStarPath}");

            Log.ForContext<SelectParticles>().Information("Saved {ParticleCount} particles", filteredStar.RowCount);
            
            return filteredStar.RowCount;
        }
        catch (Exception ex)
        {
            Log.ForContext<SelectParticles>().Error(ex, "Error filtering combined particle table");
            throw;
        }
    }
    
    public override Action TrackProgressResults()
    {
        if (!_isFinalized)
            return null;
        
        var baseUpdate = base.TrackProgressResults();
        
        if (VisAvailableIteration < 0 && !File.Exists(VisCard(0)))
        {
            var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(File.ReadAllText(ResProcessedItemsJson));

            if (processedItems.Count == 0)
                return null;

            ParticleSet particleSet = GetParticleSetResource(0);
            TomogramSet tomogramSet = GetTomogramSetResource(0);

            BakeryWrapper.TsSelectParticlesJobCard(tomogramSet.ToTomogramPath(processedItems[0].Path),
                                                   particleSet.ToMultiStarPath(processedItems[0].Path),
                                                   tomogramSet.ToTomogramPath(processedItems[1].Path),
                                                   particleSet.ToMultiStarPath(processedItems[1].Path),
                                                   particleSet.Diameter,
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