using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupSpoolStorageLimits(int MaximumItems = 1024, long MaximumBytes = 64 * 1024 * 1024);
public sealed class GroupSpoolCapacityException() : IOException("Connector spool capacity reached; retain backlog and coverage uncertainty.");

public sealed class GroupSpoolItemReference
{
    internal GroupSpoolItemReference(GroupSpoolContentContext context) { Context = context; }
    public GroupSpoolContentContext Context { get; }
}

// Host-owned private volume only, one exclusive filesystem owner per account /
// service. Filenames and headers contain fixed scoped IDs/hashes, never source
// text, credentials, external group names or keys. This is not a provider ACK.
// Flush(true) protects file contents; atomic same-directory staging supports
// process restart. Power-loss guarantees also depend on the host filesystem.
public sealed class GroupConnectorFileSpool : IDisposable
{
    public const int MaximumRecordBytes = GroupSpoolContentProtector.MaximumEnvelopeBytes + 294;
    private readonly object sync = new();
    private readonly string folder;
    private readonly GroupListenerAccountScope account;
    private readonly Guid serviceId;
    private readonly GroupSpoolStorageLimits limits;
    private FileStream? ownership;

    private GroupConnectorFileSpool(string folder, GroupListenerAccountScope account, Guid serviceId, GroupSpoolStorageLimits limits, FileStream ownership)
    { this.folder = folder; this.account = account; this.serviceId = serviceId; this.limits = limits; this.ownership = ownership; }

    public static GroupConnectorFileSpool Open(string privateRoot, GroupListenerAccountScope account, Guid serviceId, GroupSpoolStorageLimits? limits = null)
    {
        limits ??= new();
        if (account is null || serviceId == Guid.Empty || limits.MaximumItems is < 1 or > 4096 ||
            limits.MaximumBytes is < MaximumRecordBytes or > 256 * 1024 * 1024 ||
            string.IsNullOrEmpty(privateRoot) || !Path.IsPathFullyQualified(privateRoot)) throw Unavailable();
        account.Validate();
        var root = Path.GetFullPath(privateRoot);
        if (!Directory.Exists(root) || Path.TrimEndingDirectorySeparator(root) == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!)) throw Unavailable();
        FileStream? held = null;
        try
        {
            RequireRegularPath(root);
            var spoolRoot = Path.Combine(root, "group-spool");
            Directory.CreateDirectory(spoolRoot); RequireRegularPath(spoolRoot);
            var folder = Path.Combine(spoolRoot, $"{account.TenantId:N}_{account.CompanyId:N}_{account.ConnectorAccountId:N}_{serviceId:N}");
            Directory.CreateDirectory(folder); RequireRegularPath(folder);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(spoolRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var ownerPath = Path.Combine(folder, ".owner");
            if (File.Exists(ownerPath)) RequireRegularPath(ownerPath);
            held = OpenFile(ownerPath, FileMode.OpenOrCreate);
            var result = new GroupConnectorFileSpool(folder, account, serviceId, limits, held);
            result.RecoverStaging(); held = null;
            return result;
        }
        catch (Exception error) when (IsStorageError(error)) { throw Unavailable(); }
        finally { held?.Dispose(); }
    }

    public GroupSpoolItemReference Append(GroupSpoolProtectedContent item)
    {
        lock (sync)
        {
            RequireOpen();
            try
            {
                RequireItem(item); RecoverStaging();
                var target = ItemPath(item.Context.EventIdentityHash, ".spool");
                if (File.Exists(target))
                {
                    var existing = Read(target);
                    if (existing.Context != item.Context with { AdmittedAtUtc = existing.Context.AdmittedAtUtc }) throw Unavailable();
                    return new(existing.Context);
                }
                var bytes = Encode(item); var current = Inspect();
                if (current.Count >= limits.MaximumItems || current.Bytes > limits.MaximumBytes - bytes.Length) throw new GroupSpoolCapacityException();
                var stage = ItemPath(item.Context.EventIdentityHash, ".pending");
                using (var stream = OpenFile(stage, FileMode.CreateNew))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                File.Move(stage, target, overwrite: false);
                return new(item.Context);
            }
            catch (GroupSpoolCapacityException) { throw; }
            catch (Exception error) when (IsStorageError(error)) { throw Unavailable(); }
        }
    }

    public IReadOnlyList<GroupSpoolItemReference> Pending()
    {
        lock (sync)
        {
            RequireOpen();
            try
            {
                RecoverStaging();
                return Inspect().Paths.Select(path => new GroupSpoolItemReference(Read(path).Context))
                    .OrderBy(x => x.Context.AdmittedAtUtc).ThenBy(x => x.Context.EventIdentityHash, StringComparer.Ordinal).ToArray();
            }
            catch (Exception error) when (IsStorageError(error)) { throw Unavailable(); }
        }
    }

    public GroupSpoolProtectedContent Load(GroupSpoolItemReference reference)
    {
        lock (sync)
        {
            RequireOpen();
            try
            {
                if (reference is null) throw Unavailable();
                Inspect(); var stored = Read(ItemPath(reference.Context.EventIdentityHash, ".spool"));
                if (stored.Context != reference.Context) throw Unavailable();
                return stored;
            }
            catch (Exception error) when (IsStorageError(error)) { throw Unavailable(); }
        }
    }

    // Only the trusted connector transport may call this after a successful
    // authenticated response tied to its exact event request. A public receipt
    // DTO alone does not authenticate a SQL commit. No HTTP deletion API exists.
    internal bool Acknowledge(GroupSpoolItemReference reference, GroupIngressCommittedReceipt committed)
    {
        lock (sync)
        {
            RequireOpen();
            if (reference is null || committed is null || committed.Source != reference.Context.Source || committed.MessageId == Guid.Empty ||
                committed.Revision <= 0 || committed.CommittedSequence <= 0 || committed.CommittedAtUtc.Offset != TimeSpan.Zero) throw Unavailable();
            try
            {
                Inspect(); var path = ItemPath(reference.Context.EventIdentityHash, ".spool");
                if (!File.Exists(path)) return false;
                if (Read(path).Context != reference.Context) throw Unavailable();
                File.Delete(path); return true;
            }
            catch (Exception error) when (IsStorageError(error)) { throw Unavailable(); }
        }
    }

    private void RecoverStaging()
    {
        foreach (var stage in Inspect().Paths.Where(x => x.EndsWith(".pending", StringComparison.Ordinal)))
        {
            var item = Read(stage); var target = ItemPath(item.Context.EventIdentityHash, ".spool");
            if (File.Exists(target))
            {
                var existing = Read(target);
                if (existing.Context != item.Context || !existing.Envelope.AsSpan().SequenceEqual(item.Envelope)) throw Unavailable();
                File.Delete(stage);
            }
            else File.Move(stage, target, overwrite: false);
        }
    }

    private (List<string> Paths, int Count, long Bytes) Inspect()
    {
        RequireRegularPath(folder);
        var paths = new List<string>(); long bytes = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(folder))
        {
            RequireRegularPath(path);
            if (Path.GetFileName(path) == ".owner") continue;
            var name = Path.GetFileName(path); var extension = Path.GetExtension(name);
            if (extension is not ".spool" and not ".pending" || !IsHash(Path.GetFileNameWithoutExtension(name)) || Directory.Exists(path)) throw Unavailable();
            var length = new FileInfo(path).Length;
            if (length is < 1 or > MaximumRecordBytes || paths.Count >= limits.MaximumItems || bytes > limits.MaximumBytes - length) throw Unavailable();
            paths.Add(path); bytes += length;
        }
        return (paths, paths.Count, bytes);
    }

    private GroupSpoolProtectedContent Read(string path)
    {
        RequireRegularPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > MaximumRecordBytes) throw Unavailable();
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("AIOFGSP1"u8)) throw Unavailable();
        Guid Id() => new(reader.ReadBytes(16));
        string Hash() => Convert.ToHexString(reader.ReadBytes(32));
        var source = new GroupScope(Id(), Id(), Id()); var accountId = Id(); var service = Id();
        var credential = reader.ReadInt64(); var version = reader.ReadInt64(); var grant = reader.ReadInt64(); var deletion = reader.ReadInt64();
        var captured = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var external = Hash(); var eventId = Hash(); var body = Hash(); var kind = (GroupSourceEventKind)reader.ReadByte();
        var keyLength = reader.ReadByte(); if (keyLength is < 1 or > 64) throw Unavailable();
        var keyId = Encoding.ASCII.GetString(reader.ReadBytes(keyLength)); var size = reader.ReadInt32();
        if (size is < 30 or > GroupSpoolContentProtector.MaximumEnvelopeBytes || stream.Length - stream.Position != size) throw Unavailable();
        var item = new GroupSpoolProtectedContent(new(source, accountId, service, credential, version, grant, deletion, external, eventId, kind, body, captured, keyId), reader.ReadBytes(size));
        RequireItem(item);
        if (Path.GetFileNameWithoutExtension(path) != item.Context.EventIdentityHash) throw Unavailable();
        return item;
    }

    private byte[] Encode(GroupSpoolProtectedContent item)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        var context = item.Context;
        writer.Write("AIOFGSP1"u8);
        foreach (var id in new[] { context.Source.TenantId, context.Source.CompanyId, context.Source.SourceBindingId, context.ConnectorAccountId, context.ServiceId }) writer.Write(id.ToByteArray());
        foreach (var value in new[] { context.CredentialEpoch, context.SourceVersion, context.GrantVersion, context.DeletionGeneration, context.AdmittedAtUtc.Ticks }) writer.Write(value);
        foreach (var hash in new[] { context.ExternalIdentityHash, context.EventIdentityHash, context.BodySha256 }) writer.Write(Convert.FromHexString(hash));
        writer.Write((byte)context.EventKind); var keyId = Encoding.ASCII.GetBytes(context.KeyId); writer.Write((byte)keyId.Length); writer.Write(keyId);
        writer.Write(item.Envelope.Length); writer.Write(item.Envelope);
        if (stream.Length > MaximumRecordBytes) throw Unavailable();
        return stream.ToArray();
    }

    private void RequireItem(GroupSpoolProtectedContent item)
    {
        var context = item?.Context;
        if (context?.Source is null || context.Source.TenantId != account.TenantId || context.Source.CompanyId != account.CompanyId ||
            context.ConnectorAccountId != account.ConnectorAccountId || context.ServiceId != serviceId || context.CredentialEpoch <= 0 || context.SourceVersion <= 0 ||
            context.GrantVersion <= 0 || context.DeletionGeneration < 0 || context.AdmittedAtUtc.Offset != TimeSpan.Zero || !Enum.IsDefined(context.EventKind) ||
            !IsHash(context.ExternalIdentityHash) || !IsHash(context.EventIdentityHash) || !IsHash(context.BodySha256) || string.IsNullOrEmpty(context.KeyId) || context.KeyId.Length > 64 ||
            context.KeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') || item!.Envelope is null ||
            item.Envelope.Length is < 30 or > GroupSpoolContentProtector.MaximumEnvelopeBytes || item.Envelope[0] != 1) throw Unavailable();
        context.Source.Validate();
    }

    private string ItemPath(string hash, string extension)
    { if (!IsHash(hash)) throw Unavailable(); return Path.Combine(folder, hash + extension); }
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    private static void RequireRegularPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Unavailable();
    }
    private static FileStream OpenFile(string path, FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.WriteThrough };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new(path, options);
    }
    private static bool IsStorageError(Exception error) => error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException;
    private static IOException Unavailable() => new("Connector spool is unavailable; preserve backlog and coverage uncertainty.");
    private void RequireOpen() { if (ownership is null) throw new ObjectDisposedException(nameof(GroupConnectorFileSpool)); }
    public void Dispose() { lock (sync) { ownership?.Dispose(); ownership = null; } }
}
