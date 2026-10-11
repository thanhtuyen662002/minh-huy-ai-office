using System.Security.Cryptography;
using System.Text;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

internal sealed record GroupTerminalAdvanceMetadata(GroupScope Scope, Guid BatchId, Guid TerminalOperationId,
    long PreviousVersion, long Version, long AfterSequence, long ThroughSequence,
    byte[] PreviousFingerprint, byte[] TerminalManifestSha256, DateTimeOffset TerminalCommittedAtUtc,
    DateTimeOffset AdvancedAtUtc, Guid ServiceId, long CredentialEpoch, long GrantVersion,
    long SourceVersion, long DeletionGeneration, long AccountVersion, byte[] AuthoritySha256, string KeyId)
{
    public override string ToString() => "Group terminal advance metadata (integrity prerequisite).";
}

// Integrity of closed metadata only. The future owned store must authenticate
// the historical terminal graph, current Extract authority, latest predecessor
// and SQL chain before signing. This primitive cannot advance a cursor.
internal sealed class GroupTerminalAdvanceIntegrity
{
    private static readonly byte[] Magic = "AIOGFAD1"u8.ToArray();
    private static readonly byte[] Purpose = "aioffice-group-terminal-advance-integrity-v1"u8.ToArray();
    internal const int MinimumBytes = 322;
    internal const int MaximumBytes = 385;
    private const int MacBytes = 32;
    private readonly byte[] encoded;
    private readonly GroupTerminalAdvanceMetadata metadata;

    private GroupTerminalAdvanceIntegrity(byte[] bytes, GroupTerminalAdvanceMetadata value)
    { encoded = bytes.ToArray(); metadata = Copy(value); Fingerprint = Convert.ToHexString(SHA256.HashData(encoded)); }
    internal string Fingerprint { get; }
    internal GroupTerminalAdvanceMetadata Metadata => Copy(metadata);
    internal byte[] Write() => encoded.ToArray();
    public override string ToString() => "Authenticated group advance metadata (no current authority).";

    internal static GroupTerminalAdvanceIntegrity Sign(GroupTerminalAdvanceMetadata input, GroupSourceKeyMaterial key)
    {
        try
        {
            Validate(input); var value = Copy(input); Validate(value);
            if (key is null || key.KeyId != value.KeyId) throw Unavailable();
            var body = Encode(value); var signature = Mac(value.Scope, body, key);
            var envelope = new byte[body.Length + MacBytes];
            body.CopyTo(envelope, 0); signature.CopyTo(envelope, body.Length);
            return new(envelope, value);
        }
        catch (Exception) { throw Unavailable(); }
    }

    // Only selects a retained key within an already authorized exact scope.
    // The key ID remains untrusted until Require verifies the complete MAC.
    internal static string PeekKeyId(ReadOnlySpan<byte> bytes, GroupScope expectedScope)
    {
        try
        {
            expectedScope.Validate(); var value = Decode(bytes);
            if (value.Scope != expectedScope) throw Unavailable();
            return value.KeyId;
        }
        catch (Exception) { throw Unavailable(); }
    }

    internal static GroupTerminalAdvanceIntegrity Require(ReadOnlySpan<byte> bytes, GroupScope expectedScope,
        GroupSourceKeyMaterial key)
    {
        try
        {
            expectedScope.Validate();
            if (bytes.Length is < MinimumBytes or > MaximumBytes) throw Unavailable();
            var copy = bytes.ToArray(); var value = Decode(copy);
            if (value.Scope != expectedScope || key is null || key.KeyId != value.KeyId) throw Unavailable();
            var signature = Mac(expectedScope, copy.AsSpan(0, copy.Length - MacBytes), key);
            if (!CryptographicOperations.FixedTimeEquals(signature, copy.AsSpan(copy.Length - MacBytes))) throw Unavailable();
            return new(copy, value);
        }
        catch (Exception) { throw Unavailable(); }
    }

    private static byte[] Encode(GroupTerminalAdvanceMetadata value)
    {
        using var stream = new MemoryStream(MaximumBytes);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Magic);
        foreach (var id in new[] { value.Scope.TenantId, value.Scope.CompanyId, value.Scope.SourceBindingId,
            value.BatchId, value.TerminalOperationId, value.ServiceId }) writer.Write(id.ToByteArray());
        foreach (var number in new[] { value.PreviousVersion, value.Version, value.AfterSequence, value.ThroughSequence,
            value.CredentialEpoch, value.GrantVersion, value.SourceVersion, value.DeletionGeneration, value.AccountVersion }) writer.Write(number);
        writer.Write(value.TerminalCommittedAtUtc.UtcTicks); writer.Write(value.AdvancedAtUtc.UtcTicks);
        writer.Write(value.PreviousFingerprint); writer.Write(value.TerminalManifestSha256); writer.Write(value.AuthoritySha256);
        writer.Write((byte)value.KeyId.Length); writer.Write(Encoding.ASCII.GetBytes(value.KeyId));
        writer.Flush(); return stream.ToArray();
    }

    private static GroupTerminalAdvanceMetadata Decode(ReadOnlySpan<byte> envelope)
    {
        if (envelope.Length is < MinimumBytes or > MaximumBytes) throw Unavailable();
        using var stream = new MemoryStream(envelope[..^MacBytes].ToArray(), false);
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw Unavailable();
        Guid Id() { var bytes = reader.ReadBytes(16); return bytes.Length == 16 ? new(bytes) : throw Unavailable(); }
        var scope = new GroupScope(Id(), Id(), Id()); var batch = Id(); var operation = Id(); var service = Id();
        var previous = reader.ReadInt64(); var version = reader.ReadInt64(); var after = reader.ReadInt64(); var through = reader.ReadInt64();
        var credential = reader.ReadInt64(); var grant = reader.ReadInt64(); var source = reader.ReadInt64();
        var deletion = reader.ReadInt64(); var account = reader.ReadInt64();
        var committed = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var advanced = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
        var predecessor = reader.ReadBytes(32); var terminal = reader.ReadBytes(32); var authority = reader.ReadBytes(32);
        var keyLength = reader.ReadByte(); var keyBytes = reader.ReadBytes(keyLength);
        if (keyLength is < 1 or > 64 || keyBytes.Length != keyLength || keyBytes.Any(x => x > 127)
            || stream.Position != stream.Length) throw Unavailable();
        var value = new GroupTerminalAdvanceMetadata(scope, batch, operation, previous, version, after, through,
            predecessor, terminal, committed, advanced, service, credential, grant, source, deletion, account, authority,
            Encoding.ASCII.GetString(keyBytes));
        Validate(value); return value;
    }

    private static void Validate(GroupTerminalAdvanceMetadata value)
    {
        value.Scope.Validate();
        if (value.BatchId == Guid.Empty || value.TerminalOperationId == Guid.Empty || value.ServiceId == Guid.Empty
            || value.Version <= 0 || value.PreviousVersion < 0 || value.PreviousVersion != value.Version - 1
            || value.AfterSequence < 0 || value.ThroughSequence <= value.AfterSequence
            || value.ThroughSequence - value.AfterSequence > GroupBatchAllocationPrefix.MaximumRawRevisions
            || value.PreviousFingerprint is not { Length: 32 } || value.TerminalManifestSha256 is not { Length: 32 }
            || value.TerminalManifestSha256.All(x => x == 0) || value.AuthoritySha256 is not { Length: 32 } || value.AuthoritySha256.All(x => x == 0)
            || (value.Version == 1 ? value.AfterSequence != 0 || value.PreviousFingerprint.Any(x => x != 0)
                : value.AfterSequence == 0 || value.PreviousFingerprint.All(x => x == 0))
            || value.CredentialEpoch <= 0 || value.GrantVersion <= 0 || value.SourceVersion <= 0
            || value.DeletionGeneration < 0 || value.AccountVersion <= 0
            || value.TerminalCommittedAtUtc.Offset != TimeSpan.Zero || value.AdvancedAtUtc.Offset != TimeSpan.Zero
            || value.TerminalCommittedAtUtc > value.AdvancedAtUtc || string.IsNullOrEmpty(value.KeyId) || value.KeyId.Length > 64
            || value.KeyId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '-' and not '_')) throw Unavailable();
    }

    private static byte[] Mac(GroupScope scope, ReadOnlySpan<byte> body, GroupSourceKeyMaterial key)
    {
        var derived = new byte[32];
        try
        {
            var salt = Encoding.ASCII.GetBytes(FormattableString.Invariant(
                $"aioffice-group-frontier-key-v1\0{scope.TenantId:D}/{scope.CompanyId:D}/{scope.SourceBindingId:D}"));
            HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Key, derived, salt, Purpose);
            return HMACSHA256.HashData(derived, body);
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }
    private static GroupTerminalAdvanceMetadata Copy(GroupTerminalAdvanceMetadata value) => value with
    { PreviousFingerprint = value.PreviousFingerprint.ToArray(), TerminalManifestSha256 = value.TerminalManifestSha256.ToArray(), AuthoritySha256 = value.AuthoritySha256.ToArray() };
    private static InvalidOperationException Unavailable() => new("Group terminal advance integrity is not available.");
}
