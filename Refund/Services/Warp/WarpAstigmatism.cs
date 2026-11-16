namespace Refund.Services.Warp;

/// <summary>Mean and covariance of the astigmatism vectors of one movie's neighbours.</summary>
public readonly record struct WarpAstigmatismNeighborhood(double MeanX, double MeanY, double Sxx, double Syy, double Sxy, int Count);

public sealed partial class WarpSession
{
    /// <summary>Neighbours, in acquisition order, that define each movie's typical astigmatism.</summary>
    public const int AstigmatismWindow = 100;
    /// <summary>Below this many neighbours the spread is not trusted and the movie passes.</summary>
    public const int MinAstigmatismNeighbors = 10;
    private double[] _astigmatismSigma = [];
    private WarpAstigmatismNeighborhood[] _astigmatismNeighborhoods = [];

    /// <summary>Astigmatism as desktop Warp plots it, Δ·(cos 2θ, sin 2θ), so 0° and 180° coincide.</summary>
    public static (double X, double Y) AstigmatismVector(WarpMovie m) =>
        (m.Astigmatism * Math.Cos(m.AstigmatismAngle * Math.PI / 90), m.Astigmatism * Math.Sin(m.AstigmatismAngle * Math.PI / 90));

    public static bool HasAstigmatism(WarpMovie m) =>
        !m.Failed && m.Done.HasFlag(WarpStage.Ctf) && double.IsFinite(m.Astigmatism) && double.IsFinite(m.AstigmatismAngle);

    /// <summary>
    /// How unusual a movie's astigmatism is: its Mahalanobis distance, in standard deviations, from the mean
    /// of its <see cref="AstigmatismWindow"/> nearest neighbours in acquisition order. A local mean follows
    /// slow drift during collection. NaN while too few neighbours exist.
    /// </summary>
    public double AstigmatismSigma(WarpMovie m)
    {
        var sigma = _astigmatismSigma;
        return m.Index >= 0 && m.Index < sigma.Length ? sigma[m.Index] : double.NaN;
    }

    public WarpAstigmatismNeighborhood? AstigmatismNeighborhood(WarpMovie m)
    {
        var neighborhoods = _astigmatismNeighborhoods;
        return m.Index >= 0 && m.Index < neighborhoods.Length && neighborhoods[m.Index].Count > 0 ? neighborhoods[m.Index] : null;
    }

    private void RefreshAstigmatism()
    {
        var members = _movies.Where(HasAstigmatism).ToArray();
        var (memberSigma, memberNeighborhoods) = ComputeAstigmatismSigma(members.Select(AstigmatismVector).ToArray(), AstigmatismWindow);
        var sigma = new double[_movies.Count];
        Array.Fill(sigma, double.NaN);
        var neighborhoods = new WarpAstigmatismNeighborhood[_movies.Count];
        for (int k = 0; k < members.Length; k++)
        {
            sigma[members[k].Index] = memberSigma[k];
            neighborhoods[members[k].Index] = memberNeighborhoods[k];
        }
        // Readers outside the session lock see either the old or the new arrays, never a mix.
        _astigmatismSigma = sigma;
        _astigmatismNeighborhoods = neighborhoods;
    }

    /// <summary>
    /// Leave-one-out windowed statistics for vectors in acquisition order. Each window holds the
    /// <paramref name="window"/> nearest neighbours (fewer at the ends of a short series); prefix sums make
    /// each window O(1), so a whole session refreshes in one linear pass.
    /// </summary>
    internal static (double[] Sigma, WarpAstigmatismNeighborhood[] Neighborhoods) ComputeAstigmatismSigma(
        IReadOnlyList<(double X, double Y)> vectors, int window)
    {
        int n = vectors.Count;
        var sigma = new double[n];
        Array.Fill(sigma, double.NaN);
        var neighborhoods = new WarpAstigmatismNeighborhood[n];
        var sums = new (double X, double Y, double XX, double YY, double XY)[n + 1];
        for (int k = 0; k < n; k++)
        {
            var (x, y) = vectors[k];
            var p = sums[k];
            sums[k + 1] = (p.X + x, p.Y + y, p.XX + x * x, p.YY + y * y, p.XY + x * y);
        }
        int size = Math.Min(n, window + 1); // the window includes the movie itself, which is then removed
        int count = size - 1;
        if (count < MinAstigmatismNeighbors)
            return (sigma, neighborhoods);
        for (int k = 0; k < n; k++)
        {
            int lo = Math.Clamp(k - size / 2, 0, n - size), hi = lo + size;
            var (x, y) = vectors[k];
            var (a, b) = (sums[hi], sums[lo]);
            double mx = (a.X - b.X - x) / count, my = (a.Y - b.Y - y) / count;
            double sxx = Math.Max(0, (a.XX - b.XX - x * x) / count - mx * mx);
            double syy = Math.Max(0, (a.YY - b.YY - y * y) / count - my * my);
            double sxy = (a.XY - b.XY - x * y) / count - mx * my;
            // A small floor keeps identical neighbours from making the covariance singular.
            double floor = 1e-4 * (sxx + syy) + 1e-12;
            sxx += floor;
            syy += floor;
            double det = sxx * syy - sxy * sxy, dx = x - mx, dy = y - my;
            sigma[k] = Math.Sqrt(Math.Max(0, (syy * dx * dx - 2 * sxy * dx * dy + sxx * dy * dy) / det));
            neighborhoods[k] = new(mx, my, sxx, syy, sxy, count);
        }
        return (sigma, neighborhoods);
    }
}
