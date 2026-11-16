using System.Globalization;
using System.Runtime.InteropServices;
using SkiaSharp;
using Warp;
using Warp.Tools;

namespace ComponentDev.Services;

// Small, deterministic file-backed fixtures exercise the real Relay viewers without a GPU.
// The session has independent metadata for every movie, but reuses these 16 image sets.
public sealed class WarpPreviewFixtures
{
    public const int Count = 16;
    public string DirectoryPath { get; }
    private readonly object _studySync = new();
    private readonly HashSet<string> _readyStudies = [];
    public string Movie(int i) => Path.Combine(DirectoryPath, $"preview-{i % Count:D2}.mrc");
    public string Image(int i, bool enhanced) => Path.Combine(DirectoryPath, $"{(enhanced ? "enhanced" : "raw")}-{i % Count:D2}.mrc");
    public string Thumbnail(int i) => Path.Combine(DirectoryPath, $"thumb-{i % Count:D2}.png");
    public string Class(int i) => Path.Combine(DirectoryPath, $"class-{i % Count:D2}.png");
    public string Particles(int i) => Path.Combine(DirectoryPath, $"picks-{i % Count:D2}.star");

    public WarpPreviewFixtures(string root)
    {
        DirectoryPath = Path.Combine(root, "fixtures-v2");
        Directory.CreateDirectory(DirectoryPath);
        if (File.Exists(Path.Combine(DirectoryPath, "complete"))) return;
        for (int i = 0; i < Count; i++) Generate(i);
        File.WriteAllText(Path.Combine(DirectoryPath, "complete"), "synthetic");
    }

    private void Generate(int variant)
    {
        const int n = 512;
        var signal = CreateSignal(variant, out var coords, out var random);
        var raw = new float[signal.Length];
        var enhanced = new float[signal.Length];
        for (int j = 0; j < raw.Length; j++)
        {
            float noise = (float)(Math.Sqrt(-2 * Math.Log(Math.Max(1e-8, random.NextDouble()))) * Math.Cos(6.28 * random.NextDouble()));
            raw[j] = signal[j] + noise * 17;
            enhanced[j] = signal[j] + noise * 2;
        }
        WriteMrc(Image(variant, false), raw, n, n, 4);
        WriteMrc(Image(variant, true), enhanced, n, n, 4);
        using (var bitmap = new SKBitmap(n, n, SKColorType.Bgra8888, SKAlphaType.Opaque))
        {
            var pixels = new byte[n * n * 4];
            for (int j = 0; j < enhanced.Length; j++)
            {
                byte v = (byte)Math.Clamp((enhanced[j] - 60) * 2.5, 0, 255);
                pixels[j * 4] = pixels[j * 4 + 1] = pixels[j * 4 + 2] = v;
                pixels[j * 4 + 3] = 255;
            }
            Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            using var file = File.Create(Thumbnail(variant));
            encoded.SaveTo(file);
        }
        File.WriteAllText(Particles(variant), "data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n" + string.Join('\n', coords.Select(p => $"{p.X} {p.Y}")) + "\n");

        using (var bitmap = new SKBitmap(96, 96))
        {
            for (int y = 0; y < 96; y++)
            for (int x = 0; x < 96; x++)
            {
                double a = variant * .35, u = (x - 48) * Math.Cos(a) + (y - 48) * Math.Sin(a), v = -(x - 48) * Math.Sin(a) + (y - 48) * Math.Cos(a);
                double density = Math.Exp(-(u * u / 280 + v * v / 170)) + .7 * Math.Exp(-((u - 13) * (u - 13) / 65 + (v - 9) * (v - 9) / 55));
                double ring = .15 * Math.Cos(Math.Sqrt(u * u + v * v) * .45) * Math.Exp(-(u * u + v * v) / 550);
                byte value = (byte)Math.Clamp(24 + 170 * density + 70 * ring + random.NextDouble() * 7, 0, 255);
                bitmap.SetPixel(x, y, new SKColor(value, value, value));
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
            using var file = File.Create(Class(variant)); encoded.SaveTo(file);
        }

        var movie = new Movie(Movie(variant));
        movie.CTF = new CTF { PixelSize = 1.5m, Defocus = 1.1m + variant * .08m, DefocusDelta = .06m, Voltage = 300, Amplitude = .1m };
        movie.OptionsCTF = new ProcessingOptionsMovieCTF { PixelSize = 1.5m, RangeMin = .10m, RangeMax = .75m, Window = 512, Voltage = 300 };
        movie.CTFResolutionEstimate = 3.4m + variant * .08m;
        movie.CTFSpecimenThicknessAngstrom = 450 + variant * 24;
        var ctf = movie.CTF.Get1D(256, true);
        movie.PS1D = Enumerable.Range(0, 256).Select(i => new float2(i / 512f, ctf[i] * ctf[i] + (float)random.NextDouble() * .09f)).ToArray();
        movie.CTFQuality = Enumerable.Range(0, 256).Select(i => new float2(i / 512f, (float)(.92 / (1 + Math.Exp((i - 178 - variant) / 12.0))))).ToArray();
        movie.SaveMeta();
        var spectrum = new float[512 * 256];
        for (int y = 0; y < 256; y++)
        for (int x = 0; x < 512; x++)
        {
            double r = Math.Sqrt((x - 256) * (x - 256) * 1.025 + (255 - y) * (255 - y));
            int k = Math.Clamp((int)r, 0, 255);
            spectrum[y * 512 + x] = ctf[k] * ctf[k] + (float)random.NextDouble() * .5f;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(movie.PowerSpectrumPath)!);
        WriteMrc(movie.PowerSpectrumPath, spectrum, 512, 256, 1.5f);
    }

    /// <summary>
    /// Synthetic comparison evidence for UI review, not simulated microscope measurements or
    /// causal diagnoses. A condition changes visible structure, so automatic normalization in
    /// MicrographViewer does not erase the difference. All pairs retain dimensions and scale.
    /// </summary>
    public string StudyImage(int index, string condition, bool enhanced)
    {
        condition = ValidateCondition(condition);
        int variant = Math.Abs(index % Count);
        if (condition == "normal") return Image(variant, enhanced);
        EnsureStudy(variant, condition);
        return StudyPath(variant, condition, enhanced ? "enhanced.mrc" : "raw.mrc");
    }

    // These are raw-evidence thumbnails, vertically oriented like MicrographViewer.
    public string StudyThumbnail(int index, string condition)
    {
        condition = ValidateCondition(condition);
        int variant = Math.Abs(index % Count);
        EnsureStudy(variant, condition);
        return StudyPath(variant, condition, "thumb.png");
    }

    private static string ValidateCondition(string condition) => condition switch
    {
        "normal" or "lowcontrast" or "drift" => condition,
        _ => throw new ArgumentOutOfRangeException(nameof(condition), "Use normal, lowcontrast, or drift.")
    };

    private string StudyPath(int variant, string condition, string suffix) =>
        Path.Combine(DirectoryPath, "studies-v1", $"{condition}-{variant:D2}-{suffix}");

    private void EnsureStudy(int variant, string condition)
    {
        string key = $"{condition}-{variant:D2}";
        lock (_studySync)
        {
            if (_readyStudies.Contains(key)) return;
            Directory.CreateDirectory(Path.Combine(DirectoryPath, "studies-v1"));
            bool exists = File.Exists(StudyPath(variant, condition, "thumb.png")) &&
                (condition == "normal" || (File.Exists(StudyPath(variant, condition, "raw.mrc")) &&
                                           File.Exists(StudyPath(variant, condition, "enhanced.mrc"))));
            if (!exists) GenerateStudy(variant, condition);
            _readyStudies.Add(key);
        }
    }

    private static float[] CreateSignal(int variant, out List<(int X, int Y)> coords, out Random random)
    {
        const int n = 512;
        random = new Random(1000 + variant);
        var signal = new float[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
            signal[y * n + x] = 135 + 9 * MathF.Sin(x * .013f + variant) * MathF.Cos(y * .009f) + 4 * MathF.Cos(x * .06f + y * .022f);
        coords = new List<(int X, int Y)>();
        // Small asymmetric lobes, occasional aggregates and a broad ice gradient.
        for (int p = 0; p < 190 + variant * 4; p++)
        {
            int cx = random.Next(16, n - 16), cy = random.Next(16, n - 16);
            bool overlaps = false;
            foreach (var c in coords)
                if (Math.Abs(c.X - cx) < 13 && Math.Abs(c.Y - cy) < 13) { overlaps = true; break; }
            if (overlaps) continue;
            coords.Add((cx, cy));
            float angle = (float)random.NextDouble() * 6.28f;
            for (int dy = -12; dy <= 12; dy++)
            for (int dx = -12; dx <= 12; dx++)
            {
                float u = dx * MathF.Cos(angle) + dy * MathF.Sin(angle);
                float v = -dx * MathF.Sin(angle) + dy * MathF.Cos(angle);
                float density = MathF.Exp(-(u * u / 28 + v * v / 13)) + .7f * MathF.Exp(-((u - 4) * (u - 4) / 9 + (v - 3) * (v - 3) / 8));
                signal[(cy + dy) * n + cx + dx] -= 34 * density;
            }
        }
        return signal;
    }

    private void GenerateStudy(int variant, string condition)
    {
        const int n = 512;
        var signal = CreateSignal(variant, out _, out var random);
        if (condition == "lowcontrast")
        {
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                // An uneven broad dense region and loss of particle contrast survive per-image
                // normalization. This is illustrative appearance, not an inference of ice thickness.
                float dx = (x - 330f) / 195f, dy = (y - 280f) / 180f;
                float region = 1 / (1 + MathF.Exp((dx * dx + dy * dy - 1) * 7));
                float background = 135 + 9 * MathF.Sin(x * .013f + variant) * MathF.Cos(y * .009f) + 4 * MathF.Cos(x * .06f + y * .022f);
                float particles = signal[y * n + x] - background;
                float texture = 5 * MathF.Sin(x * .058f + y * .025f) * MathF.Cos(y * .071f);
                signal[y * n + x] = background + particles * (.5f - .27f * region) - 42 * region + texture * region;
            }
        }
        else if (condition == "drift")
        {
            // A directional box blur makes the same particle field visibly smeared. The
            // enhanced view removes noise only; it does not magically undo this displacement.
            var original = signal;
            signal = new float[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float sum = 0;
                for (int step = -10; step <= 10; step++)
                {
                    int sx = Math.Clamp(x + step, 0, n - 1);
                    int sy = Math.Clamp(y + (int)MathF.Round(step * .4f), 0, n - 1);
                    sum += original[sy * n + sx];
                }
                signal[y * n + x] = sum / 21;
            }
        }

        var raw = new float[signal.Length];
        var enhanced = new float[signal.Length];
        float rawNoise = condition == "lowcontrast" ? 20 : condition == "drift" ? 12 : 17;
        for (int j = 0; j < signal.Length; j++)
        {
            float noise = (float)(Math.Sqrt(-2 * Math.Log(Math.Max(1e-8, random.NextDouble()))) * Math.Cos(6.28 * random.NextDouble()));
            raw[j] = signal[j] + noise * rawNoise;
            enhanced[j] = signal[j] + noise * 2;
        }
        if (condition != "normal")
        {
            WriteMrc(StudyPath(variant, condition, "raw.mrc"), raw, n, n, 4);
            WriteMrc(StudyPath(variant, condition, "enhanced.mrc"), enhanced, n, n, 4);
        }
        WriteStudyThumbnail(StudyPath(variant, condition, "thumb.png"), raw, n);
    }

    private static void WriteStudyThumbnail(string path, float[] data, int n)
    {
        // Fixed across every condition and index: synthetic intensity 60 is black, 195
        // is white; values outside that range clip. No per-image normalization. The
        // 512-square image and vertical orientation match MicrographViewer.
        const double minimum = 60, range = 135;
        using var bitmap = new SKBitmap(n, n, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pixels = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int destination = (y * n + x) * 4;
            byte value = (byte)Math.Clamp((data[(n - y - 1) * n + x] - minimum) * 255 / range, 0, 255);
            pixels[destination] = pixels[destination + 1] = pixels[destination + 2] = value;
            pixels[destination + 3] = 255;
        }
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
        using var file = File.Create(path);
        encoded.SaveTo(file);
    }

    private static void WriteMrc(string path, float[] data, int width, int height, float pixelSize)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[1024]);
        stream.Position = 0;
        foreach (int v in new[] { width, height, 1, 2, 0, 0, 0, width, height, 1 }) writer.Write(v);
        foreach (float v in new[] { width * pixelSize, height * pixelSize, pixelSize, 90f, 90f, 90f }) writer.Write(v);
        foreach (int v in new[] { 1, 2, 3 }) writer.Write(v);
        writer.Write(data.Min()); writer.Write(data.Max()); writer.Write(data.Average());
        stream.Position = 208; writer.Write(new byte[] { 77, 65, 80, 32, 68, 65, 0, 0 });
        stream.Position = 1024;
        foreach (float value in data) writer.Write(value);
    }
}
