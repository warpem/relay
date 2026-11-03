using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Refund.Services;

namespace Refund.Tests.Services;

public sealed class TextFileEditorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "relay-editor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly HashSet<string> connectedSpacePaths = [];
    private readonly TextFileEditorService service;
    public TextFileEditorTests()
    {
        Directory.CreateDirectory(root);
        service = new(new EphemeralDataProtectionProvider(), () => connectedSpacePaths.ToArray());
    }
    public void Dispose() => Directory.Delete(root, true);
    private TextFileEditorService.Ticket Ticket(string path, string? encoding = null) =>
        service.OpenTicket(service.CreateTicket(path, root, "alice", encoding), "alice");
    private Task Save(TextFileEditorService.Ticket ticket, string text) =>
        service.SaveAsync(ticket, new MemoryStream(Encoding.UTF8.GetBytes(text)));
    private async Task<string> Read(TextFileEditorService.Ticket ticket)
    {
        using var result = new MemoryStream();
        await service.CopyContentAsync(ticket, result);
        return Encoding.UTF8.GetString(result.ToArray());
    }

    [Theory]
    [InlineData("space.relay")]
    [InlineData("legacy.space")]
    public async Task ConnectedSpaceCannotBeOpenedOrSavedUsingAnOlderTicket(string name)
    {
        string path = Path.Combine(root, name);
        await File.WriteAllTextAsync(path, "original");
        var ticket = Ticket(path, "utf8");
        connectedSpacePaths.Add(path);
        Assert.True(service.IsConnectedSpaceFile(path));
        Assert.Throws<UnauthorizedAccessException>(() => Ticket(path));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DescribeAsync(ticket));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Read(ticket));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Save(ticket, "bad"));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        connectedSpacePaths.Clear();
        Assert.False(service.IsConnectedSpaceFile(path));
        await Save(Ticket(path, "utf8"), "disconnected");
        Assert.Equal("disconnected", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveAsCannotTargetConnectedSpaceButOtherFilesRemainEditable()
    {
        string connected = Path.Combine(root, "space.relay");
        string other = Path.Combine(root, "other.space");
        await File.WriteAllTextAsync(connected, "connected");
        await File.WriteAllTextAsync(other, "other");
        var staleDestination = Ticket(connected, "utf8");
        connectedSpacePaths.Add(connected);
        var ticket = Ticket(other, "utf8");
        Assert.Throws<UnauthorizedAccessException>(() => service.PrepareDestination(ticket, "space.relay"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Save(staleDestination, "bad"));
        await Save(ticket, "allowed");
        Assert.Equal("connected", await File.ReadAllTextAsync(connected));
        Assert.Equal("allowed", await File.ReadAllTextAsync(other));
    }

    [Fact]
    public async Task ConnectingSpaceDuringUploadPreventsReplacementAndCleansUp()
    {
        string path = Path.Combine(root, "space.relay");
        await File.WriteAllTextAsync(path, "original");
        var ticket = Ticket(path, "utf8");
        using var input = new GeneratedTextStream(100000, onRead: () => connectedSpacePaths.Add(path));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(ticket, input));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root, ".relay-edit-*"));
    }

    [Fact]
    public async Task ConnectedSpaceProtectionResolvesRelativePathsAndSymbolicLinks()
    {
        string path = Path.Combine(root, "space.relay");
        await File.WriteAllTextAsync(path, "original");
        connectedSpacePaths.Add(Path.Combine(root, ".", "space.relay"));
        Assert.True(service.IsConnectedSpaceFile(path));
        if (OperatingSystem.IsWindows()) return;
        string alias = Path.Combine(root, "innocent.txt");
        File.CreateSymbolicLink(alias, path);
        Assert.True(service.IsConnectedSpaceFile(alias));
        Assert.Throws<UnauthorizedAccessException>(() => Ticket(alias));
        string directoryAlias = Path.Combine(root, "alias");
        Directory.CreateSymbolicLink(directoryAlias, root);
        Assert.True(service.IsConnectedSpaceFile(Path.Combine(directoryAlias, "space.relay")));
    }

    [Theory]
    [InlineData("utf8", false)]
    [InlineData("utf8", true)]
    [InlineData("utf16le", true)]
    [InlineData("utf16be", true)]
    [InlineData("utf32le", true)]
    [InlineData("utf32be", true)]
    public async Task StreamsUnicodePreservingBomLineEndingsAndFinalNewline(string type, bool bom)
    {
        Encoding encoding = type switch
        {
            "utf16le" => new UnicodeEncoding(false, bom), "utf16be" => new UnicodeEncoding(true, bom),
            "utf32le" => new UTF32Encoding(false, bom), "utf32be" => new UTF32Encoding(true, bom), _ => new UTF8Encoding(bom)
        };
        string path = Path.Combine(root, "input.txt");
        string text = "first\r\nβeta😀\r\n";
        await File.WriteAllTextAsync(path, text, encoding);
        var document = await service.DescribeAsync(Ticket(path));
        Assert.Equal("CRLF", document.LineEnding);
        var ticket = Ticket(path, document.Encoding);
        Assert.Equal(text, await Read(ticket));
        await Save(ticket, "changed\r\nβeta😀\r\n");
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes("changed\r\nβeta😀\r\n")), await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(root, ".relay-edit-*"));
    }

    [Fact]
    public async Task SaveOverwritesExternalChangesAndRecreatesDeletedOriginal()
    {
        string path = Path.Combine(root, "input.txt");
        await File.WriteAllTextAsync(path, "original");
        var ticket = Ticket(path, "utf8");
        await File.WriteAllTextAsync(path, "external change");
        await Save(ticket, "mine");
        Assert.Equal("mine", await File.ReadAllTextAsync(path));
        File.Delete(path);
        await Save(ticket, "mine again");
        Assert.Equal("mine again", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task SaveAsPreflightReportsExistingDestinationWithoutUploading()
    {
        string path = Path.Combine(root, "input.txt");
        await File.WriteAllTextAsync(path, "original");
        var ticket = Ticket(path, "utf8");
        var destination = service.PrepareDestination(ticket, "copy.txt");
        Assert.False(destination.Exists);
        Assert.Equal(Path.Combine(ticket.Root, "copy.txt"), destination.Path);
        await Save(Ticket(destination.Path, ticket.Encoding), "copy");
        Assert.True(service.PrepareDestination(ticket, "copy.txt").Exists);
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        await Save(Ticket(destination.Path, ticket.Encoding), "replace");
        Assert.Equal("replace", await File.ReadAllTextAsync(destination.Path));
    }

    [Fact]
    public async Task StreamsHundredMiBWithoutWholeFileBuffers()
    {
        const long length = 100L * 1024 * 1024;
        string path = Path.Combine(root, "large.star");
        var ticket = Ticket(path, "utf8");
        using var input = new GeneratedTextStream(length);
        await service.SaveAsync(ticket, input);
        Assert.Equal(length, new FileInfo(path).Length);
        Assert.True(input.LargestRead <= 65536);
        var document = await service.DescribeAsync(ticket);
        Assert.Equal(length, document.Size);
        // Stream.Null never retains the download; CopyContent uses fixed-size buffers.
        await service.CopyContentAsync(ticket, Stream.Null);
        Assert.Empty(Directory.GetFiles(root, ".relay-edit-*"));
    }

    [Fact]
    public async Task InterruptedOrInvalidUploadsKeepOriginalAndRemoveStagingFiles()
    {
        string path = Path.Combine(root, "input.txt");
        await File.WriteAllTextAsync(path, "original");
        using var broken = new GeneratedTextStream(1000000, failAfter: 100000);
        await Assert.ThrowsAsync<IOException>(() => service.SaveAsync(Ticket(path, "utf8"), broken));
        await Assert.ThrowsAsync<InvalidDataException>(() => Save(Ticket(path, "utf8"), "bad\0text"));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root, ".relay-edit-*"));
    }

    [Fact]
    public async Task UnicodeSurrogatesSurviveStreamBufferBoundaries()
    {
        string path = Path.Combine(root, "unicode.txt");
        string text = new string('a', 65535) + "😀" + new string('b', 65535) + "😀";
        var ticket = Ticket(path, "utf16le");
        await Save(ticket, text);
        Assert.Equal(text, await Read(ticket));
    }

    [Fact]
    public async Task RejectsBinaryInvalidUtf8AndOversizedFiles()
    {
        string path = Path.Combine(root, "input.txt");
        await File.WriteAllBytesAsync(path, [65, 0, 66]);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DescribeAsync(Ticket(path)));
        await File.WriteAllBytesAsync(path, [0xff, 65]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => service.DescribeAsync(Ticket(path)));
        using (var oversized = File.OpenWrite(path)) oversized.SetLength(TextFileEditorService.MaximumBytes + 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.DescribeAsync(Ticket(path)));
    }

    [Fact]
    public void RejectsTraversalSiblingPrefixAndSymlinksOutsideRoot()
    {
        var ticket = Ticket(Path.Combine(root, "input.txt"), "utf8");
        Assert.Throws<UnauthorizedAccessException>(() => service.PrepareDestination(ticket, "../outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => service.CreateTicket(root + "-sibling/file.txt", root, "alice"));
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), Path.GetTempPath());
        Assert.Throws<UnauthorizedAccessException>(() => service.PrepareDestination(ticket, "escape/outside.txt"));
    }

    [Fact]
    public void TicketsAreBoundToUserAndCannotBeModifiedOrReusedAfterExpiry()
    {
        string path = Path.Combine(root, "input.txt");
        string token = service.CreateTicket(path, root, "alice");
        Assert.Throws<UnauthorizedAccessException>(() => service.OpenTicket(token, "bob"));
        Assert.Throws<UnauthorizedAccessException>(() => service.OpenTicket("invalid", "alice"));
        var provider = new EphemeralDataProtectionProvider();
        var otherService = new TextFileEditorService(provider);
        string expired = provider.CreateProtector("Relay.TextFileEditor.v2").Protect(System.Text.Json.JsonSerializer.Serialize(
            new TextFileEditorService.Ticket(path, root, "alice", DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Throws<UnauthorizedAccessException>(() => otherService.OpenTicket(expired, "alice"));
    }

    [Fact]
    public async Task PreservesUnixExecutablePermissionAndRefusesReadOnlyFile()
    {
        if (OperatingSystem.IsWindows()) return;
        string path = Path.Combine(root, "script.sh");
        await File.WriteAllTextAsync(path, "echo hello\n");
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead;
        File.SetUnixFileMode(path, mode);
        await Save(Ticket(path, "utf8"), "echo bye\n");
        Assert.Equal(mode, File.GetUnixFileMode(path));
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Save(Ticket(path, "utf8"), "bad"));
    }

    // Produces data on demand; cannot seek and never holds a full document in memory.
    private sealed class GeneratedTextStream(long length, long failAfter = long.MaxValue, Action? onRead = null) : Stream
    {
        private long position;
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            onRead?.Invoke();
            if (position >= failAfter) throw new IOException("Simulated disconnected upload.");
            int read = (int)Math.Min(count, length - position);
            buffer.AsSpan(offset, read).Fill((byte)'a');
            position += read;
            LargestRead = Math.Max(LargestRead, read);
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
