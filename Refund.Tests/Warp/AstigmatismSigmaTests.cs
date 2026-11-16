using Refund.Services.Warp;
using Xunit;

namespace Refund.Tests.Warp;

/// <summary>Astigmatism is judged against the movie's neighbours in acquisition order, as a 2D distance in σ.</summary>
public class AstigmatismSigmaTests
{
    private static (double X, double Y)[] Noise(int count, double cx, double cy, double spread, int seed)
    {
        var random = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => (cx + spread * Gaussian(random), cy + spread * Gaussian(random))).ToArray();
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    [Fact]
    public void MatchesBruteForceWindows()
    {
        var vectors = Noise(357, .05, -.02, .01, 7);
        var (sigma, neighborhoods) = WarpSession.ComputeAstigmatismSigma(vectors, 100);
        for (int k = 0; k < vectors.Length; k++)
        {
            int lo = Math.Clamp(k - 50, 0, vectors.Length - 101);
            var others = Enumerable.Range(lo, 101).Where(i => i != k).Select(i => vectors[i]).ToArray();
            double mx = others.Average(v => v.X), my = others.Average(v => v.Y);
            double sxx = others.Average(v => (v.X - mx) * (v.X - mx)), syy = others.Average(v => (v.Y - my) * (v.Y - my));
            double sxy = others.Average(v => (v.X - mx) * (v.Y - my));
            double floor = 1e-4 * (sxx + syy) + 1e-12;
            sxx += floor; syy += floor;
            double dx = vectors[k].X - mx, dy = vectors[k].Y - my;
            double expected = Math.Sqrt((syy * dx * dx - 2 * sxy * dx * dy + sxx * dy * dy) / (sxx * syy - sxy * sxy));
            Assert.Equal(expected, sigma[k], 1e-6);
            Assert.Equal(100, neighborhoods[k].Count);
            Assert.Equal(mx, neighborhoods[k].MeanX, 1e-12);
        }
    }

    [Fact]
    public void FlagsOutliersButNotTypicalMovies()
    {
        var vectors = Noise(300, .05, 0, .01, 3);
        vectors[150] = (.05, .12); // 12 σ away
        var (sigma, _) = WarpSession.ComputeAstigmatismSigma(vectors, 100);
        Assert.True(sigma[150] > 8, $"outlier at {sigma[150]:F1} σ");
        Assert.True(sigma.Where((_, i) => i != 150).Count(s => s > 3) <= 6, "about 1% of typical movies exceed 3 σ");
    }

    [Fact]
    public void FollowsDriftThatAGlobalMeanWouldMiss()
    {
        // Two halves of a collection with different typical astigmatism. A movie in the first half that
        // looks like the second half is typical overall, but unusual among its neighbours.
        var vectors = Noise(200, .02, 0, .004, 11).Concat(Noise(200, .08, 0, .004, 12)).ToArray();
        vectors[60] = (.08, 0);
        var (sigma, _) = WarpSession.ComputeAstigmatismSigma(vectors, 100);
        Assert.True(sigma[60] > 10, $"locally unusual movie at {sigma[60]:F1} σ");
        Assert.True(sigma[300] < 4, $"typical second-half movie at {sigma[300]:F1} σ");
    }

    [Fact]
    public void TooFewNeighboursPass()
    {
        var (sigma, neighborhoods) = WarpSession.ComputeAstigmatismSigma(Noise(WarpSession.MinAstigmatismNeighbors, 0, 0, .01, 1), 100);
        Assert.All(sigma, s => Assert.True(double.IsNaN(s)));
        Assert.All(neighborhoods, n => Assert.Equal(0, n.Count));
    }

    [Fact]
    public void IdenticalNeighboursStayFinite()
    {
        var vectors = Enumerable.Repeat((.05, .01), 40).ToArray();
        var (sigma, _) = WarpSession.ComputeAstigmatismSigma(vectors, 100);
        Assert.All(sigma, s => Assert.True(double.IsFinite(s) && s < 1e-3));
    }
}
