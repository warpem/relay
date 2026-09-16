using Refund.Utils;

namespace Refund.Tests.Utils;

public sealed class FileUtilsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relay-delete-" + Guid.NewGuid());

    public FileUtilsTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void DeletionRetriesAndRemovesFilesLeftByAPartialAttempt()
    {
        string directory = Path.Combine(_root, "job");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "old.txt"), "old");
        string keep = Path.Combine(_root, "keep.txt");
        File.WriteAllText(keep, "keep");
        int attempts = 0;
        var delays = new List<TimeSpan>();

        FileUtils.DeleteDirectoryWithRetry(directory, (path, recursive) =>
        {
            if (++attempts == 1)
            {
                File.Delete(Path.Combine(path, "old.txt"));
                // Simulate a file arriving after recursive deletion enumerated the directory.
                File.WriteAllText(Path.Combine(path, "late.txt"), "late");
                throw new IOException("Directory not empty");
            }
            Directory.Delete(path, recursive);
        }, delays.Add);

        Assert.Equal(2, attempts);
        Assert.Single(delays);
        Assert.False(Directory.Exists(directory));
        Assert.Equal("keep", File.ReadAllText(keep));
    }

    [Fact]
    public void PersistentFailureIsPropagatedAfterBoundedRetries()
    {
        var failure = new IOException("Directory not empty");
        int attempts = 0;
        var delays = new List<TimeSpan>();

        var thrown = Assert.Throws<IOException>(() =>
            FileUtils.DeleteDirectoryWithRetry(_root, (_, _) =>
            {
                attempts++;
                throw failure;
            }, delays.Add));

        Assert.Same(failure, thrown);
        Assert.InRange(attempts, 2, 10);
        Assert.Equal(attempts - 1, delays.Count);
        Assert.InRange(delays.Sum(delay => delay.TotalMilliseconds), 1, 5_000);
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void MissingDirectoryIsAlreadyDeleted()
    {
        FileUtils.DeleteDirectoryWithRetry(Path.Combine(_root, "missing"));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public void MissingChildDoesNotLeaveTheParentUncleared()
    {
        string directory = Path.Combine(_root, "job");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "remaining.txt"), "remaining");
        int attempts = 0;

        FileUtils.DeleteDirectoryWithRetry(directory, (path, recursive) =>
        {
            if (++attempts == 1)
                throw new DirectoryNotFoundException("A child directory disappeared");
            Directory.Delete(path, recursive);
        }, _ => { });

        Assert.Equal(2, attempts);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void PermissionFailureIsPropagatedWithoutRetrying()
    {
        var failure = new UnauthorizedAccessException("Permission denied");
        var delays = new List<TimeSpan>();

        var thrown = Assert.Throws<UnauthorizedAccessException>(() =>
            FileUtils.DeleteDirectoryWithRetry(_root, (_, _) => throw failure, delays.Add));

        Assert.Same(failure, thrown);
        Assert.Empty(delays);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
