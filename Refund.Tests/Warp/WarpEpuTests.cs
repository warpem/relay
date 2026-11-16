using Refund.Services.Warp;

namespace Refund.Tests.Warp;

public sealed class WarpEpuTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "warp-epu-" + Guid.NewGuid().ToString("N"));

    /// <summary>The shape of an EPU exposure XML around its beam shift (namespaces as EPU writes them).</summary>
    internal static string Xml(double x, double y) =>
        "<MicroscopeImage xmlns=\"http://schemas.datacontract.org/2004/07/Fei.SharedObjects\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        "<microscopeData><optics><BeamDiameter>6.5E-07</BeamDiameter>" +
        $"<BeamShift xmlns:a=\"http://schemas.datacontract.org/2004/07/Fei.Types\"><a:_x>{x.ToString(System.Globalization.CultureInfo.InvariantCulture)}</a:_x><a:_y>{y.ToString(System.Globalization.CultureInfo.InvariantCulture)}</a:_y></BeamShift>" +
        "<BeamTilt xmlns:a=\"http://schemas.datacontract.org/2004/07/Fei.Types\"><a:_x>9</a:_x><a:_y>9</a:_y></BeamTilt></optics></microscopeData></MicroscopeImage>";

    [Theory]
    [InlineData("FoilHole_1_Data_2_3_20250101_120000_Fractions.tiff")]
    [InlineData("FoilHole_1_Data_2_3_20250101_120000_fractions.tiff")]
    [InlineData("FoilHole_1_Data_2_3_20250101_120000_Fractions.mrc")]
    [InlineData("FoilHole_1_Data_2_3_20250101_120000_EER.eer")]
    public void MoviesShareTheirXmlsName(string movie)
    {
        Assert.Equal("FoilHole_1_Data_2_3_20250101_120000", WarpEpu.Stem(movie));
        Assert.Equal("foilhole_1_data_2_3_20250101_120000", WarpEpu.Key(movie));
        // Copies often add a prefix; the exposure's core still matches.
        Assert.Equal(WarpEpu.Key(movie), WarpEpu.Key("123456789012345678901_" + movie));
    }

    [Fact]
    public void BeamShiftIsReadBesideTheMovieOrFromAMetadataFolder()
    {
        string session = Path.Combine(_root, "epu session"), data = Path.Combine(session, "Images-Disc1", "GridSquare_7", "Data");
        Directory.CreateDirectory(data);
        File.WriteAllText(Path.Combine(data, "FoilHole_1_Data_2_3_20250101_120000.xml"), Xml(.049, -.113));
        // Fraction metadata carries no beam shift, and is never taken for the exposure's XML.
        File.WriteAllText(Path.Combine(data, "FoilHole_1_Data_2_4_20250101_120100_Fractions.xml"), Xml(5, 5));
        string beside = Path.Combine(data, "FoilHole_1_Data_2_3_20250101_120000_Fractions.tiff");
        var shift = WarpEpu.Read(WarpEpu.Beside(beside)!)!;
        Assert.Equal((.049, -.113, session), (shift.X, shift.Y, shift.Session));

        // Movies stored elsewhere are matched in the metadata folder, searched recursively.
        string movies = Path.Combine(_root, "movies");
        Directory.CreateDirectory(movies);
        string apart = Path.Combine(movies, "FoilHole_1_Data_2_3_20250101_120000_EER.eer");
        Assert.Null(WarpEpu.Beside(apart));
        Assert.Null(new WarpEpu.Finder("").Find(apart));
        var finder = new WarpEpu.Finder(session);
        Assert.Equal(WarpEpu.Beside(beside), finder.Find(apart));
        Assert.Null(finder.Find(Path.Combine(movies, "FoilHole_1_Data_2_4_20250101_120100_EER.eer")));

        File.WriteAllText(Path.Combine(data, "broken.xml"), "<MicroscopeImage>");
        Assert.Null(WarpEpu.Read(Path.Combine(data, "broken.xml")));
        Assert.Equal("", WarpEpu.SessionOf(Path.Combine(movies, "a.xml")));
    }

    [Fact]
    public void GroupsFollowBeamShiftPerSessionAndNeverChangeAsMoviesArrive()
    {
        // Two holes, two shots each (0.025 apart), a later return to the first hole, one exposure without metadata,
        // and the same position in another EPU session.
        WarpBeamShift[] shifts =
        [
            new(0, 0, "a"), new(.025, 0, "a"), new(.1, 0, "a"), new(.125, .002, "a"),
            null, new(.004, .001, "a"), new(0, 0, "b")
        ];
        Assert.Equal([1, 1, 2, 2, 0, 1, 3], WarpEpu.Group(shifts, .05));
        // A smaller radius separates the shots.
        Assert.Equal([1, 2, 3, 4, 0, 1, 5], WarpEpu.Group(shifts, .01));
        // Groups given to earlier exposures do not depend on later ones.
        for (int n = 1; n <= shifts.Length; n++)
            Assert.Equal(WarpEpu.Group(shifts, .05)[..n], WarpEpu.Group(shifts[..n], .05));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
