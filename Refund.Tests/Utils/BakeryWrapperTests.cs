using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Refund.Utils;

namespace Refund.Tests.Utils;

public sealed class BakeryWrapperTests
{
    [Fact]
    public void FailedRendererPropagatesItsDiagnosticInsteadOfReportingSuccess()
    {
        var error = Assert.Throws<InvalidOperationException>(() => BakeryWrapper.RunCommand(Shell(
            "echo 'ImportError: broken SciPy binary' >&2; exit 7")));

        Assert.Contains("exit code 7", error.Message);
        Assert.Contains("ImportError: broken SciPy binary", error.Message);
    }

    [Fact]
    public void MissingRendererDoesNotReportSuccess()
    {
        Assert.Throws<Win32Exception>(() => BakeryWrapper.RunCommand(
            new ProcessStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "bakery"))));
    }

    [Fact]
    public void SuccessfulRendererCanWriteWarningsToStderr()
    {
        BakeryWrapper.RunCommand(Shell("echo 'A harmless warning' >&2; exit 0"));
    }

    [Fact]
    public void IterationBatchCombinesParallelTasksAndNestedScopesIntoOneInvocation()
    {
        int invocations = 0;
        string? manifestPath = null;
        using (BakeryWrapper.BeginBatch(path =>
        {
            invocations++;
            manifestPath = path;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var commands = json.RootElement.EnumerateArray().ToArray();
            Assert.Equal(6, commands.Length);
            Assert.All(commands, command =>
            {
                var arguments = command.GetProperty("ArgumentList").EnumerateArray().Select(a => a.GetString()).ToArray();
                Assert.Equal("map-orthoslice-atlas", arguments[0]);
                Assert.Contains(arguments, argument => argument!.StartsWith("/data with spaces/map"));
            });
        }))
        {
            Parallel.For(1, 7, c =>
            {
                using var nested = BakeryWrapper.BeginBatch();
                BakeryWrapper.MapOrthosliceAtlas($"/data with spaces/map{c}.mrc", 1, $"/output/slices{c}.png");
            });
            Assert.Equal(0, invocations);
        }
        Assert.Equal(1, invocations);
        Assert.False(File.Exists(manifestPath));
    }

    [Fact]
    public void FailedBatchReleasesItsScopeAndDeletesTheManifest()
    {
        string? manifest = null;
        var batch = BakeryWrapper.BeginBatch(path =>
        {
            manifest = path;
            throw new InvalidOperationException("render failed");
        });
        BakeryWrapper.MapOrthosliceAtlas("/map.mrc", 1, "/image.png");
        Assert.Throws<InvalidOperationException>(batch.Dispose);
        Assert.False(File.Exists(manifest));
        batch.Dispose(); // Disposal does not repeat a failed render.
        using (BakeryWrapper.BeginBatch(path =>
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Single(json.RootElement.EnumerateArray());
        })) BakeryWrapper.MapOrthosliceAtlas("/next.mrc", 1, "/next.png");
    }

    private static ProcessStartInfo Shell(string command) => new("/bin/bash")
    {
        ArgumentList = { "-c", command }
    };
}
