using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Refund.JobResources;
using static Refund.Utils.Star.StreamingStar;

namespace Refund.Utils.Star;

/// <summary>
/// Merges particle metadata, never images. Only optics and tomogram definitions are retained;
/// particle loops are streamed and are deliberately neither deduplicated nor renumbered.
/// </summary>
internal sealed class ParticleStarMerger(CancellationToken cancellationToken)
{
    private sealed class Optics
    {
        public string Id, Name;
        public string[] Columns, Row;
    }

    private sealed class Input
    {
        public string Label;
        public StreamingStar Particles, Tomograms, Optimisation;
        public Dictionary<string, Optics> OpticsById = new();
        public Dictionary<string, Optics> OpticsByName = new();
        public HashSet<string> TomogramNames = new();
    }

    private sealed record Tomogram(Input Input, Block Global, string[] Row, Block Tilts, string Signature);
    private readonly List<Input> _inputs = [];
    private readonly List<Optics> _optics = [];
    private readonly Dictionary<string, Tomogram> _tomograms = new();
    private long _metadataBytes;
    private const long MetadataBudget = 64 * 1024 * 1024;

    public void Merge(IReadOnlyList<(string Label, ParticleSet Resource)> resources,
        string directory, string finalDirectory, string rootDirectory)
    {
        foreach (var (label, resource) in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var input = new Input
                {
                    Label = label,
                    Particles = new StreamingStar(resource.ParticlesSingleStarPath, cancellationToken)
                };
                // Old Warp/RELION exports use an anonymous particle loop.
                foreach (var block in input.Particles.Blocks.Where(b => b.Name == "" && b.IsLoop))
                    block.Name = "particles";
                if (!RequireBlock(input.Particles, "particles").IsLoop)
                    throw new InvalidDataException($"{input.Particles.Path}: data_particles must be a loop.");
                if (resource.DataDimensionality == ParticleType.Tiltseries)
                {
                    input.Tomograms = new StreamingStar(resource.TomogramsStarPath, cancellationToken);
                    if (!string.IsNullOrEmpty(resource.OptimisationSetStarPath))
                        input.Optimisation = new StreamingStar(resource.OptimisationSetStarPath, cancellationToken);
                }
                foreach (var file in new[] { input.Particles, input.Tomograms, input.Optimisation }.Where(f => f != null))
                    foreach (var block in file.Blocks)
                    {
                        Reserve([block.Name], 192);
                        Reserve(block.Columns.ToArray());
                        Reserve(block.Scalars.ToArray());
                    }
                _inputs.Add(input);
                ReadOptics(input);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidDataException($"{label}: {ex.Message}", ex);
            }
        }

        CheckBlockSets(_inputs.Select(i => i.Particles).ToList());
        foreach (var input in _inputs.Where(i => i.Tomograms != null)) ReadTomograms(input);
        var activeOptics = _inputs.SelectMany(i => i.OpticsById.Values).ToHashSet();
        int nextOpticsId = 0;
        foreach (var optic in _optics.Where(activeOptics.Contains))
            optic.Id = (++nextOpticsId).ToString(CultureInfo.InvariantCulture);
        Directory.CreateDirectory(directory);
        WriteParticles(System.IO.Path.Combine(directory, "particles.star"));
        if (_inputs[0].Tomograms != null)
        {
            WriteTomograms(System.IO.Path.Combine(directory, "tomograms.star"));
            WriteOptimisation(System.IO.Path.Combine(directory, "optimisation_set.star"), finalDirectory, rootDirectory);
        }
        foreach (var input in _inputs)
        {
            input.Particles.EnsureUnchanged();
            input.Tomograms?.EnsureUnchanged();
            input.Optimisation?.EnsureUnchanged();
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private void Reserve(string[] values, long overhead = 0)
    {
        _metadataBytes += overhead + values.Sum(v => 24L + v.Length * 2L) + 8L * values.Length;
        if (_metadataBytes > MetadataBudget)
            throw new InvalidDataException("Optics/tomogram definitions exceed the 64 MiB merge metadata budget. Particle rows are streamed and do not count toward this limit.");
    }

    private void ReadOptics(Input input)
    {
        var block = input.Particles.FindBlock("optics");
        if (block == null) return;
        if (!block.IsLoop) throw new InvalidDataException($"{input.Label}: data_optics must be a loop.");
        int id = Column(block, "rlnOpticsGroup"), name = block.Columns.IndexOf("rlnOpticsGroupName");
        var usedNames = _optics.Select(o => o.Name).ToHashSet();
        input.Particles.ReadRows(block, row =>
        {
            string oldId = OpticsId(Value(row[id]));
            string oldName = name < 0 ? "opticsGroup" + oldId : Value(row[name]);
            if (input.OpticsById.ContainsKey(oldId) || input.OpticsByName.ContainsKey(oldName))
                throw new InvalidDataException($"{input.Label}: duplicate optics group ID or name ({oldId}, {oldName}).");
            string newId = (_optics.Count + 1).ToString(CultureInfo.InvariantCulture);
            string newName = oldName;
            for (int suffix = 2; !usedNames.Add(newName); suffix++) newName = oldName + "_relay" + suffix;
            Reserve(row);
            var optic = new Optics { Id = newId, Name = newName, Columns = block.Columns.ToArray(), Row = (string[])row.Clone() };
            _optics.Add(optic);
            input.OpticsById.Add(oldId, optic);
            input.OpticsByName.Add(oldName, optic);
        }, cancellationToken);
    }

    private static string OpticsId(string value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : throw new InvalidDataException($"Invalid optics group ID '{value}'. Expected a positive integer.");

    private void ReadTomograms(Input input)
    {
        var global = RequireBlock(input.Tomograms, "global");
        if (!global.IsLoop) throw new InvalidDataException($"{input.Label}: data_global must be a loop.");
        int nameColumn = Column(global, "rlnTomoName");
        var names = input.TomogramNames;
        input.Tomograms.ReadRows(global, row =>
        {
            string name = Value(row[nameColumn]);
            if (!names.Add(name)) throw new InvalidDataException($"{input.Label}: duplicate tomogram definition '{name}'.");
            var tilts = input.Tomograms.FindBlock(name);
            if (tilts == null && !global.Columns.Contains("rlnTomoTiltSeriesStarFile"))
                throw new InvalidDataException($"{input.Label}: tomogram '{name}' has no tilt-series table or file reference.");
            string signature = DefinitionSignature(input, global, row) + (tilts == null ? "" : BlockSignature(input, tilts));
            if (_tomograms.TryGetValue(name, out var existing))
            {
                if (signature != existing.Signature)
                    throw new InvalidDataException($"{input.Label} conflicts with {existing.Input.Label}: tomogram '{name}' has different definitions or tilt-series data.");
                // A shared tomogram definition establishes shared optics provenance. Reuse its
                // optics assignment, rather than assigning the same tomogram two different groups.
                foreach (var column in new[] { "rlnOpticsGroupName", "rlnOpticsGroup" })
                {
                    int c = global.Columns.IndexOf(column);
                    if (c < 0) continue;
                    int previous = existing.Global.Columns.IndexOf(column);
                    var currentOptic = LookupOptic(input, column, Value(row[c]));
                    var previousOptic = LookupOptic(existing.Input, column, Value(existing.Row[previous]));
                    foreach (var key in input.OpticsById.Where(p => p.Value == currentOptic).Select(p => p.Key).ToArray())
                        input.OpticsById[key] = previousOptic;
                    foreach (var key in input.OpticsByName.Where(p => p.Value == currentOptic).Select(p => p.Key).ToArray())
                        input.OpticsByName[key] = previousOptic;
                }
            }
            else
            {
                Reserve(row);
                _tomograms.Add(name, new Tomogram(input, global, (string[])row.Clone(), tilts, signature));
            }
        }, cancellationToken);
        foreach (var block in input.Tomograms.Blocks)
            if (block != global && !names.Contains(block.Name))
                throw new InvalidDataException($"{input.Label}: data_{block.Name} has no corresponding tomogram definition.");
    }

    private static Optics LookupOptic(Input input, string column, string value)
    {
        var map = column == "rlnOpticsGroup" ? input.OpticsById : input.OpticsByName;
        string key = column == "rlnOpticsGroup" ? OpticsId(value) : value;
        if (!map.TryGetValue(key, out var optic))
            throw new InvalidDataException($"{input.Label}: _{column} refers to undefined optics group '{value}'.");
        return optic;
    }

    private static string DefinitionSignature(Input input, Block block, string[] row)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var column in block.Columns.Order(StringComparer.Ordinal))
        {
            AddHash(hash, column);
            string value = Value(row[block.Columns.IndexOf(column)]);
            if (column is "rlnOpticsGroup" or "rlnOpticsGroupName")
            {
                var optics = LookupOptic(input, column, value);
                foreach (var label in optics.Columns.Where(c => c is not ("rlnOpticsGroup" or "rlnOpticsGroupName")).Order(StringComparer.Ordinal))
                {
                    AddHash(hash, label);
                    AddHash(hash, Value(optics.Row[Array.IndexOf(optics.Columns, label)]));
                }
            }
            else AddHash(hash, value);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private string BlockSignature(Input input, Block block)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddHash(hash, block.IsLoop.ToString());
        var order = block.Columns.Order(StringComparer.Ordinal).Select(c => block.Columns.IndexOf(c)).ToArray();
        foreach (int c in order) AddHash(hash, block.Columns[c]);
        if (block.IsLoop)
            input.Tomograms.ReadRows(block, row =>
            {
                // References may use different local optics IDs in otherwise identical
                // tilt-series definitions; compare the referenced optics values instead.
                AddHash(hash, DefinitionSignature(input, block, row));
            }, cancellationToken);
        else
            AddHash(hash, DefinitionSignature(input, block, block.Scalars.ToArray()));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AddHash(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private void WriteParticles(string path)
    {
        using var writer = Writer(path);
        foreach (var first in _inputs[0].Particles.Blocks)
        {
            var blocks = _inputs.Select(i => RequireBlock(i.Particles, first.Name)).ToArray();
            foreach (var block in blocks) CheckColumns(first, block);
            if (!first.IsLoop)
            {
                var transformed = blocks.Select((b, i) => TransformScalars(_inputs[i], b)).ToArray();
                WriteScalars(writer, transformed[0], transformed);
                continue;
            }
            WriteHeader(writer, first.Name, first.Columns);
            if (first.Name == "optics")
            {
                var active = _inputs.SelectMany(i => i.OpticsById.Values).ToHashSet();
                foreach (var optic in _optics.Where(active.Contains))
                    WriteRow(writer, first.Columns.Select(c => c == "rlnOpticsGroup" ? optic.Id
                        : c == "rlnOpticsGroupName" ? Quote(optic.Name) : optic.Row[Array.IndexOf(optic.Columns, c)]));
                continue;
            }
            foreach (var input in _inputs)
            {
                var block = RequireBlock(input.Particles, first.Name);
                StreamBlock(writer, input, input.Particles, block, first.Columns);
            }
        }
    }

    private void StreamBlock(StreamWriter writer, Input input, StreamingStar file, Block block, List<string> columns)
    {
        int[] order = columns.Select(c => block.Columns.IndexOf(c)).ToArray();
        var operations = new RewriteValue[block.Columns.Count];
        for (int c = 0; c < block.Columns.Count; c++)
        {
            // Build only the operations this table actually needs. Ordinary particle values
            // never go through string decoding, column-name comparisons or transformation.
            switch (block.Columns[c])
            {
                case "rlnOpticsGroup":
                    var ids = input.OpticsById.ToDictionary(p => long.Parse(p.Key, CultureInfo.InvariantCulture),
                        p => Encoding.UTF8.GetBytes(p.Value.Id));
                    operations[c] = raw =>
                    {
                        var value = Utf8Value(raw);
                        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
                            throw new InvalidDataException($"{input.Label}: invalid optics group ID '{Encoding.UTF8.GetString(value)}'.");
                        if (!ids.TryGetValue(id, out var replacement))
                            throw new InvalidDataException($"{input.Label}: _rlnOpticsGroup refers to undefined optics group '{id}'.");
                        return replacement;
                    };
                    break;
                case "rlnOpticsGroupName":
                    operations[c] = NameOperation(input.OpticsByName.ToDictionary(p => p.Key,
                        p => Encoding.UTF8.GetBytes(Quote(p.Value.Name)), StringComparer.Ordinal),
                        input.Label, "optics group", preserve: false);
                    break;
                case "rlnTomoName" when input.Tomograms != null:
                    operations[c] = NameOperation(input.TomogramNames.ToDictionary(name => name,
                        _ => Array.Empty<byte>(), StringComparer.Ordinal), input.Label, "tomogram", preserve: true);
                    break;
            }
        }
        // Flush the small text headers before writing UTF-8 rows to the same stream.
        writer.Flush();
        file.RewriteRows(block, writer.BaseStream, order, operations, cancellationToken);
    }

    private static RewriteValue NameOperation(Dictionary<string, byte[]> names, string source, string kind, bool preserve)
    {
        // Alternate span lookup avoids allocating one string per particle for tomogram/name
        // references. The only retained character buffer is reused across the entire table.
        var lookup = names.GetAlternateLookup<ReadOnlySpan<char>>();
        char[] characters = new char[256];
        return raw =>
        {
            var value = Utf8Value(raw);
            if (value.Length > characters.Length) Array.Resize(ref characters, value.Length);
            int count = Encoding.UTF8.GetChars(value, characters);
            if (!lookup.TryGetValue(characters.AsSpan(0, count), out var replacement))
                throw new InvalidDataException($"{source}: refers to undefined {kind} '{new string(characters, 0, count)}'.");
            return preserve ? raw : replacement;
        };
    }

    private string Transform(Input input, string column, string raw)
    {
        string value;
        if (column is "rlnOpticsGroup" or "rlnOpticsGroupName")
        {
            var optic = LookupOptic(input, column, Value(raw));
            return column == "rlnOpticsGroup" ? optic.Id : Quote(optic.Name);
        }
        if (column == "rlnTomoName" && input.Tomograms != null)
        {
            value = Value(raw);
            if (!input.TomogramNames.Contains(value))
                throw new InvalidDataException($"{input.Label}: particle refers to undefined tomogram '{value}'.");
        }
        return raw;
    }

    private void WriteTomograms(string path)
    {
        using var writer = Writer(path);
        var first = RequireBlock(_inputs[0].Tomograms, "global");
        foreach (var input in _inputs) CheckColumns(first, RequireBlock(input.Tomograms, "global"));
        WriteHeader(writer, "global", first.Columns);
        foreach (var tomo in _tomograms.Values)
            WriteRow(writer, first.Columns.Select(c => Transform(tomo.Input, c, tomo.Row[tomo.Global.Columns.IndexOf(c)])));
        foreach (var (name, tomo) in _tomograms)
        {
            if (tomo.Tilts == null) continue;
            if (!tomo.Tilts.IsLoop)
            {
                var transformed = TransformScalars(tomo.Input, tomo.Tilts);
                WriteScalars(writer, transformed, [transformed]);
                continue;
            }
            WriteHeader(writer, name, tomo.Tilts.Columns);
            StreamBlock(writer, tomo.Input, tomo.Input.Tomograms, tomo.Tilts, tomo.Tilts.Columns);
        }
    }

    private void WriteOptimisation(string path, string finalDirectory, string rootDirectory)
    {
        // Trajectories are outside Relay's RELION workflow. Never carry a stale trajectory
        // pointer from one input into a merged particle set.
        var ignored = new HashSet<string> { "rlnTomoParticlesFile", "rlnTomoTomogramsFile", "rlnTomoTrajectoriesFile" };
        var values = new Dictionary<string, string>();
        foreach (var input in _inputs)
        {
            if (input.Optimisation == null) continue;
            foreach (var block in input.Optimisation.Blocks)
            {
                if (block.IsLoop) throw new InvalidDataException($"{input.Label}: optimisation set must contain scalar entries.");
                for (int c = 0; c < block.Columns.Count; c++)
                {
                    string column = block.Columns[c], raw = block.Scalars[c];
                    if (ignored.Contains(column)) continue;
                    if (values.TryGetValue(column, out string previous) && Value(previous) != Value(raw))
                        throw new InvalidDataException($"{input.Label}: conflicting optimisation-set value for _{column}.");
                    values[column] = raw;
                }
            }
        }
        values["rlnTomoParticlesFile"] = Quote(System.IO.Path.GetRelativePath(rootDirectory, System.IO.Path.Combine(finalDirectory, "particles.star")));
        values["rlnTomoTomogramsFile"] = Quote(System.IO.Path.GetRelativePath(rootDirectory, System.IO.Path.Combine(finalDirectory, "tomograms.star")));
        using var writer = Writer(path);
        writer.WriteLine("data_\n");
        foreach (var (column, raw) in values) WriteRow(writer, ["_" + column, raw]);
    }

    private static StreamWriter Writer(string path) => new(path, false, new UTF8Encoding(false), 65536);
    private static Block RequireBlock(StreamingStar file, string name) => file.FindBlock(name)
        ?? throw new InvalidDataException($"{file.Path}: missing data_{name}.");
    private static int Column(Block block, string column) => block.Columns.IndexOf(column) is var index && index >= 0 ? index
        : throw new InvalidDataException($"data_{block.Name}: missing _{column}.");

    private static void CheckBlockSets(List<StreamingStar> files)
    {
        var names = files[0].Blocks.Select(b => b.Name).ToHashSet();
        foreach (var file in files.Skip(1))
            if (!names.SetEquals(file.Blocks.Select(b => b.Name)))
                throw new InvalidDataException($"{file.Path}: table names differ from {files[0].Path}. Expected [{string.Join(", ", names)}], found [{string.Join(", ", file.Blocks.Select(b => b.Name))}].");
    }

    private static void CheckColumns(Block first, Block other)
    {
        if (first.IsLoop != other.IsLoop || !first.Columns.ToHashSet().SetEquals(other.Columns))
            throw new InvalidDataException($"{other.SourcePath}, data_{first.Name}: incompatible table layout compared with {first.SourcePath}. Missing columns: [{string.Join(", ", first.Columns.Except(other.Columns))}]; extra columns: [{string.Join(", ", other.Columns.Except(first.Columns))}].");
    }

    private Block TransformScalars(Input input, Block block) => new()
    {
        Name = block.Name, SourcePath = block.SourcePath, Columns = block.Columns,
        Scalars = block.Columns.Select((c, i) => Transform(input, c, block.Scalars[i])).ToList()
    };

    private static void WriteScalars(TextWriter writer, Block first, IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            CheckColumns(first, block);
            foreach (string column in first.Columns)
                if (Value(first.Scalars[first.Columns.IndexOf(column)]) != Value(block.Scalars[block.Columns.IndexOf(column)]))
                    throw new InvalidDataException($"{block.SourcePath}, data_{first.Name}: conflicting scalar value for _{column} compared with {first.SourcePath}.");
        }
        writer.WriteLine($"\ndata_{first.Name}\n");
        for (int c = 0; c < first.Columns.Count; c++) WriteRow(writer, ["_" + first.Columns[c], first.Scalars[c]]);
    }
}
