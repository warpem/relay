using System.Text.Json;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs;
using Serilog;

namespace Refund.Utils;

/// <summary>
/// Reads output files once at finalization. Resource descriptions and UI rendering never call this.
/// </summary>
public static class ResourceItemCounter
{
    private static readonly HashSet<Type> CountedResourceTypes =
    [typeof(ParticleSet), typeof(MicrographSet), typeof(TiltSeriesSet), typeof(TomogramSet), typeof(DataSetFs), typeof(DataSetTs)];

    public static Dictionary<string, long> CountOutputs(Job job, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var counts = new Dictionary<string, long>();
        var starCounts = new Dictionary<string, long?>(StringComparer.Ordinal);
        var jsonCounts = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var port in job.PortsOut.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!CountedResourceTypes.Contains(port.ResourceType))
                continue;
            try
            {
                // A fresh description avoids accidentally saving a previous run's count again.
                long? count = port.GetResourceDescription() switch
                {
                    ParticleSet particles => CountParticles(job, particles, starCounts, cancellationToken),
                    MicrographSet micrographs => CountProcessedItems(micrographs.ProcessedItemsJson, jsonCounts, cancellationToken),
                    TiltSeriesSet tiltSeries => CountProcessedItems(tiltSeries.ProcessedItemsJson, jsonCounts, cancellationToken),
                    TomogramSet tomograms => CountProcessedItems(tomograms.ProcessedItemsJson, jsonCounts, cancellationToken),
                    DataSetFs movies => CountFiles(movies.DataDirectory, movies.FileSearchPattern,
                        movies.DoRecursiveSearch ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly, cancellationToken),
                    DataSetTs tiltSeries => CountFiles(tiltSeries.DataDirectory, "*.tomostar", SearchOption.TopDirectoryOnly, cancellationToken),
                    _ => null // Counts derived from settings remain in the resource description.
                };
                if (count.HasValue)
                    counts[port.Name] = count.Value;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or JsonException or ArgumentException or FormatException or NotImplementedException)
            {
                // Counts are optional metadata: unavailable or malformed outputs must not turn a
                // completed scientific computation into a failed job, or be reported as zero.
                Log.ForContext(typeof(ResourceItemCounter)).Warning(exception,
                    "Could not count output {PortName} for job {JobId}", port.Name, job.Id);
            }
        }

        return counts;
    }

    private static long? CountParticles(Job job, ParticleSet particles,
        Dictionary<string, long?> cache, CancellationToken cancellationToken)
    {
        if (particles.HasSingleStar)
            return CountStar(particles.ParticlesSingleStarPath, cache, cancellationToken);
        if (!particles.HasMultiStar || !Directory.Exists(particles.ParticlesMultiStarDirectory))
            return null;

        // Use this job's successful items where available, so failed/unprocessed images are not
        // mistaken for missing particle files. Pass-through sets fall back to their source items.
        string processedItems = job is WarpJob warp && File.Exists(warp.ResProcessedItemsJson)
            ? warp.ResProcessedItemsJson
            : particles.PickedInTomograms?.ProcessedItemsJson ?? particles.PickedInMicrographs?.ProcessedItemsJson;

        IEnumerable<string> paths;
        if (particles.ToMultiStarPath != null && File.Exists(processedItems))
        {
            paths = ReadProcessedItems(processedItems, cancellationToken).Select(item =>
            {
                if (!item.TryGetProperty("Path", out var path) || path.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("A processed item has no file path.");
                return particles.ToMultiStarPath(path.GetString());
            });
        }
        else
        {
            // Imported coordinate sets have no linked micrographs/tomograms. Derive the filename
            // filter from the same mapper consumers use, excluding other templates and STAR tables.
            const string probe = "__relay_item_count_probe__";
            string prefix = "", suffix = ".star";
            if (particles.ToMultiStarPath != null)
            {
                string mapped = Path.GetFileName(particles.ToMultiStarPath(probe));
                int position = mapped.IndexOf(probe, StringComparison.Ordinal);
                if (position < 0)
                    return null;
                prefix = mapped[..position];
                suffix = mapped[(position + probe.Length)..];
            }
            paths = Directory.EnumerateFiles(particles.ParticlesMultiStarDirectory, "*.star")
                .Where(path => Path.GetFileName(path).StartsWith(prefix, StringComparison.Ordinal)
                    && Path.GetFileName(path).EndsWith(suffix, StringComparison.Ordinal));
        }

        long count = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(Path.GetFullPath(path)))
                continue;
            // Per-image STAR output is sparse: picking can produce no file for zero picks,
            // and particle selection skips images without an input STAR. Consumers likewise
            // skip these files. The enclosing directory must still exist to report a count.
            if (!File.Exists(path))
                continue;
            long? fileCount = CountStar(path, cache, cancellationToken);
            if (!fileCount.HasValue)
                return null;
            count = checked(count + fileCount.Value);
        }
        return count;
    }

    private static long? CountStar(string path, Dictionary<string, long?> cache, CancellationToken cancellationToken)
    {
        string key = Path.GetFullPath(path);
        if (!cache.TryGetValue(key, out long? count))
        {
            count = File.Exists(path) ? StarItemCounter.CountParticles(path, cancellationToken) : null;
            cache[key] = count;
        }
        return count;
    }

    private static long? CountProcessedItems(string path, Dictionary<string, long?> cache, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        string key = Path.GetFullPath(path);
        if (!cache.TryGetValue(key, out long? count))
        {
            count = File.Exists(path) ? ReadProcessedItems(path, cancellationToken).LongCount() : null;
            cache[key] = count;
        }
        return count;
    }

    private static IEnumerable<JsonElement> ReadProcessedItems(string path, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(stream,
            cancellationToken: cancellationToken).ToBlockingEnumerable(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The processed-items list contains a non-object entry.");
            yield return item;
        }
    }

    private static long? CountFiles(string directory, string pattern, SearchOption searchOption,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory) || string.IsNullOrEmpty(pattern))
            return null;
        long count = 0;
        foreach (string _ in Directory.EnumerateFiles(directory, pattern, searchOption))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }
        return count;
    }
}
