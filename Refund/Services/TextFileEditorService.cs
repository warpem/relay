using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Refund.Services;

/// <summary>Grants user-bound editing access and streams text between files and HTTP.</summary>
public sealed class TextFileEditorService(IDataProtectionProvider protection, Func<IEnumerable<string>> connectedSpacePaths = null)
{
    public const long MaximumBytes = 512L * 1024 * 1024;
    public const string ConnectedSpaceMessage = "This file represents a space currently connected in Relay and cannot be edited.";
    private readonly IDataProtector protector = protection.CreateProtector("Relay.TextFileEditor.v2");

    public record Ticket(string Path, string Root, string User, DateTimeOffset Expires, string Encoding = null);
    public record Document(string Path, long Size, string Encoding, string LineEnding);

    public string CreateTicket(string path, string root, string user, string encoding = null)
    {
        if (string.IsNullOrEmpty(user)) throw new UnauthorizedAccessException();
        root = ResolvePath(Path.GetFullPath(string.IsNullOrEmpty(root) ? "/" : root));
        path = CheckedPath(path, root);
        EnsureEditable(path);
        return protector.Protect(JsonSerializer.Serialize(new Ticket(path, root, user, DateTimeOffset.UtcNow.AddHours(8), encoding)));
    }

    public bool IsConnectedSpaceFile(string path)
    {
        if (connectedSpacePaths == null) return false;
        string resolved = ResolvePath(Path.GetFullPath(path));
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return connectedSpacePaths().Any(connected =>
            string.Equals(resolved, ResolvePath(Path.GetFullPath(connected)), comparison));
    }

    private void EnsureEditable(string path)
    {
        if (IsConnectedSpaceFile(path)) throw new UnauthorizedAccessException(ConnectedSpaceMessage);
    }

    public Ticket OpenTicket(string token, string user)
    {
        Ticket ticket;
        try { ticket = JsonSerializer.Deserialize<Ticket>(protector.Unprotect(token)); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        { throw new UnauthorizedAccessException("This editor session is invalid. Reopen the file browser."); }
        if (ticket == null || ticket.User != user || string.IsNullOrEmpty(user) || ticket.Expires < DateTimeOffset.UtcNow)
            throw new UnauthorizedAccessException("This editor session has expired. Reopen the file browser.");
        return ticket;
    }

    public async Task<Document> DescribeAsync(Ticket ticket, CancellationToken cancellation = default)
    {
        string path = CheckedPath(ticket.Path, ticket.Root);
        EnsureEditable(path);
        await using var stream = OpenRead(path);
        CheckSize(stream.Length);
        byte[] prefix = new byte[4];
        int count = await stream.ReadAtLeastAsync(prefix, 4, throwOnEndOfStream: false, cancellationToken: cancellation);
        var (encoding, skip, name) = DetectEncoding(prefix.AsSpan(0, count));
        stream.Position = skip;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);
        char[] sample = new char[32768];
        int chars = await reader.ReadAsync(sample.AsMemory(), cancellation);
        ValidateText(sample.AsSpan(0, chars));
        return new(path, stream.Length, name, sample.AsSpan(0, chars).Contains("\r\n".AsSpan(), StringComparison.Ordinal) ? "CRLF" : "LF");
    }

    /// <summary>Transcodes incrementally to UTF-8; no full-file byte array or string is allocated.</summary>
    public async Task CopyContentAsync(Ticket ticket, Stream destination, CancellationToken cancellation = default)
    {
        string path = CheckedPath(ticket.Path, ticket.Root);
        EnsureEditable(path);
        await using var source = OpenRead(path);
        CheckSize(source.Length);
        var encoding = EncodingByName(ticket.Encoding);
        source.Position = encoding.GetPreamble().Length;
        using var reader = new StreamReader(source, encoding, detectEncodingFromByteOrderMarks: false,
            bufferSize: 65536, leaveOpen: true);
        await using var writer = new StreamWriter(destination, new UTF8Encoding(false, true), 65536, leaveOpen: true);
        await CopyTextAsync(reader, writer, cancellation);
    }

    public (string Path, bool Exists) PrepareDestination(Ticket ticket, string destination)
    {
        EnsureEditable(ticket.Path);
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("Enter a destination file path.");
        string path = CheckedPath(Path.GetFullPath(destination, Path.GetDirectoryName(ticket.Path)!), ticket.Root);
        EnsureEditable(path);
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException();
        if (Directory.Exists(path)) throw new ArgumentException("Choose a file name, rather than a folder.");
        return (path, File.Exists(path));
    }

    /// <summary>Stages a raw UTF-8 upload, preserving the original encoding and replacing on success.</summary>
    public async Task SaveAsync(Ticket ticket, Stream content, CancellationToken cancellation = default)
    {
        if (ticket.Encoding == null) throw new InvalidDataException("Load the file before saving it.");
        string path = CheckedPath(ticket.Path, ticket.Root);
        EnsureEditable(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".relay-edit-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var reader = new StreamReader(content, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false,
                    bufferSize: 65536, leaveOpen: true);
                await using var writer = new StreamWriter(output, EncodingByName(ticket.Encoding), 65536, leaveOpen: true);
                await CopyTextAsync(reader, writer, cancellation, () => CheckSize(output.Position));
                CheckSize(output.Position);
            }
            // Do not check whether another program changed/deleted the original: Save deliberately overwrites.
            // Re-resolve paths to retain the browser root restriction if a symlink changed during upload.
            if (CheckedPath(path, ticket.Root) != path) throw new UnauthorizedAccessException("The destination path changed during saving.");
            if (File.Exists(path))
            {
                using (File.Open(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { }
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            }
            cancellation.ThrowIfCancellationRequested();
            EnsureEditable(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task CopyTextAsync(StreamReader reader, StreamWriter writer, CancellationToken cancellation, Action checkSize = null)
    {
        char[] buffer = new char[65536];
        long characters = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) != 0)
        {
            characters += count;
            CheckSize(characters); // UTF-8 uploads cannot contain more characters than bytes.
            ValidateText(buffer.AsSpan(0, count));
            await writer.WriteAsync(buffer.AsMemory(0, count), cancellation);
            checkSize?.Invoke();
        }
        await writer.FlushAsync(cancellation);
    }

    private static void CheckSize(long size)
    {
        if (size > MaximumBytes) throw new InvalidDataException("Text editing is limited to 512 MiB per file.");
    }

    private static void ValidateText(ReadOnlySpan<char> content)
    {
        foreach (char c in content)
            if (char.IsControl(c) && c is not ('\t' or '\r' or '\n' or '\f'))
                throw new InvalidDataException("This file contains binary or unsupported control characters.");
    }

    private static (Encoding Encoding, int Skip, string Name) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        foreach (string name in new[] { "utf32le", "utf32be", "utf8bom", "utf16le", "utf16be" })
        {
            var encoding = EncodingByName(name);
            var preamble = encoding.GetPreamble();
            if (bytes.StartsWith(preamble)) return (encoding, preamble.Length, name);
        }
        return (EncodingByName("utf8"), 0, "utf8");
    }

    private static Encoding EncodingByName(string name) => name switch
    {
        "utf8" => new UTF8Encoding(false, true),
        "utf8bom" => new UTF8Encoding(true, true),
        "utf16le" => new UnicodeEncoding(false, true, true),
        "utf16be" => new UnicodeEncoding(true, true, true),
        "utf32le" => new UTF32Encoding(false, true, true),
        "utf32be" => new UTF32Encoding(true, true, true),
        _ => throw new InvalidDataException("Unsupported text encoding.")
    };

    private static string CheckedPath(string path, string root)
    {
        path = ResolvePath(Path.GetFullPath(path));
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("The file must be inside the file browser's root folder.");
        return path;
    }

    // Resolve every existing component, not only the final file: parent directories can be links too.
    private static string ResolvePath(string path)
    {
        string resolved = Path.GetPathRoot(path)!;
        foreach (string part in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            FileSystemInfo info = Directory.Exists(resolved) ? new DirectoryInfo(resolved) : new FileInfo(resolved);
            if (info.LinkTarget != null)
                resolved = ResolvePath(info.ResolveLinkTarget(true)?.FullName ?? throw new IOException("Cannot resolve symbolic link."));
        }
        return resolved;
    }
}
