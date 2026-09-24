namespace Refund.Utils;

/// <summary>
/// Counts particle rows without loading STAR columns or retaining particle records in memory.
/// </summary>
public static class StarItemCounter
{
    public static long? CountParticles(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 65536, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        return CountParticles(reader, cancellationToken);
    }

    /// <summary>
    /// Prefers data_particles over legacy coordinate/image loops and excludes data_optics.
    /// Returns null when no particle table can be identified or its rows are incomplete.
    /// The caller retains ownership of the reader.
    /// </summary>
    public static long? CountParticles(TextReader reader, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = new TokenReader(reader, cancellationToken);
        TokenKind block = TokenKind.DataOther;
        bool inLoop = false, inHeader = false, particleColumns = false;
        long values = 0;
        int columns = 0;
        long namedCount = 0, legacyCount = 0;
        bool hasNamed = false, hasLegacy = false, validNamed = true, validLegacy = true;

        void FinishLoop()
        {
            if (!inLoop || columns == 0)
                return;

            if (block == TokenKind.DataParticles)
            {
                hasNamed = true;
                validNamed &= values % columns == 0;
                namedCount = checked(namedCount + values / columns);
            }
            else if (block != TokenKind.DataOptics && particleColumns)
            {
                hasLegacy = true;
                validLegacy &= values % columns == 0;
                legacyCount = checked(legacyCount + values / columns);
            }
        }

        try
        {
            while (tokens.TryRead(out var token))
            {
                if (inLoop && inHeader && token is TokenKind.Tag or TokenKind.ParticleTag)
                {
                    columns++;
                    particleColumns |= token == TokenKind.ParticleTag;
                    continue;
                }

                if (inLoop && token == TokenKind.Value)
                {
                    inHeader = false;
                    values++;
                    continue;
                }

                FinishLoop();
                inLoop = false;
                if (token == TokenKind.Loop)
                {
                    inLoop = inHeader = true;
                    columns = 0;
                    values = 0;
                    particleColumns = false;
                }
                else if (token is TokenKind.DataParticles or TokenKind.DataOptics or TokenKind.DataOther)
                {
                    block = token;
                }
            }

            FinishLoop();
            return hasNamed ? validNamed ? namedCount : null
                : hasLegacy && validLegacy ? legacyCount : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private enum TokenKind { Value, Tag, ParticleTag, DataParticles, DataOptics, DataOther, Loop, Stop }

    /// <summary>
    /// Reads fixed-size buffers and retains only a short token prefix for recognizing STAR tags.
    /// Quoted values and semicolon-delimited text fields each count as one value, even across lines.
    /// </summary>
    private sealed class TokenReader(TextReader reader, CancellationToken cancellationToken)
    {
        private readonly char[] _buffer = new char[65536];
        private readonly char[] _prefix = new char[128];
        private int _position, _length;
        private bool _atLineStart = true;

        private int Peek()
        {
            if (_position < _length)
                return _buffer[_position];

            cancellationToken.ThrowIfCancellationRequested();
            _length = reader.Read(_buffer, 0, _buffer.Length);
            _position = 0;
            return _length == 0 ? -1 : _buffer[0];
        }

        private int Read()
        {
            int value = Peek();
            if (value >= 0)
            {
                _position++;
                _atLineStart = value is '\n' or '\r';
            }
            return value;
        }

        public bool TryRead(out TokenKind kind)
        {
            kind = TokenKind.Value;
            int first;
            while ((first = Peek()) >= 0)
            {
                if (char.IsWhiteSpace((char)first))
                    Read();
                else if (first == '#')
                {
                    while (Peek() is not (-1 or '\n' or '\r'))
                        Read();
                }
                else
                    break;
            }

            if (first < 0)
                return false;

            if (first == ';' && _atLineStart)
            {
                Read();
                while (Peek() >= 0)
                {
                    bool closing = _atLineStart && Peek() == ';';
                    Read();
                    if (closing)
                        return true;
                }
                throw new InvalidDataException("Unterminated STAR text field.");
            }

            if (first is '\'' or '"')
            {
                Read();
                while (Peek() >= 0)
                {
                    if (Read() == first && (Peek() < 0 || char.IsWhiteSpace((char)Peek()) || Peek() == '#'))
                        return true;
                }
                throw new InvalidDataException("Unterminated STAR quoted value.");
            }

            int prefixLength = 0;
            bool truncated = false;
            while (Peek() >= 0 && !char.IsWhiteSpace((char)Peek()))
            {
                var value = (char)Read();
                if (prefixLength < _prefix.Length)
                    _prefix[prefixLength++] = value;
                else
                    truncated = true;
            }

            var text = _prefix.AsSpan(0, prefixLength);
            if (text[0] == '_')
            {
                kind = !truncated && IsParticleTag(text) ? TokenKind.ParticleTag : TokenKind.Tag;
            }
            else if (text.StartsWith("data_", StringComparison.OrdinalIgnoreCase))
            {
                kind = text.Equals("data_particles", StringComparison.OrdinalIgnoreCase) ? TokenKind.DataParticles
                    : text.Equals("data_optics", StringComparison.OrdinalIgnoreCase) ? TokenKind.DataOptics
                    : TokenKind.DataOther;
            }
            else if (!truncated && text.Equals("loop_", StringComparison.OrdinalIgnoreCase))
                kind = TokenKind.Loop;
            else if (!truncated && (text.Equals("stop_", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("save_", StringComparison.OrdinalIgnoreCase)
                || text.Equals("global_", StringComparison.OrdinalIgnoreCase)))
                kind = TokenKind.Stop;

            return true;
        }

        private static bool IsParticleTag(ReadOnlySpan<char> tag) =>
            tag.Equals("_rlnImageName", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("_rlnParticleName", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("_rlnTomoParticleId", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("_rlnCoordinate", StringComparison.OrdinalIgnoreCase)
            || tag.StartsWith("_rlnCenteredCoordinate", StringComparison.OrdinalIgnoreCase);
    }
}
