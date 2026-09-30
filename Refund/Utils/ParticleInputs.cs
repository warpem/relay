using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Utils.Star;

namespace Refund.Utils;

/// <summary>Descriptions and editor checks do no file I/O. Files are merged only during staging.</summary>
public static class ParticleInputs
{
    private static readonly SemaphoreSlim MergeSlots = new(2);
    public sealed record Issue(int SourceIndex, string Message);

    public static bool NeedsMerge(PortIn port) => port.IsActive() && port.ResourceType == typeof(ParticleSet)
        && port.MaxItems > 1 && port.Edges.Count > 1;

    public static List<Issue> Validate(IReadOnlyList<(string Label, ParticleSet Resource)> inputs)
    {
        var issues = new List<Issue>();
        if (inputs.Count < 2) return issues;
        for (int i = 0; i < inputs.Count; i++)
        {
            var resource = inputs[i].Resource;
            // Resources of unfinished/unconfigured producers may not yet be describable.
            if (resource == null) continue;
            if (!resource.HasSingleStar)
                issues.Add(new(i, "Merging requires a single particle STAR file per resource."));
            if (resource.DataDimensionality == ParticleType.Tiltseries && string.IsNullOrEmpty(resource.TomogramsStarPath))
                issues.Add(new(i, "Merging tilt-series particles requires tomogram metadata."));
            for (int j = 0; j < i; j++)
            {
                var other = inputs[j].Resource;
                if (other == null) continue;
                var differences = new List<string>();
                if (resource.DataDimensionality != other.DataDimensionality) differences.Add("particle representation");
                if (resource.HasData != other.HasData) differences.Add("extracted data versus positions only");
                if (resource.HasPositions && other.HasPositions)
                {
                    if (resource.Has3dCoords != other.Has3dCoords) differences.Add("coordinate dimensionality");
                    if (resource.HasNormalizedCoords != other.HasNormalizedCoords) differences.Add("normalized coordinates");
                    if (resource.HasCenteredCoords != other.HasCenteredCoords) differences.Add("coordinate origin");
                    // Image sampling is allowed to vary between optics groups. CoordPixelSize
                    // describes coordinate units, and is only a merge constraint for positions.
                    if (!resource.HasData && !resource.HasNormalizedCoords && !other.HasNormalizedCoords
                        && resource.CoordPixelSize != other.CoordPixelSize) differences.Add("coordinate pixel size");
                }
                if (differences.Count == 0) continue;
                string reason = string.Join(", ", differences);
                issues.Add(new(i, $"Incompatible with {inputs[j].Label}: {reason}."));
                issues.Add(new(j, $"Incompatible with {inputs[i].Label}: {reason}."));
            }
        }
        return issues;
    }

    public static ParticleSet GetDescription(ReadOnlyPortOut port) =>
        port == null ? null : TryDescribe(() => port.GetResource());

    private static ParticleSet TryDescribe(Func<Resource> describe)
    {
        try { return describe() as ParticleSet; }
        // Like PortOut.ItemCount, metadata inspection must tolerate unconfigured producers
        // and factory blueprints that do not yet have materialized storage.
        catch (InvalidOperationException) { return null; }
        catch (NullReferenceException) { return null; }
        catch (KeyNotFoundException) { return null; }
    }

    public static List<(string Label, ParticleSet Resource)> Sources(PortIn port) => port.Edges
        .Select(e => ($"J{e.Source.Job.Id} → {e.Source.Alias}", TryDescribe(() => e.Source.GetResource()))).ToList();

    public static ParticleSet Describe(PortIn port)
    {
        var sources = Sources(port);
        if (sources.Count == 0 || sources.Any(s => s.Resource == null)) return null;
        var resources = sources.Select(s => s.Resource).ToList();
        var result = resources[0].Copy();
        if (resources.Count == 1) return result;
        string directory = DirectoryFor(port);
        result.ParticlesSingleStarPath = Path.Combine(directory, "particles.star");
        result.ParticlesMultiStarDirectory = "";
        result.ToMultiStarPath = null;
        result.OptimisationSetStarPath = result.DataDimensionality == ParticleType.Tiltseries
            ? Path.Combine(directory, "optimisation_set.star") : null;
        result.TomogramsStarPath = result.DataDimensionality == ParticleType.Tiltseries
            ? Path.Combine(directory, "tomograms.star") : null;
        result.ItemCount = resources.All(r => r.ItemCount.HasValue) ? resources.Sum(r => r.ItemCount.Value) : null;
        result.HasData = resources.All(r => r.HasData);
        result.HasPositions = resources.All(r => r.HasPositions);
        result.HasAngles = resources.All(r => r.HasAngles);
        result.HasClasses = resources.All(r => r.HasClasses);
        result.HasShifts = resources.All(r => r.HasShifts);
        result.HasScale = resources.All(r => r.HasScale);
        result.HasCtf = resources.All(r => r.HasCtf);
        // These singular associations cannot describe independent input datasets. Do not
        // advertise the first source's maps/micrographs/tomograms as applying to every row.
        if (!resources.All(r => ReferenceEquals(r.PickedInMicrographs, result.PickedInMicrographs))) result.PickedInMicrographs = null;
        if (!resources.All(r => ReferenceEquals(r.PickedInTomograms, result.PickedInTomograms))) result.PickedInTomograms = null;
        if (!resources.All(r => ReferenceEquals(r.CorrespondingMaps, result.CorrespondingMaps))) result.CorrespondingMaps = null;
        if (!resources.All(r => r.Diameter == result.Diameter)) result.Diameter = 0;
        return result;
    }

    internal static string DirectoryFor(PortIn port) => Path.Combine(port.Job.DirectoryPath, "inputs", Uri.EscapeDataString(port.Name));

    internal static void Stage(Job job, CancellationToken cancellationToken)
    {
        foreach (var port in job.PortsIn.Values.Where(NeedsMerge))
        {
            var sources = Sources(port);
            var issues = Validate(sources);
            if (sources.Any(s => s.Resource == null))
                throw new InvalidDataException($"{port.Alias}: one or more particle inputs are unavailable.");
            if (issues.Count > 0)
                throw new InvalidDataException(string.Join("\n", issues.Select(i => $"{sources[i.SourceIndex].Label}: {i.Message}")));
            string destination = DirectoryFor(port);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            MergeSlots.Wait(cancellationToken);
            try
            {
                new ParticleStarMerger(cancellationToken).Merge(sources, temporary, destination, job.Space.RootDirectory);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                // Execution preparation resets the job directory before staging. Refuse to
                // replace a completed bundle accidentally when Stage is called twice.
                Directory.Move(temporary, destination);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"Cannot merge {port.Alias} inputs ({string.Join(", ", sources.Select(s => s.Label))}): {ex.Message}", ex);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
                }
                finally { MergeSlots.Release(); }
            }
        }
    }
}
