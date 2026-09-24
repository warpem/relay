using Refund.Utils;

namespace Refund.Tests.Utils;

public class StarItemCounterTests
{
    [Fact]
    public void PrefersParticlesTableAndExcludesOpticsAndOtherLoops()
    {
        const string star = """
            data_optics
            loop_
            _rlnOpticsGroup #1
            _rlnImagePixelSize #2
            1 1.5
            2 1.5
            data_legacy
            loop_
            _rlnCoordinateX
            1 2 3
            data_particles
            loop_
            _rlnImageName #1
            _rlnOpticsGroup #2
            1@particles.mrcs 1
            2@particles.mrcs 2
            data_model_general
            _rlnNrClasses 20
            loop_
            _rlnReferenceImage
            class001.mrc
            """;

        Assert.Equal(2, Count(star));
    }

    [Fact]
    public void CountsLegacyRowsAcrossLineBreaksAndComments()
    {
        const string star = """
            # coordinates exported by Warp
            data_
            loop_
            _rlnCoordinateX #1
            _rlnCoordinateY #2
            _rlnMicrographName #3
            1
            2 'micrograph with spaces.mrc' # a comment
            3 4 "another micrograph.mrc" 5 6 micrograph3.mrc
            """;

        Assert.Equal(3, Count(star));
    }

    [Fact]
    public void QuotedKeywordsAndMultilineTextAreSingleValues()
    {
        const string star = """
            data_particles
            loop_
            _rlnImageName
            _comment
            'loop_'
            ;a multiline
            value containing data_optics and # comments
            ;
            "data_particles" '_rlnCoordinateX'
            """;

        Assert.Equal(2, Count(star));
    }

    [Theory]
    [InlineData("data_particles\nloop_\n_rlnImageName\n", 0L)]
    [InlineData("data_\nloop_\n_rlnCoordinateX\n_rlnCoordinateY\n", 0L)]
    [InlineData("data_optics\nloop_\n_rlnCoordinateX\n1\n", null)]
    [InlineData("data_other\nloop_\n_rlnOpticsGroup\n1\n", null)]
    [InlineData("data_particles\nloop_\n_rlnImageName\n_rlnOpticsGroup\n1@p 1\n2@p", null)]
    [InlineData("data_particles\nloop_\n_rlnImageName\n'unterminated", null)]
    [InlineData("data_particles\nloop_\n_rlnImageName\n;unterminated", null)]
    public void DistinguishesKnownEmptyFromUnidentifiedOrMalformedTables(string star, long? expected)
    {
        Assert.Equal(expected, Count(star));
    }

    [Fact]
    public void StreamsLargeTablesWithBoundedReads()
    {
        using var reader = new RepeatedRowsReader(500_000);

        Assert.Equal(500_000, StarItemCounter.CountParticles(reader));
        Assert.True(reader.ReadCalls > 1);
        Assert.InRange(reader.LargestRead, 1, 65536);
    }

    [Fact]
    public void CancelsWhileReadingLargeTables()
    {
        using var cancellation = new CancellationTokenSource();
        using var reader = new RepeatedRowsReader(500_000, () => cancellation.Cancel());

        Assert.Throws<OperationCanceledException>(() => StarItemCounter.CountParticles(reader, cancellation.Token));
        Assert.Equal(1, reader.ReadCalls);
    }

    private static long? Count(string star)
    {
        using var reader = new StringReader(star);
        return StarItemCounter.CountParticles(reader);
    }

    // Generates the input on demand and rejects ReadToEnd/ReadLine, so the test does not itself
    // materialize a large STAR table and would catch a regression to whole-file parsing.
    private sealed class RepeatedRowsReader(long rowCount, Action? onFirstRead = null) : TextReader
    {
        private const string Header = "data_particles\nloop_\n_rlnImageName\n_rlnOpticsGroup\n";
        private const string Row = "1@particles.mrcs 1\n";
        private long _position;
        public int ReadCalls { get; private set; }
        public int LargestRead { get; private set; }

        public override int Read(char[] buffer, int index, int count)
        {
            ReadCalls++;
            LargestRead = Math.Max(LargestRead, count);
            int length = (int)Math.Min(count, Header.Length + rowCount * Row.Length - _position);
            for (int i = 0; i < length; i++, _position++)
                buffer[index + i] = _position < Header.Length
                    ? Header[(int)_position]
                    : Row[(int)((_position - Header.Length) % Row.Length)];
            if (ReadCalls == 1)
                onFirstRead?.Invoke();
            return length;
        }

        public override string ReadToEnd() => throw new NotSupportedException();
        public override string ReadLine() => throw new NotSupportedException();
    }
}
