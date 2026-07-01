using Refund.Utils;

namespace Refund.Tests.Utils;

public class JobToolsReadLogTailTests
{
    private static string WriteTemp(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), "relay_logtail_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void CollapsesCarriageReturnProgressBars()
    {
        string path = WriteTemp("start\nprogress: 10%\rprogress: 50%\rprogress: 100%\ndone\n");
        try
        {
            var tail = JobTools.ReadLogTail(path, 100);
            Assert.Equal(new[] { "start", "progress: 100%", "done" }, tail);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReturnsOnlyLastNLines()
    {
        string path = WriteTemp(string.Join("\n", Enumerable.Range(1, 10).Select(i => $"line{i}")) + "\n");
        try
        {
            var tail = JobTools.ReadLogTail(path, 3);
            Assert.Equal(new[] { "line8", "line9", "line10" }, tail);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DropsPartialFirstLine_WhenWindowTruncates()
    {
        string content = string.Join("\n", Enumerable.Range(1, 100).Select(i => $"line{i:D3}")) + "\n";
        string path = WriteTemp(content);
        try
        {
            // Tiny window forces the read to start mid-file; the partial first line must be dropped.
            var tail = JobTools.ReadLogTail(path, 100, maxWindowBytes: 20);
            Assert.Equal("line100", tail[^1]);
            Assert.All(tail, l => Assert.Matches(@"^line\d{3}$", l)); // every returned line is complete
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFile_ReturnsEmpty()
    {
        string missing = Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N"));
        Assert.Empty(JobTools.ReadLogTail(missing, 100));
    }

    [Fact]
    public void HandlesCrlfLineEndings()
    {
        string path = WriteTemp("alpha\r\nbeta\r\ngamma\r\n");
        try
        {
            var tail = JobTools.ReadLogTail(path, 100);
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, tail);
        }
        finally { File.Delete(path); }
    }
}
