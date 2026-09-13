using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.Services.Core.Repositories;

namespace Refund.Tests.Services;

public sealed class DataRepositoryPersistenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowStorageDoesNotBlockEditsOrLoseChangesMadeDuringTheWrite(bool projects)
    {
        string directory = Path.Combine(Path.GetTempPath(), "relay-persistence-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var documents = new ConcurrentQueue<string>();
        string target = Path.Combine(directory, projects ? "projects.json" : "space.relay");
        using var repository = new DataRepository(Path.Combine(directory, "projects.json"), (path, document) =>
        {
            if (path == target)
            {
                documents.Enqueue(document);
                if (documents.Count == 1)
                {
                    started.SetResult();
                    release.Wait(TimeSpan.FromSeconds(10));
                }
            }
            File.WriteAllText(path, document);
        });
        var user = new User { Id = 1 };
        var project = repository.CreateProject(user);
        var space = repository.CreateSpace(user, project, new Space { RootDirectory = directory });
        Task saving = Task.Run(() => SavePending(repository));
        Task? followingSave = null;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(() =>
            {
                if (projects)
                    repository.UpdateProject(user, project, item => item.Alias = "Edited during save");
                else
                    repository.UpdateSpace(user, space, item => item.Alias = "Edited during save");
            }).WaitAsync(TimeSpan.FromSeconds(1));

            followingSave = Task.Run(() => SavePending(repository));
            Assert.False(followingSave.IsCompleted);
        }
        finally
        {
            release.Set();
            await saving.WaitAsync(TimeSpan.FromSeconds(5));
            if (followingSave != null)
                await followingSave.WaitAsync(TimeSpan.FromSeconds(5));
        }

        try
        {
            Assert.Equal(2, documents.Count);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(target))!;
            Assert.Equal("Edited during save", (projects ? saved["Projects"]![0] : saved)!["Alias"]!.GetValue<string>());
            SavePending(repository);
            Assert.Equal(2, documents.Count);
        }
        finally
        {
            repository.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedSaveIsRetriedAndCleanSpaceIsNotWrittenAgain()
    {
        string directory = Path.Combine(Path.GetTempPath(), "relay-persistence-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "space.relay");
        int writes = 0;
        using var repository = new DataRepository(Path.Combine(directory, "projects.json"), (path, document) =>
        {
            if (path == target && ++writes == 1)
                throw new IOException("Storage unavailable");
            File.WriteAllText(path, document);
        });
        try
        {
            var user = new User { Id = 1 };
            var project = repository.CreateProject(user);
            var space = repository.CreateSpace(user, project, new Space { RootDirectory = directory });

            Assert.Throws<IOException>(() => repository.SaveSpaceImmediately(space));
            repository.UpdateSpace(user, space, item => item.Alias = "Latest edit");
            repository.SaveSpaceImmediately(space);
            repository.SaveSpaceImmediately(space);

            Assert.Equal(2, writes);
            Assert.Equal("Latest edit", JsonNode.Parse(File.ReadAllText(target))!["Alias"]!.GetValue<string>());
        }
        finally
        {
            repository.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SavePending(DataRepository repository) =>
        typeof(DataRepository).GetMethod("SaveChanges", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(repository, [null]);
}
