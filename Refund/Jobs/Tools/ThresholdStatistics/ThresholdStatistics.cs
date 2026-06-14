using System.Globalization;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Ts.SelectParticles;
using Refund.Utils;
using Serilog;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Tools.ThresholdStatistics;

/// <summary>
/// Interactive job for general-purpose particle filtering by thresholding any numerical column
/// in a STAR file. Discovers numerical columns dynamically and lets users set thresholds
/// on all of them via histograms.
/// </summary>
[GenerateReadOnly]
public class ThresholdStatistics : LocalJob, ILocalJob
{
    public override string TypeGuid => "a1c3e5f7-2b4d-6e8f-0a1c-3e5f7b9d1e3f";

    public override string TypeCategory => "Common.Tools.Threshold statistics";

    public override string TypeName => "Threshold statistics";

    public override string TypeNameShort => "Threshold stats";

    public override string TypeDescription => "Filter particles by thresholding any numerical column in a STAR file";

    public override JobQueueType QueueType => JobQueueType.Local;

    public override bool IsInteractive => true;

    public override Type ExpandedViewType => typeof(ThresholdStatisticsExpandedView);

    public override int2 CardSquareCount { get; set; } = new int2(2, 1);

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInParticles = "Particles";
    public const string PortOutParticles = "Particles";

    #region Serialized State

    /// <summary>
    /// Filter settings (thresholds) per column
    /// </summary>
    [RelayProperty]
    [Clearable]
    public FilterCollection FilterSettings { get; set; } = new();

    /// <summary>
    /// Discovered numerical column names from the input STAR file
    /// </summary>
    [RelayProperty]
    [Clearable]
    public string[] NumericalColumnNames { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Whether the input was a multi-table STAR file (with optics table)
    /// </summary>
    [RelayProperty]
    [Clearable]
    public bool IsMultiTableInput { get; set; } = false;

    #endregion

    #region Results paths

    private const string ResFilteredStar = "filtered.star";
    private const string ResOptimisationSetStar = "optimisation_set.star";

    /// <summary>
    /// Full path to the filtered particle STAR file.
    /// </summary>
    public string ResFilteredStarFile => Path.Combine(DirectoryPath, ResFilteredStar);

    /// <summary>
    /// Full path to the adapted optimisation_set.star file (tomo only).
    /// </summary>
    public string ResOptimisationSetStarFile => Path.Combine(DirectoryPath, ResOptimisationSetStar);

    #endregion

    public ThresholdStatistics()
    {
        var portInParticles = new PortIn(this, typeof(ParticleSet), PortInParticles, "Particles", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInParticles.Name] = portInParticles
        });

        var portOutParticles = new PortOut(this, typeof(ParticleSet), PortOutParticles, "Particles", GetParticleSetResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutParticles.Name] = portOutParticles
        });
    }

    #region Resource locators

    private ParticleSet GetParticleSetResource(int iter)
    {
        if (!PortsIn[PortInParticles].IsConnected)
            return null;

        ParticleSet result = PortsIn[PortInParticles].GetSingleResource<ParticleSet>();
        if (result == null)
            return null;

        result.ParticlesSingleStarPath = ResFilteredStarFile;

        if (!string.IsNullOrEmpty(result.OptimisationSetStarPath))
            result.OptimisationSetStarPath = ResOptimisationSetStarFile;

        return result;
    }

    #endregion

    public override void Stage()
    {
        base.Stage();

        ParticleSet resourceParticles = PortsIn[PortInParticles].GetSingleResource<ParticleSet>();
        if (resourceParticles == null)
            throw new InvalidOperationException("Particle set input not found.");

        if (!resourceParticles.HasSingleStar)
            throw new InvalidOperationException("Input must have a single STAR file.");

        string starPath = resourceParticles.ParticlesSingleStarPath;
        if (!File.Exists(starPath))
            throw new InvalidOperationException($"STAR file not found: {starPath}");

        Directory.CreateDirectory(DirectoryPath);

        // Check if multi-table STAR
        IsMultiTableInput = Star.IsMultiTable(starPath);

        Star star;
        if (IsMultiTableInput)
            star = new Star(starPath, "particles");
        else
            star = new Star(starPath);

        // Discover numerical columns
        string[] allColumns = star.GetColumnNames();
        var numericalColumns = new List<string>();

        foreach (string col in allColumns)
        {
            string[] values = star.GetColumn(col);
            if (values == null || values.Length == 0)
                continue;

            // Check if all values are parseable as float
            bool allNumeric = true;
            for (int i = 0; i < values.Length; i++)
            {
                if (!float.TryParse(values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    allNumeric = false;
                    break;
                }
            }

            if (allNumeric)
                numericalColumns.Add(col);
        }

        NumericalColumnNames = numericalColumns.ToArray();

        // For each numerical column, compute min/max and create a FilterSetting
        foreach (string col in NumericalColumnNames)
        {
            float[] values = star.GetFloatRobust(col);

            // Exclude NaN and Inf
            float min = float.MaxValue;
            float max = float.MinValue;

            foreach (float v in values)
            {
                if (float.IsNaN(v) || float.IsInfinity(v))
                    continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }

            if (min > max)
            {
                min = 0;
                max = 1;
            }

            FilterSettings.Filters[col] = new FilterSetting
            {
                Min = Math.Floor((decimal)min),
                Max = Math.Ceiling((decimal)max)
            };
        }
    }

    public void RunLocal(CancellationToken token)
    {
        while (!IsInteractiveFinished && !token.IsCancellationRequested)
            Thread.Sleep(100);

        if (token.IsCancellationRequested)
            return;

        try
        {
            FinalizeFiltering();
        }
        catch (Exception ex)
        {
            Log.ForContext<ThresholdStatistics>().Error(ex, "Error finalizing threshold filtering");
            throw;
        }
    }

    private void WriteLog(string message)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);
        File.AppendAllText(LogFilePath(0), message + Environment.NewLine);
    }

    private void FinalizeFiltering()
    {
        ParticleSet resourceParticles = PortsIn[PortInParticles].GetSingleResource<ParticleSet>();
        string inputStarPath = resourceParticles.ParticlesSingleStarPath;

        WriteLog($"Reading input STAR file: {inputStarPath}");

        Star tableInGeneral = null;
        Star tableInOptics = null;
        Star tableInParticles;

        if (IsMultiTableInput)
        {
            tableInParticles = new Star(inputStarPath, "particles");
            tableInOptics = new Star(inputStarPath, "optics");
            if (Star.ContainsTable(inputStarPath, "general"))
                tableInGeneral = new StarParameters(inputStarPath, "general");
        }
        else
        {
            tableInParticles = new Star(inputStarPath);
        }

        WriteLog($"Loaded {tableInParticles.RowCount} particles, {NumericalColumnNames.Length} numerical columns");

        // Pre-cache column data and active filters to avoid re-parsing per row
        var activeFilters = new List<(string name, float[] values, FilterSetting filter)>();
        foreach (string col in NumericalColumnNames)
        {
            if (!FilterSettings.Filters.ContainsKey(col))
                continue;

            activeFilters.Add((col, tableInParticles.GetFloatRobust(col), FilterSettings.Filters[col]));
        }

        WriteLog($"Applying {activeFilters.Count} active filters:");
        foreach (var (name, _, filter) in activeFilters)
            WriteLog($"  {name}: [{filter.Min}, {filter.Max}]");

        // Build list of rows that pass all filters
        var rowsSelected = new List<int>();

        for (int row = 0; row < tableInParticles.RowCount; row++)
        {
            bool passes = true;

            foreach (var (_, values, filter) in activeFilters)
            {
                float value = values[row];

                if (float.IsNaN(value) || float.IsInfinity(value) || !filter.Passes(value))
                {
                    passes = false;
                    break;
                }
            }

            if (passes)
                rowsSelected.Add(row);
        }

        WriteLog($"Selected {rowsSelected.Count}/{tableInParticles.RowCount} particles");

        Star tableOutFiltered = tableInParticles.CreateSubset(rowsSelected);

        if (IsMultiTableInput && tableInOptics != null)
        {
            var tables = new Dictionary<string, Star>();
            if (tableInGeneral != null)
                tables["general"] = tableInGeneral;
            tables["optics"] = tableInOptics;
            tables["particles"] = tableOutFiltered;

            Star.SaveMultitable(ResFilteredStarFile, tables);
        }
        else
        {
            tableOutFiltered.Save(ResFilteredStarFile);
        }

        WriteLog($"Saved filtered particles to {ResFilteredStarFile}");

        // If the input has an optimisation_set.star, adapt it to point to our filtered output
        if (!string.IsNullOrEmpty(resourceParticles.OptimisationSetStarPath) &&
            File.Exists(resourceParticles.OptimisationSetStarPath))
        {
            var setTable = new StarParameters(resourceParticles.OptimisationSetStarPath);
            if (setTable.HasColumn("rlnTomoParticlesFile"))
                setTable.SetRowValue(0, "rlnTomoParticlesFile", Space.GetRelativePath(ResFilteredStarFile));
            setTable.Save(ResOptimisationSetStarFile);
            WriteLog($"Saved adapted optimisation_set to {ResOptimisationSetStarFile}");
        }

        Log.ForContext<ThresholdStatistics>()
           .Information("Saved {FilteredCount}/{TotalCount} particles to {Path}",
                        rowsSelected.Count, tableInParticles.RowCount, ResFilteredStarFile);
    }
}