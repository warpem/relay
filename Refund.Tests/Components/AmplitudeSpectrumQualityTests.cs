using System.Text.Json;
using Refund.Components.FourierSpace;
using Warp;
using Warp.Tools;

namespace Refund.Tests.Components;

public class AmplitudeSpectrumQualityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-ctf-quality-" + Guid.NewGuid().ToString("N"));

    public AmplitudeSpectrumQualityTests() => Directory.CreateDirectory(_root);

    private static float2[] Spectrum() => Enumerable.Range(0, 32).Select(i => new float2(i / 64f, 1 + i % 3)).ToArray();
    private static float2[] Quality(float correlation) => Enumerable.Range(0, 32)
        .Select(i => new float2(i / 64f, i == 0 ? float.NaN : correlation)).ToArray();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovieView_UsesSavedQualityAndSupportsLegacyMetadata(bool hasQuality)
    {
        string path = Path.Combine(_root, "movie.mrc");
        var movie = new Movie(path)
        {
            PS1D = Spectrum(),
            CTF = new CTF { PixelSize = 1 },
            OptionsCTF = new ProcessingOptionsMovieCTF { PixelSize = 1, Window = 512 },
            CTFSpecimenThicknessAngstrom = hasQuality ? 1456.75M : 0,
            CTFQuality = hasQuality ? Quality(-0.3f) : null
        };
        movie.SaveMeta();

        var result = AmplitudeSpectrumViewer.GetWarpSeries(path, 8, 2);
        Assert.Equal(hasQuality ? 1456.75M : (decimal?)null, result.SpecimenThicknessAngstrom);
        if (hasQuality)
        {
            Assert.Null(result.QualityValues[0]);
            Assert.Equal(-0.3f, result.QualityValues[1]);
            // Unsupported bins must cross JS interop as null, never NaN or a fake zero correlation.
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new ChartConfig { QualityValues = result.QualityValues }));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("QualityValues")[0].ValueKind);
        }
        else Assert.Empty(result.QualityValues);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TiltView_UsesSelectedTiltQualityInsteadOfGlobalCurve(bool hasSecondTiltQuality)
    {
        string path = Path.Combine(_root, "series.tomostar");
        File.WriteAllText(path, "data_\nloop_\n_wrpMovieName #1\n_wrpAngleTilt #2\n_wrpDose #3\na.mrc -30 1\nb.mrc 30 2\n");
        var series = new TiltSeries(path)
        {
            CTF = new CTF { PixelSize = 1 },
            OptionsCTF = new ProcessingOptionsMovieCTF { PixelSize = 1, Window = 512 },
            CTFSpecimenThicknessAngstrom = 2468.5M,
            CTFQuality = Quality(0.95f)
        };
        for (int tilt = 0; tilt < 2; tilt++)
        {
            series.TiltPS1D.Add(Spectrum());
            series.TiltSimulatedScale.Add(new Cubic1D([new float2(0, 1), new float2(0.5f, 1)]));
        }
        series.TiltCTFQuality.Add(Quality(0.7f));
        if (hasSecondTiltQuality) series.TiltCTFQuality.Add(Quality(-0.4f));
        series.SaveMeta();
        var loaded = new TiltSeries(path);

        Assert.Equal(0.7f, AmplitudeSpectrumViewer.GetTiltWarpSeries(loaded, 0, 8, 2).QualityValues[1]);
        var result = AmplitudeSpectrumViewer.GetTiltWarpSeries(loaded, 1, 8, 2);
        Assert.Equal(2468.5M, result.SpecimenThicknessAngstrom);
        if (hasSecondTiltQuality)
        {
            Assert.Null(result.QualityValues[0]);
            Assert.Equal(-0.4f, result.QualityValues[1]);
        }
        else Assert.Empty(result.QualityValues);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
