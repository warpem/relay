using System.Buffers;
using System.Text;

namespace Refund.Utils.Star;

/// <summary>
/// Indexes STAR blocks without retaining loop rows. Byte offsets allow each block to be
/// revisited directly, rather than rescanning a multi-GB file for every table.
/// </summary>
internal sealed class StreamingStar
{
    internal sealed class Block
    {
        public string Name;
        public string SourcePath;
        public bool IsLoop;
        public List<string> Columns = [];
        public List<string> Scalars = [];
        public long Start, End, RowCount;
        public bool StartsAtLineStart;
    }

    public string Path { get; }
    public List<Block> Blocks { get; } = [];
    private readonly long _length;
    private readonly DateTime _modified;
    private long _metadataBytes;
    private Dictionary<string, Block> _byName;

    public Block FindBlock(string name)
    {
        _byName ??= Blocks.ToDictionary(b => b.Name);
        return _byName.GetValueOrDefault(name);
    }

    public StreamingStar(string path, CancellationToken cancellationToken)
    {
        Path = path;
        var info = new FileInfo(path);
        _length = info.Length;
        _modified = info.LastWriteTimeUtc;
        using var reader = new Tokens(path, cancellationToken);
        var names = new HashSet<string>();
        var token = reader.Read();
        while (token != null)
        {
            if (!token.Value.IsData)
                throw Error("Expected a data_ block", token.Value.Start);
            var block = new Block { Name = token.Value.Value[5..], SourcePath = path };
            Reserve(block.Name, 192);
            if (!names.Add(block.Name))
                throw Error($"Duplicate data_{block.Name} block", token.Value.Start);
            Blocks.Add(block);
            token = reader.Read();
            if (token is { IsLoop: true })
            {
                block.IsLoop = true;
                token = reader.Read();
                while (token is { IsTag: true })
                {
                    AddColumn(block, token.Value.Value[1..]);
                    token = reader.Read();
                }
                if (block.Columns.Count == 0)
                    throw Error($"data_{block.Name}: loop has no columns", reader.Position);
                block.Start = token?.Start ?? reader.Position;
                block.StartsAtLineStart = token?.AtLineStart ?? true;
                long count = 0;
                while (token != null && !token.Value.IsControl)
                {
                    count++;
                    token = reader.Read(capture: false);
                }
                block.End = token?.Start ?? reader.Position;
                if (count % block.Columns.Count != 0)
                    throw Error($"data_{block.Name}: incomplete row ({count} values for {block.Columns.Count} columns)", block.End);
                block.RowCount = count / block.Columns.Count;
                if (token is { IsStop: true })
                    token = reader.Read();
            }
            else
            {
                while (token is { IsTag: true })
                {
                    AddColumn(block, token.Value.Value[1..]);
                    token = reader.Read();
                    if (token == null || token.Value.IsControl)
                        throw Error($"data_{block.Name}: missing scalar value", reader.Position);
                    Reserve(token.Value.Raw);
                    block.Scalars.Add(token.Value.Raw);
                    token = reader.Read();
                }
            }
            if (token != null && !token.Value.IsData)
                throw Error($"data_{block.Name}: multiple loops or mixed scalar/loop blocks are not supported", token.Value.Start);
        }
        if (Blocks.Count == 0)
            throw Error("No STAR blocks found", 0);
        EnsureUnchanged();
    }

    private void AddColumn(Block block, string name)
    {
        if (block.Columns.Contains(name))
            throw Error($"data_{block.Name}: duplicate column _{name}", block.Start);
        Reserve(name);
        block.Columns.Add(name);
    }

    private void Reserve(string value, int overhead = 40)
    {
        _metadataBytes += overhead + value.Length * 2L;
        if (_metadataBytes > 64L * 1024 * 1024)
            throw Error("STAR headers/scalar metadata exceed the 64 MiB indexing budget", 0);
    }

    public void EnsureUnchanged()
    {
        var info = new FileInfo(Path);
        if (info.Length != _length || info.LastWriteTimeUtc != _modified)
            throw new IOException($"STAR input changed during merging: {Path}");
    }

    // The same row array is reused; consumers must copy it if they need to retain it.
    public void ReadRows(Block block, Action<string[]> consume, CancellationToken cancellationToken)
    {
        using var reader = new Tokens(Path, cancellationToken, block.Start, block.StartsAtLineStart);
        var row = new string[block.Columns.Count];
        for (long r = 0; r < block.RowCount; r++)
        {
            long rowBytes = 0;
            for (int c = 0; c < row.Length; c++)
            {
                var token = reader.Read();
                if (token == null || token.Value.IsControl || token.Value.Start >= block.End)
                    throw Error($"data_{block.Name}: input changed or row {r + 1} is incomplete", reader.Position);
                row[c] = token.Value.Raw;
                rowBytes += row[c].Length * 2L;
                if (rowBytes > 64L * 1024 * 1024)
                    throw Error($"data_{block.Name}: a single row exceeds the 64 MiB merge buffer budget", reader.Position);
            }
            consume(row);
        }
    }

    internal delegate ReadOnlySpan<byte> RewriteValue(ReadOnlySpan<byte> raw);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    /// <summary>
    /// Copies token bytes without allocating strings. Operations are indexed by source column;
    /// outputOrder is computed once by the caller. Reordered tables use one reusable row buffer.
    /// </summary>
    public void RewriteRows(Block block, Stream destination, int[] outputOrder, RewriteValue[] operations,
        CancellationToken cancellationToken)
    {
        if (outputOrder.Length != block.Columns.Count || operations.Length != block.Columns.Count)
            throw new ArgumentException("Column operation count does not match the STAR table.");
        bool reorder = outputOrder.Where((column, i) => column != i).Any();
        using var reader = new Tokens(Path, cancellationToken, block.Start, block.StartsAtLineStart);
        var writer = new ByteWriter(destination);
        byte[] row = reorder ? new byte[4096] : null;
        int[] starts = reorder ? new int[outputOrder.Length] : null;
        int[] lengths = reorder ? new int[outputOrder.Length] : null;
        const int maxRowBytes = 64 * 1024 * 1024;
        for (long r = 0; r < block.RowCount; r++)
        {
            int rowLength = 0;
            for (int c = 0; c < operations.Length; c++)
            {
                if (!reader.ReadUtf8() || reader.IsControl || reader.Start >= block.End)
                    throw Error($"data_{block.Name}: input changed or row {r + 1} is incomplete", reader.Position);
                ReadOnlySpan<byte> raw = reader.Raw;
                // Validate UTF-8 without decoding or allocating. Unknown columns must remain
                // valid even though only reference columns are interpreted by the merger.
                StrictUtf8.GetCharCount(raw);
                if (reader.IsBareSemicolon)
                    raw = StrictUtf8.GetBytes(Quote(StrictUtf8.GetString(raw)));
                if (operations[c] != null) raw = operations[c](raw);
                if (raw.Length > maxRowBytes - rowLength)
                    throw Error($"data_{block.Name}: a single row exceeds the 64 MiB merge buffer budget", reader.Position);
                if (reorder)
                {
                    int required = rowLength + raw.Length;
                    if (required > row.Length)
                        Array.Resize(ref row, Math.Min(maxRowBytes, Math.Max(required, row.Length * 2)));
                    starts[c] = rowLength;
                    lengths[c] = raw.Length;
                    raw.CopyTo(row.AsSpan(rowLength));
                }
                else writer.Value(raw);
                rowLength += raw.Length;
            }
            if (reorder)
                foreach (int c in outputOrder) writer.Value(row.AsSpan(starts[c], lengths[c]));
            writer.EndRow();
        }
        writer.Flush();
    }

    public static ReadOnlySpan<byte> Utf8Value(ReadOnlySpan<byte> raw)
    {
        if (raw.Length >= 2 && raw[0] is (byte)'\'' or (byte)'"') return raw[1..^1];
        if (!raw.IsEmpty && raw[0] == ';')
        {
            raw = raw[1..^1];
            while (!raw.IsEmpty && raw[^1] is (byte)'\r' or (byte)'\n') raw = raw[..^1];
        }
        return raw;
    }

    private sealed class ByteWriter(Stream destination)
    {
        private readonly byte[] _buffer = new byte[65536];
        private int _length;
        private bool _lineStart = true;

        public void Value(ReadOnlySpan<byte> raw)
        {
            if (!raw.IsEmpty && raw[0] == ';')
            {
                if (!_lineStart) Byte((byte)'\n');
                Write(raw);
                Byte((byte)'\n');
                _lineStart = true;
            }
            else
            {
                if (!_lineStart) Byte((byte)' ');
                Write(raw);
                _lineStart = false;
            }
        }

        public void EndRow()
        {
            if (!_lineStart) Byte((byte)'\n');
            _lineStart = true;
        }

        private void Byte(byte value)
        {
            if (_length == _buffer.Length) Flush();
            _buffer[_length++] = value;
        }

        private void Write(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length <= _buffer.Length - _length)
            {
                bytes.CopyTo(_buffer.AsSpan(_length));
                _length += bytes.Length;
                return;
            }
            Flush();
            if (bytes.Length >= _buffer.Length) destination.Write(bytes);
            else
            {
                bytes.CopyTo(_buffer);
                _length = bytes.Length;
            }
        }

        public void Flush()
        {
            if (_length > 0) destination.Write(_buffer.AsSpan(0, _length));
            _length = 0;
        }
    }

    private InvalidDataException Error(string message, long position) =>
        new($"{Path}, byte {position}: {message}");

    public static string Value(string raw)
    {
        if (raw.Length >= 2 && raw[0] is '\'' or '"')
            return raw[1..^1];
        if (raw.StartsWith(';'))
            return raw[1..^1].TrimEnd('\r', '\n');
        return raw;
    }

    public static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(char.IsWhiteSpace) && value[0] is not ('#' or '_' or ';' or '\'' or '"')
            && !value.StartsWith("data_", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("loop_", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("stop_", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("global_", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("save_", StringComparison.OrdinalIgnoreCase))
            return value;
        if (!value.Contains('\'') && !value.Contains('\n') && !value.Contains('\r'))
            return "'" + value + "'";
        if (!value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
            return "\"" + value + "\"";
        if (value.Split('\n').Any(line => line.StartsWith(';')))
            throw new InvalidDataException("Cannot represent a STAR value containing a semicolon at the start of a line.");
        return ";" + value + "\n;";
    }

    public static void WriteRow(TextWriter writer, IEnumerable<string> values)
    {
        bool start = true;
        foreach (var raw in values)
        {
            if (raw.StartsWith(';'))
            {
                if (!start) writer.WriteLine();
                writer.WriteLine(raw);
                start = true;
            }
            else
            {
                if (!start) writer.Write(' ');
                writer.Write(raw);
                start = false;
            }
        }
        if (!start) writer.WriteLine();
    }

    public static void WriteHeader(TextWriter writer, string name, IEnumerable<string> columns)
    {
        writer.WriteLine($"\ndata_{name}\n\nloop_");
        int i = 0;
        foreach (var column in columns)
            writer.WriteLine($"_{column} #{++i}");
    }

    private readonly record struct Token(string Raw, long Start, bool IsControl, bool AtLineStart)
    {
        public string Value => StreamingStar.Value(Raw);
        public bool IsLoop => IsControl && Raw.Equals("loop_", StringComparison.OrdinalIgnoreCase);
        public bool IsStop => IsControl && Raw.Equals("stop_", StringComparison.OrdinalIgnoreCase);
        public bool IsTag => IsControl && Raw.StartsWith('_');
        public bool IsData => IsControl && Raw.StartsWith("data_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// UTF-8 lexer. Ordinary tokens are slices of the read buffer; only tokens crossing
    /// a buffer boundary need copying. Delimiter searches operate on spans in chunks.
    /// A returned byte span is valid only until the next read.
    /// </summary>
    private sealed class Tokens : IDisposable
    {
        private readonly FileStream _stream;
        private readonly CancellationToken _cancellationToken;
        private readonly byte[] _buffer = new byte[65536];
        private byte[] _scratch = new byte[256];
        private int _position, _length, _scratchLength;
        private byte[] _rawBuffer;
        private int _rawStart, _rawLength;
        private bool _lineStart;
        private static readonly SearchValues<byte> Whitespace = SearchValues.Create(" \t\r\n\f"u8);
        private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
        private const int MaxTokenBytes = 16 * 1024 * 1024;

        public long Position { get; private set; }
        public long Start { get; private set; }
        public bool AtLineStart { get; private set; }
        public bool IsControl { get; private set; }
        public bool IsBareSemicolon { get; private set; }
        public ReadOnlySpan<byte> Raw => _rawBuffer.AsSpan(_rawStart, _rawLength);

        public Tokens(string path, CancellationToken cancellationToken, long offset = 0, bool atLineStart = true)
        {
            _lineStart = atLineStart;
            _cancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
            _stream.Position = Position = offset;
            if (offset == 0 && Peek() == 0xef)
            {
                if (Take() != 0xef || Take() != 0xbb || Take() != 0xbf)
                    throw new InvalidDataException($"Invalid UTF-8 BOM in {path}");
                _lineStart = true;
            }
        }

        private int Peek()
        {
            if (_position < _length) return _buffer[_position];
            _cancellationToken.ThrowIfCancellationRequested();
            _length = _stream.Read(_buffer);
            _position = 0;
            return _length == 0 ? -1 : _buffer[0];
        }

        private int Take()
        {
            int value = Peek();
            if (value >= 0) Advance(1);
            return value;
        }

        private void Advance(int count)
        {
            if (count == 0) return;
            _position += count;
            Position += count;
            _lineStart = _buffer[_position - 1] is (byte)'\r' or (byte)'\n';
        }

        private static bool Space(int b) => b is ' ' or '\t' or '\r' or '\n' or '\f';

        private void Append(ReadOnlySpan<byte> bytes, bool retain)
        {
            if (!retain) return;
            if (bytes.Length > MaxTokenBytes - _scratchLength)
                throw new InvalidDataException($"{_stream.Name}: STAR token exceeds {MaxTokenBytes} bytes at byte {Start}.");
            int required = _scratchLength + bytes.Length;
            if (required > _scratch.Length)
                Array.Resize(ref _scratch, Math.Min(MaxTokenBytes, Math.Max(required, _scratch.Length * 2)));
            bytes.CopyTo(_scratch.AsSpan(_scratchLength));
            _scratchLength = required;
        }

        public Token? Read(bool capture = true)
        {
            if (!ReadUtf8(capture)) return null;
            string raw = capture || IsControl ? Utf8.GetString(Raw) : "";
            if (IsBareSemicolon && raw.Length > 0) raw = Quote(raw);
            return new Token(raw, Start, IsControl, AtLineStart);
        }

        public bool ReadUtf8(bool capture = true)
        {
            int first;
            while ((first = Peek()) >= 0)
            {
                var available = _buffer.AsSpan(_position, _length - _position);
                if (Space(first))
                {
                    int end = available.IndexOfAnyExcept(Whitespace);
                    Advance(end < 0 ? available.Length : end);
                }
                else if (first == '#')
                {
                    // The comment may cross any number of I/O buffers.
                    do
                    {
                        available = _buffer.AsSpan(_position, _length - _position);
                        int end = available.IndexOfAny((byte)'\r', (byte)'\n');
                        Advance(end < 0 ? available.Length : end);
                        if (end >= 0) break;
                    } while (Peek() >= 0);
                }
                else break;
            }
            if (first < 0) return false;
            Start = Position;
            AtLineStart = _lineStart;
            bool text = first == ';' && _lineStart;
            bool quoted = first is '\'' or '"';
            bool candidate = !text && !quoted && first is '_' or 'd' or 'D' or 'l' or 'L' or 's' or 'S' or 'g' or 'G';
            bool retain = capture || candidate;
            IsBareSemicolon = first == ';' && !text;
            _scratchLength = 0;
            _rawBuffer = _buffer;
            _rawStart = _position;
            _rawLength = 0;

            if (!text && !quoted)
            {
                // Common path: a complete unquoted value already in the I/O buffer.
                var available = _buffer.AsSpan(_position, _length - _position);
                int end = available.IndexOfAny(Whitespace);
                if (end >= 0)
                {
                    _rawLength = end;
                    Advance(end);
                }
                else
                {
                    do
                    {
                        available = _buffer.AsSpan(_position, _length - _position);
                        end = available.IndexOfAny(Whitespace);
                        int count = end < 0 ? available.Length : end;
                        Append(available[..count], retain);
                        Advance(count);
                        if (end >= 0) break;
                    } while (Peek() >= 0);
                    UseScratch();
                }
            }
            else
            {
                // Quoted/text values may contain whitespace and cross buffer boundaries.
                // Keep their original delimiters; searches skip whole runs between candidates.
                Append(_buffer.AsSpan(_position, 1), retain);
                Advance(1);
                while (true)
                {
                    if (Peek() < 0)
                        throw new InvalidDataException($"{_stream.Name}: Unterminated STAR {(text ? "text field" : "quote")} at byte {Start}.");
                    if (text && _lineStart && Peek() == ';')
                    {
                        Append(_buffer.AsSpan(_position, 1), retain);
                        Advance(1);
                        break;
                    }
                    var available = _buffer.AsSpan(_position, _length - _position);
                    int end = text ? available.IndexOfAny((byte)'\r', (byte)'\n') : available.IndexOf((byte)first);
                    int count = end < 0 ? available.Length : end + 1;
                    Append(available[..count], retain);
                    Advance(count);
                    if (!text && end >= 0 && (Peek() < 0 || Space(Peek()) || Peek() == '#')) break;
                }
                UseScratch();
            }
            IsControl = candidate && IsStructural(Raw);
            return true;
        }

        private void UseScratch()
        {
            _rawBuffer = _scratch;
            _rawStart = 0;
            _rawLength = _scratchLength;
        }

        private static bool IsStructural(ReadOnlySpan<byte> raw) => raw.Length > 0 &&
            (raw[0] == '_' || StartsWithIgnoreCase(raw, "data_"u8) || StartsWithIgnoreCase(raw, "save_"u8)
                || raw.Length == 5 && (StartsWithIgnoreCase(raw, "loop_"u8) || StartsWithIgnoreCase(raw, "stop_"u8))
                || raw.Length == 7 && StartsWithIgnoreCase(raw, "global_"u8));

        private static bool StartsWithIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> prefix)
        {
            if (value.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
            {
                byte b = value[i];
                if (b >= 'A' && b <= 'Z') b += 32;
                if (b != prefix[i]) return false;
            }
            return true;
        }

        public void Dispose() => _stream.Dispose();
    }
}
