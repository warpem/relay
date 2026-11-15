using System.Diagnostics;
using Refund.Services.Storage.Walking;
using Xunit.Abstractions;

namespace Refund.Tests.Storage;

/// <summary>
/// Measures a real folder with both walkers and reports the totals and speed, for checking the native walker on a
/// cluster file system against du/find. Does nothing unless RELAY_STORAGE_BENCH names a folder:
/// <c>RELAY_STORAGE_BENCH=/some/job/folder dotnet test --filter StorageWalkBenchmark --logger "console;verbosity=detailed"</c>
/// </summary>
public sealed class StorageWalkBenchmark(ITestOutputHelper output)
{
    [Fact]
    public void WalkersAgreeOnARealFolder()
    {
        string folder = Environment.GetEnvironmentVariable("RELAY_STORAGE_BENCH");
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        var managed = Walk(folder, native: false);
        if (!LinuxFs.IsSupported)
            return;
        var native = Walk(folder, native: true);

        Assert.Equal(managed.Entries, native.Entries);
        Assert.Equal(managed.Folders, native.Folders);
        Assert.Equal(managed.Links, native.Links);
    }

    private Refund.DataModel.UsageTotals Walk(string folder, bool native)
    {
        var state = new WalkState();
        state.Begin(null, null, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        Assert.True(native ? LinuxWalker.Walk(folder, state) : ManagedWalker.Walk(folder, state));
        var t = state.Totals;
        double seconds = Math.Max(1e-6, watch.Elapsed.TotalSeconds);
        output.WriteLine($"{(native ? "native " : "managed")}: {t.Bytes:N0} bytes, {t.Allocated:N0} allocated, {t.Files:N0} files, " +
            $"{t.Folders:N0} folders, {t.Links:N0} links, {t.Errors:N0} errors in {seconds:N2} s ({t.Entries / seconds:N0} entries/s)");
        return t;
    }
}
