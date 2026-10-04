using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Setup.Core;
using Xunit;

namespace Setup.Core.Tests;

public sealed class InstallerPersistenceTests : IDisposable
{
    private const string Revision = "1234567890abcdef1234567890abcdef12345678";
    private readonly string root = Path.Combine(Path.GetTempPath(), "aioffice-setup-tests-" + Guid.NewGuid().ToString("N"), "Minh Huy tiếng Việt");

    [Fact]
    public void RebootProgressSurvivesASeparateStoreAndDoesNotTouchIdentity()
    {
        Directory.CreateDirectory(root);
        var identity = Path.Combine(root, "installation.json");
        File.WriteAllText(identity, "existing identity and protected credentials");
        var store = new ProgressStore(root);
        store.Save(new InstallProgress(1, Revision, InstallPhase.AwaitingReboot));
        Assert.Equal(InstallPhase.AwaitingReboot, new ProgressStore(root).Load()!.Phase);
        store.Save(new InstallProgress(1, Revision, InstallPhase.Ready));
        Assert.Equal(InstallPhase.Ready, new ProgressStore(root).Load()!.Phase);
        Assert.Equal("existing identity and protected credentials", File.ReadAllText(identity));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{invalid")]
    [InlineData("{\"SchemaVersion\":2,\"Revision\":\"1234567890abcdef1234567890abcdef12345678\",\"Phase\":\"Ready\"}")]
    [InlineData("{\"SchemaVersion\":1,\"Revision\":\"1234567890abcdef1234567890abcdef12345678\",\"Phase\":\"Ready\",\"Password\":\"unsafe\"}")]
    public void CorruptOrUnsupportedProgressFailsClosedAndIsRetained(string content)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "setup-progress.json");
        File.WriteAllText(path, content);
        Assert.ThrowsAny<Exception>(() => new ProgressStore(root).Load());
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public void InvalidSavePreservesThePreviousProgress()
    {
        var store = new ProgressStore(root);
        store.Save(new InstallProgress(1, Revision, InstallPhase.AwaitingReboot));
        Assert.Throws<InvalidDataException>(() => store.Save(new InstallProgress(1, "../other", InstallPhase.Ready)));
        Assert.Equal(InstallPhase.AwaitingReboot, store.Load()!.Phase);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("C:\\rooted.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("con.txt")]
    [InlineData("dir/NUL")]
    [InlineData("dir/Lpt9.log")]
    [InlineData("dir/trailing. ")]
    [InlineData("dir//file.txt")]
    public void UnsafeArchivesAreRejectedBeforeAnyPayloadIsWritten(string name)
    {
        using var archive = Archive(("safe.txt", "data"), (name, "unsafe"));
        Assert.Throws<InvalidDataException>(() => Extract(archive));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void HashMismatchDoesNotWriteAnyPayload()
    {
        using var archive = Archive(("safe.txt", "data"));
        Assert.Throws<InvalidDataException>(() => new BundleExtractor().Extract(archive, Revision, new string('0', 64), root));
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("File.txt", "file.txt")]
    [InlineData("parent", "parent/child.txt")]
    public void CaseCollisionsAndFileDirectoryConflictsAreRejected(string first, string second)
    {
        using var archive = Archive((first, "one"), (second, "two"));
        Assert.Throws<InvalidDataException>(() => Extract(archive));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void UnixSymlinkCannotBecomeAWindowsPayload()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xA1FF0000);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("../outside");
        }
        Assert.Throws<InvalidDataException>(() => Extract(archive));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void RepeatExtractionRetainsDataAndChecksPayloadIntegrity()
    {
        using var archive = Archive(("infra/khởi động.ps1", "source"), ("compose.local.yaml", "compose"));
        var target = Extract(archive);
        var identity = Path.Combine(root, "installation.json");
        File.WriteAllText(identity, "retain");
        Assert.Equal(target, Extract(archive));
        Assert.Equal("source", File.ReadAllText(Path.Combine(target, "infra", "khởi động.ps1")));
        File.WriteAllText(Path.Combine(target, "compose.local.yaml"), "changed");
        Assert.Throws<InvalidDataException>(() => Extract(archive));
        Assert.Equal("retain", File.ReadAllText(identity));
        Assert.Empty(Directory.GetDirectories(root, ".staging-*"));
    }

    [Theory]
    [InlineData("rogue.cs", false)]
    [InlineData("infra/rogue.ps1", false)]
    [InlineData("extra", true)]
    public void UnexpectedInstalledContentIsRejectedWithoutDeletingData(string relative, bool directory)
    {
        using var archive = Archive(("infra/start.ps1", "source"), ("compose.local.yaml", "compose"));
        var target = Extract(archive);
        var unexpected = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
        if (directory) Directory.CreateDirectory(unexpected);
        else File.WriteAllText(unexpected, "unexpected executable source");
        var identity = Path.Combine(root, "installation.json");
        File.WriteAllText(identity, "retain identity");
        Assert.Throws<InvalidDataException>(() => Extract(archive));
        Assert.Equal("retain identity", File.ReadAllText(identity));
        Assert.True(Path.Exists(unexpected));
        Assert.Empty(Directory.GetDirectories(root, ".staging-*"));
    }

    private string Extract(MemoryStream archive)
    {
        archive.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(archive));
        return new BundleExtractor().Extract(archive, Revision, hash, root);
    }

    private static MemoryStream Archive(params (string Name, string Content)[] entries)
    {
        var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        buffer.Position = 0;
        return buffer;
    }

    public void Dispose()
    {
        var owned = Path.GetDirectoryName(root)!;
        if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
    }
}
