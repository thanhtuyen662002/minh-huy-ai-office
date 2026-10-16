extern alias GroupReferenceProof;

using System.Security.Cryptography;
using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedFrontierRootObservationTests
{
    public static IEnumerable<object[]> Fields()
    {
        foreach (var (kind, type) in new[] { ("advance", typeof(GroupTerminalAdvanceRecord)),
            ("key", typeof(GroupTerminalAdvanceKeyRecord)), ("cursor", typeof(GroupTerminalFrontierStateRecord)) })
            foreach (var property in type.GetProperties()) yield return [kind, property.Name];
    }

    [Fact]
    public void IndependentBinaryFrameAndDistinctKeyPurposeAcceptTheShippingRoot()
    {
        var f = new Fixture(); f.Observe();
        Assert.Equal(26, typeof(GroupTerminalAdvanceRecord).GetProperties().Length);
        Assert.Equal(14, typeof(GroupTerminalAdvanceKeyRecord).GetProperties().Length);
        Assert.Equal(7, typeof(GroupTerminalFrontierStateRecord).GetProperties().Length);
    }

    [Theory]
    [MemberData(nameof(Fields))]
    public void EveryPersistedAdvanceEnrollmentAndCursorFieldIsObserved(string kind, string field)
    {
        var f = new Fixture(); f.Observe();
        var target = kind == "advance" ? (object)f.Row : kind == "key" ? f.Key : f.Cursor;
        var property = target.GetType().GetProperty(field)!;
        var value = property.GetValue(target);
        object replacement = property.PropertyType == typeof(Guid) || property.PropertyType == typeof(Guid?) ? Guid.NewGuid()
            : property.PropertyType == typeof(long) ? (long)value! + 1
            : property.PropertyType == typeof(long?) ? 1L
            : property.PropertyType == typeof(int) ? (int)value! + 1
            : property.PropertyType == typeof(DateTimeOffset) ? ((DateTimeOffset)value!).AddTicks(1)
            : property.PropertyType == typeof(DateTimeOffset?) ? f.Now
            : property.PropertyType == typeof(string) ? "another-key"
            : Changed(value as byte[]);
        property.SetValue(target, replacement);
        Assert.Throws<InvalidOperationException>(f.Observe);
    }

    [Theory]
    [InlineData("envelope")]
    [InlineData("enrollment-purpose")]
    [InlineData("foreign-terminal")]
    [InlineData("foreign-terminal-company")]
    [InlineData("foreign-terminal-tenant")]
    [InlineData("terminal-after")]
    [InlineData("future-terminal")]
    [InlineData("row-offset")]
    [InlineData("key-offset")]
    [InlineData("cursor-offset")]
    [InlineData("terminal-offset")]
    [InlineData("expected-offset")]
    [InlineData("raw-key")]
    public void ChecksumsForeignHistoryEqualInstantOffsetsAndWrongPurposeDoNotExcuseTampering(string fault)
    {
        var f = new Fixture(); f.Observe();
        switch (fault)
        {
            case "envelope": f.Row.Envelope[120] ^= 1; f.Row.Fingerprint = SHA256.HashData(f.Row.Envelope); break;
            case "enrollment-purpose": f.Key.MaterialFingerprint = SHA256.HashData(f.Raw); break;
            case "foreign-terminal": f.Terminal.BindingId = Guid.NewGuid(); break;
            case "foreign-terminal-company": f.Terminal.CompanyId = Guid.NewGuid(); break;
            case "foreign-terminal-tenant": f.Terminal.TenantId = Guid.NewGuid(); break;
            case "terminal-after": f.Terminal.AfterSequence = 1; break;
            case "future-terminal": f.Terminal.CommittedAtUtc = f.Now.AddTicks(1); break;
            case "row-offset": f.Row.AdvancedAtUtc = f.Now.ToOffset(TimeSpan.FromHours(7)); break;
            case "key-offset": f.Key.EnrolledAtUtc = f.Now.ToOffset(TimeSpan.FromHours(7)); break;
            case "cursor-offset": f.Cursor.UpdatedAtUtc = f.Now.ToOffset(TimeSpan.FromHours(7)); break;
            case "terminal-offset": f.Row.TerminalCommittedAtUtc = f.Terminal.CommittedAtUtc = f.Terminal.CommittedAtUtc.ToOffset(TimeSpan.FromHours(7)); break;
            case "expected-offset": f.Now = f.Now.ToOffset(TimeSpan.FromHours(7)); break;
            case "raw-key": f.Raw[0] ^= 1; break;
        }
        Assert.Throws<InvalidOperationException>(f.Observe);
    }

    [Theory]
    [InlineData("row")]
    [InlineData("cursor")]
    [InlineData("key")]
    [InlineData("terminal")]
    [InlineData("authority")]
    [InlineData("raw")]
    [InlineData("envelope")]
    [InlineData("fingerprint")]
    [InlineData("row-terminal")]
    [InlineData("row-authority")]
    [InlineData("terminal-hash")]
    [InlineData("key-material")]
    [InlineData("key-authority")]
    [InlineData("authority-hash")]
    public void MissingOrOversizedCarriersRefuseBeforeBinaryFrameConstruction(string fault)
    {
        foreach (var missing in new[] { true, false })
        {
            var f = new Fixture(); var bytes = missing ? null! : new byte[1_000_000];
            switch (fault)
            {
                case "row": f.Row = null!; break;
                case "cursor": f.Cursor = null!; break;
                case "key": f.Key = null!; break;
                case "terminal": f.Terminal = null!; break;
                case "authority": f.Authority = null!; break;
                case "raw": f.Raw = bytes; break;
                case "envelope": f.Row.Envelope = bytes; break;
                case "fingerprint": f.Row.Fingerprint = bytes; break;
                case "row-terminal": f.Row.TerminalManifestSha256 = bytes; break;
                case "row-authority": f.Row.AuthoritySha256 = bytes; break;
                case "terminal-hash": f.Terminal.ManifestSha256 = bytes; break;
                case "key-material": f.Key.MaterialFingerprint = bytes; break;
                case "key-authority": f.Key.AuthoritySha256 = bytes; break;
                case "authority-hash": f.Authority = f.Authority with { Hash = bytes }; break;
            }
            Assert.Throws<InvalidOperationException>(f.Observe);
        }
    }

    private static byte[] Changed(byte[]? value)
    { var result = value?.ToArray() ?? new byte[32]; result[0] ^= 1; return result; }

    private sealed class Fixture
    {
        internal DateTimeOffset Now = new(2026, 10, 11, 7, 0, 0, TimeSpan.Zero);
        internal byte[] Raw = Enumerable.Range(1, 32).Select(x => (byte)x).ToArray();
        internal GroupTerminalAdvanceRecord Row;
        internal GroupTerminalAdvanceKeyRecord Key;
        internal GroupTerminalFrontierStateRecord Cursor;
        internal GroupBatchTerminalReceiptRecord Terminal;
        internal GroupFrontierRootRuntimeProof.Authority Authority;
        internal Fixture()
        {
            var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            Authority = new(scope, Guid.NewGuid(), 1, 2, 3, 0, 4, SHA256.HashData([99]));
            Terminal = new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                BatchId = Guid.NewGuid(),
                OperationId = Guid.NewGuid(),
                AfterSequence = 0,
                ThroughSequence = 500,
                ManifestSha256 = SHA256.HashData([42]),
                CommittedAtUtc = Now.AddHours(-1)
            };
            using var material = new GroupSourceKeyMaterial(GroupFrontierRootRuntimeProof.KeyId, Raw);
            var signed = GroupTerminalAdvanceIntegrity.Sign(new(scope, Terminal.BatchId, Terminal.OperationId, 0, 1, 0, 500,
                new byte[32], Terminal.ManifestSha256, Terminal.CommittedAtUtc, Now, Authority.Service, 1, 2, 3, 0, 4,
                Authority.Hash, material.KeyId), material);
            Row = GroupTerminalAdvanceRowProof.Stage(signed, null);
            Key = new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                KeyId = material.KeyId,
                FormatVersion = 1,
                MaterialFingerprint = GroupTerminalAdvanceKeyProof.Fingerprint(scope, material),
                EnrolledAtUtc = Now,
                ServiceId = Authority.Service,
                CredentialEpoch = 1,
                GrantVersion = 2,
                SourceVersion = 3,
                DeletionGeneration = 0,
                AccountVersion = 4,
                AuthoritySha256 = Authority.Hash.ToArray()
            };
            Cursor = new()
            {
                TenantId = scope.TenantId,
                CompanyId = scope.CompanyId,
                BindingId = scope.SourceBindingId,
                Version = 1,
                ThroughSequence = 500,
                LastTerminalBatchId = Terminal.BatchId,
                UpdatedAtUtc = Now
            };
        }
        internal void Observe() => GroupFrontierRootRuntimeProof.RequireValue(Row, Cursor, Key, Terminal, Authority, Now, Raw);
    }
}
