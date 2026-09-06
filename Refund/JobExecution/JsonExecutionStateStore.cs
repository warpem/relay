using System.Text;
using System.Text.Json;

namespace Refund.JobExecution;

public sealed class JsonExecutionStateStore : IExecutionStateStore
{
    private const int FormatVersion = 1;

    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private string _lastDocument;

    public JsonExecutionStateStore(string path)
    {
        _path = path;
    }

    public async Task<ExecutionCoordinatorSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return null;

        string json = await File.ReadAllTextAsync(_path, cancellationToken);
        var document = JsonSerializer.Deserialize<ExecutionStateDocument>(json, _options)
                       ?? throw new InvalidDataException($"Execution state in {_path} is empty.");

        if (document.Version != FormatVersion)
            throw new InvalidDataException(
                $"Execution state version {document.Version} is not supported by this Relay version.");

        _lastDocument = json;
        return document.Coordinator;
    }

    public async Task SaveAsync(
        ExecutionCoordinatorSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(
            new ExecutionStateDocument(FormatVersion, snapshot), _options);
        if (json == _lastDocument)
            return;

        string directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = $"{_path}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
            _lastDocument = json;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record ExecutionStateDocument(
        int Version,
        ExecutionCoordinatorSnapshot Coordinator);
}
