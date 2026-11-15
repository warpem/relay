using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Refund.DataModel;

/// <summary>Disk usage of a folder tree. Entries (files, folders and links) are what a file-count quota counts.</summary>
public readonly record struct UsageTotals(long Bytes, long Allocated, long Files, long Folders, long Links, long Errors)
{
    /// <summary>Inodes: files, folders and links.</summary>
    public long Entries => Files + Folders + Links;

    public static UsageTotals operator +(UsageTotals a, UsageTotals b) =>
        new(a.Bytes + b.Bytes, a.Allocated + b.Allocated, a.Files + b.Files, a.Folders + b.Folders, a.Links + b.Links, a.Errors + b.Errors);

    internal string Encode() => string.Create(CultureInfo.InvariantCulture, $"{Bytes} {Allocated} {Files} {Folders} {Links} {Errors}");

    internal static UsageTotals Decode(string text)
    {
        var p = StorageEncoding.Longs(text, 6);
        return new(p[0], p[1], p[2], p[3], p[4], p[5]);
    }
}

/// <summary>Allocated bytes and entries of one part of a job: a folder, a file type or an iteration.</summary>
public readonly record struct UsagePart(long Allocated, long Entries)
{
    public static UsagePart operator +(UsagePart a, UsagePart b) => new(a.Allocated + b.Allocated, a.Entries + b.Entries);
}

/// <summary>A folder inside a job (path relative to the job folder, '/'-separated); totals include its subfolders.</summary>
public readonly record struct UsageFolder(string Path, UsagePart Usage);

/// <summary>Files with one extension (lower case, with the dot; empty for files without one).</summary>
public readonly record struct UsageKind(string Extension, UsagePart Usage);

/// <summary>Files belonging to one iteration, numbered as on disk.</summary>
public readonly record struct UsageIteration(int Iteration, UsagePart Usage);

/// <summary>
/// One measurement of a job's folder. Immutable: a new measurement replaces it. Stored with the job in space.relay.
/// </summary>
[JsonConverter(typeof(JobStorageConverter))]
public sealed class JobStorage
{
    public static readonly UsageFolder[] NoFolders = [];
    public static readonly UsageKind[] NoKinds = [];
    public static readonly UsageIteration[] NoIterations = [];

    /// <summary>When the measurement finished (UTC).</summary>
    public DateTime MeasuredAt { get; init; }
    /// <summary>How long walking the folder took.</summary>
    public int DurationMs { get; init; }
    /// <summary>The job's status when it was measured.</summary>
    public JobStatus Status { get; init; }
    /// <summary>Start of the run the measurement belongs to (the latest RunningStarted event), if any.</summary>
    public DateTime? RunStartedAt { get; init; }
    /// <summary>Whether the job folder existed.</summary>
    public bool Exists { get; init; }
    public UsageTotals Total { get; init; }
    /// <summary>Most recent modification of anything inside (UTC).</summary>
    public DateTime? NewestWrite { get; init; }
    /// <summary>The biggest folders, by allocated bytes or entries, at most <see cref="MaxFolders"/>.</summary>
    public UsageFolder[] Folders { get; init; } = NoFolders;
    /// <summary>The biggest file types, at most <see cref="MaxKinds"/>.</summary>
    public UsageKind[] Kinds { get; init; } = NoKinds;
    /// <summary>Files that belong to one iteration, for jobs whose type knows its iteration files.</summary>
    public UsageIteration[] Iterations { get; init; } = NoIterations;
    /// <summary>Files the job type can always regenerate or never needs again (worker pools, staging leftovers).</summary>
    public UsagePart Regenerable { get; init; }

    public const int MaxFolders = 16, MaxKinds = 8;

    /// <summary>A job without a folder.</summary>
    public static JobStorage Missing(DateTime measuredAt, JobStatus status, DateTime? runStartedAt) =>
        new() { MeasuredAt = measuredAt, Status = status, RunStartedAt = runStartedAt, Exists = false };

    /// <summary>
    /// This measurement as it stands after cleaning with <paramref name="keep"/>, until the folder is measured again.
    /// </summary>
    public JobStorage AfterCleaning(IReadOnlySet<int> keep)
    {
        var removed = Cleanable(keep);
        var total = Total;
        long files = Math.Max(0, total.Files - removed.Entries);
        return new JobStorage
        {
            MeasuredAt = MeasuredAt, DurationMs = DurationMs, Status = Status, RunStartedAt = RunStartedAt, Exists = Exists,
            Total = new(Math.Max(0, total.Bytes - removed.Allocated), Math.Max(0, total.Allocated - removed.Allocated), files,
                total.Folders, total.Links, total.Errors),
            NewestWrite = NewestWrite, Folders = Folders, Kinds = Kinds,
            Iterations = Iterations.Where(i => keep.Contains(i.Iteration)).ToArray(), Regenerable = default,
        };
    }

    /// <summary>
    /// Bytes and entries that cleaning intermediate files would remove, given the iterations to keep.
    /// </summary>
    public UsagePart Cleanable(IReadOnlySet<int> keep)
    {
        UsagePart sum = Regenerable;
        foreach (var it in Iterations)
            if (keep == null || !keep.Contains(it.Iteration))
                sum += it.Usage;
        return sum;
    }
}

/// <summary>A folder in a space's root that is not the folder of any of its jobs.</summary>
public readonly record struct SpaceLeftover(string Name, int? JobId, UsageTotals Total, DateTime? NewestWrite, DateTime MeasuredAt)
{
    /// <summary>Folders named after a job ID are Relay's own: the folder of a deleted job.</summary>
    public bool IsJobFolder => JobId.HasValue;

    /// <summary>Whether an admin may delete it from the storage overlay.</summary>
    public bool IsDeletable => StorageLeftovers.IsDeletable(Name);
}

/// <summary>What a space's root holds besides its jobs' folders. Stored with the space in space.relay.</summary>
[JsonConverter(typeof(SpaceStorageConverter))]
public sealed class SpaceStorage
{
    public static readonly SpaceLeftover[] NoLeftovers = [];

    /// <summary>When the root was last listed (UTC).</summary>
    public DateTime ListedAt { get; init; }
    public SpaceLeftover[] Leftovers { get; init; } = NoLeftovers;
    /// <summary>Files directly in the root, such as space.relay.</summary>
    public UsageTotals LooseFiles { get; init; }

    public UsageTotals LeftoverTotal
    {
        get
        {
            UsageTotals sum = default;
            foreach (var l in Leftovers) sum += l.Total;
            return sum;
        }
    }
}

/// <summary>Compact text forms: numbers separated by spaces, a name after the numbers.</summary>
internal static class StorageEncoding
{
    public static long[] Longs(string text, int count)
    {
        var result = new long[count];
        var span = text.AsSpan();
        for (int i = 0; i < count; i++)
        {
            span = span.TrimStart(' ');
            int end = span.IndexOf(' ');
            var token = end < 0 ? span : span[..end];
            result[i] = token.IsEmpty ? 0 : long.Parse(token, NumberStyles.Integer, CultureInfo.InvariantCulture);
            span = end < 0 ? ReadOnlySpan<char>.Empty : span[(end + 1)..];
        }
        return result;
    }

    /// <summary>"allocated entries name"; the name is the rest of the line and may contain spaces.</summary>
    public static string Part(UsagePart part, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"{part.Allocated} {part.Entries} {name}");

    public static (UsagePart Part, string Name) ParsePart(string text)
    {
        int a = text.IndexOf(' '), b = a < 0 ? -1 : text.IndexOf(' ', a + 1);
        if (a < 0 || b < 0) throw new FormatException($"Not a usage part: {text}");
        return (new(long.Parse(text.AsSpan(0, a), CultureInfo.InvariantCulture), long.Parse(text.AsSpan(a + 1, b - a - 1), CultureInfo.InvariantCulture)), text[(b + 1)..]);
    }

    public static void WriteTime(Utf8JsonWriter w, string name, DateTime? time)
    {
        if (time.HasValue) w.WriteString(name, DateTime.SpecifyKind(time.Value, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
    }

    public static DateTime ReadTime(ref Utf8JsonReader r) =>
        DateTime.Parse(r.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}

/// <summary>
/// Writes a measurement in a few short properties instead of nested arrays, so that space.relay (written indented)
/// grows by about a kilobyte per measured job.
/// </summary>
internal sealed class JobStorageConverter : JsonConverter<JobStorage>
{
    public override JobStorage Read(ref Utf8JsonReader r, Type typeToConvert, JsonSerializerOptions options)
    {
        if (r.TokenType == JsonTokenType.Null) return null;
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a storage object");
        DateTime at = default; int ms = 0; JobStatus status = JobStatus.Building; DateTime? run = null, newest = null;
        bool exists = false; UsageTotals total = default; UsagePart regenerable = default;
        List<UsageFolder> folders = null; List<UsageKind> kinds = null; List<UsageIteration> iterations = null;
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            string name = r.GetString(); r.Read();
            switch (name)
            {
                case "At": at = StorageEncoding.ReadTime(ref r); break;
                case "Ms": ms = r.GetInt32(); break;
                case "Status": status = Enum.TryParse<JobStatus>(r.GetString(), out var s) ? s : JobStatus.Building; break;
                case "Run": run = StorageEncoding.ReadTime(ref r); break;
                case "Exists": exists = r.GetBoolean(); break;
                case "Total": total = UsageTotals.Decode(r.GetString()); break;
                case "Newest": newest = StorageEncoding.ReadTime(ref r); break;
                case "Regenerable": { var p = StorageEncoding.Longs(r.GetString(), 2); regenerable = new(p[0], p[1]); break; }
                case "Folders":
                    folders = [];
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray) { var (part, path) = StorageEncoding.ParsePart(r.GetString()); folders.Add(new(path, part)); }
                    break;
                case "Kinds":
                    kinds = [];
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray) { var (part, ext) = StorageEncoding.ParsePart(r.GetString()); kinds.Add(new(ext, part)); }
                    break;
                case "Iterations":
                    iterations = [];
                    foreach (var token in r.GetString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var f = token.Split(':');
                        iterations.Add(new(int.Parse(f[0], CultureInfo.InvariantCulture), new(long.Parse(f[1], CultureInfo.InvariantCulture), long.Parse(f[2], CultureInfo.InvariantCulture))));
                    }
                    break;
                default: r.Skip(); break;
            }
        }
        return new JobStorage
        {
            MeasuredAt = at, DurationMs = ms, Status = status, RunStartedAt = run, Exists = exists, Total = total, NewestWrite = newest,
            Folders = folders?.ToArray() ?? JobStorage.NoFolders, Kinds = kinds?.ToArray() ?? JobStorage.NoKinds,
            Iterations = iterations?.ToArray() ?? JobStorage.NoIterations, Regenerable = regenerable,
        };
    }

    public override void Write(Utf8JsonWriter w, JobStorage v, JsonSerializerOptions options)
    {
        w.WriteStartObject();
        StorageEncoding.WriteTime(w, "At", v.MeasuredAt);
        w.WriteNumber("Ms", v.DurationMs);
        w.WriteString("Status", v.Status.ToString());
        StorageEncoding.WriteTime(w, "Run", v.RunStartedAt);
        w.WriteBoolean("Exists", v.Exists);
        if (v.Exists)
        {
            w.WriteString("Total", v.Total.Encode());
            StorageEncoding.WriteTime(w, "Newest", v.NewestWrite);
            if (v.Folders.Length > 0)
            {
                w.WriteStartArray("Folders");
                foreach (var f in v.Folders) w.WriteStringValue(StorageEncoding.Part(f.Usage, f.Path));
                w.WriteEndArray();
            }
            if (v.Kinds.Length > 0)
            {
                w.WriteStartArray("Kinds");
                foreach (var k in v.Kinds) w.WriteStringValue(StorageEncoding.Part(k.Usage, k.Extension));
                w.WriteEndArray();
            }
            if (v.Iterations.Length > 0)
            {
                var sb = new StringBuilder(v.Iterations.Length * 16);
                foreach (var it in v.Iterations)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(CultureInfo.InvariantCulture, $"{it.Iteration}:{it.Usage.Allocated}:{it.Usage.Entries}");
                }
                w.WriteString("Iterations", sb.ToString());
            }
            if (v.Regenerable.Entries > 0)
                w.WriteString("Regenerable", string.Create(CultureInfo.InvariantCulture, $"{v.Regenerable.Allocated} {v.Regenerable.Entries}"));
        }
        w.WriteEndObject();
    }
}

internal sealed class SpaceStorageConverter : JsonConverter<SpaceStorage>
{
    public override SpaceStorage Read(ref Utf8JsonReader r, Type typeToConvert, JsonSerializerOptions options)
    {
        if (r.TokenType == JsonTokenType.Null) return null;
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a storage object");
        DateTime listed = default; UsageTotals loose = default; List<SpaceLeftover> leftovers = null;
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            string name = r.GetString(); r.Read();
            switch (name)
            {
                case "Listed": listed = StorageEncoding.ReadTime(ref r); break;
                case "Loose": loose = UsageTotals.Decode(r.GetString()); break;
                case "Leftovers":
                    leftovers = [];
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        // "measuredTicks newestTicks bytes allocated files folders links errors name"
                        string text = r.GetString();
                        int cut = 0;
                        for (int i = 0; i < 8; i++) cut = text.IndexOf(' ', cut) + 1;
                        var n = StorageEncoding.Longs(text[..(cut - 1)], 8);
                        string folder = text[cut..];
                        leftovers.Add(new(folder, StorageLeftovers.JobIdOf(folder), new(n[2], n[3], n[4], n[5], n[6], n[7]),
                            n[1] == 0 ? null : new DateTime(n[1], DateTimeKind.Utc), new DateTime(n[0], DateTimeKind.Utc)));
                    }
                    break;
                default: r.Skip(); break;
            }
        }
        return new SpaceStorage { ListedAt = listed, LooseFiles = loose, Leftovers = leftovers?.ToArray() ?? SpaceStorage.NoLeftovers };
    }

    public override void Write(Utf8JsonWriter w, SpaceStorage v, JsonSerializerOptions options)
    {
        w.WriteStartObject();
        StorageEncoding.WriteTime(w, "Listed", v.ListedAt);
        w.WriteString("Loose", v.LooseFiles.Encode());
        if (v.Leftovers.Length > 0)
        {
            w.WriteStartArray("Leftovers");
            foreach (var l in v.Leftovers)
                w.WriteStringValue(string.Create(CultureInfo.InvariantCulture,
                    $"{l.MeasuredAt.Ticks} {l.NewestWrite?.Ticks ?? 0} {l.Total.Encode()} {l.Name}"));
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }
}

/// <summary>Rules for folders in a space root.</summary>
public static class StorageLeftovers
{
    /// <summary>A leftover folder being deleted is first renamed with this prefix.</summary>
    public const string DeletingPrefix = ".relay-deleting-";

    /// <summary>Whether a folder in a space root is Relay's own and may be deleted when no job owns it.</summary>
    public static bool IsDeletable(string folderName) =>
        JobIdOf(folderName).HasValue || folderName.StartsWith(DeletingPrefix, StringComparison.Ordinal);

    /// <summary>The job ID a folder name stands for, when it is a plain positive number (how Relay names job folders).</summary>
    public static int? JobIdOf(string folderName) =>
        folderName.Length is > 0 and <= 9 && folderName[0] != '0' && folderName.All(char.IsAsciiDigit) ? int.Parse(folderName, CultureInfo.InvariantCulture) : null;
}
