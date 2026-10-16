using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Shared.Contracts.Tests;

public sealed class GroupIngressDispatchReferenceTests
{
    private static GroupIngressDispatchReference Reference() => new(1,
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), Guid.NewGuid(), 2, 3);

    [Fact]
    public void ExactReferenceRoundtripsWithoutPortalIdentityContentOrAuthority()
    {
        var reference = Reference();
        var bytes = reference.ToBytes();
        Assert.Equal(reference, Parse(reference, bytes));
        Assert.InRange(bytes.Length, 1, GroupIngressDispatchReference.MaximumBytes);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(new[] { "version", "source", "eventId", "messageId", "revision", "committedSequence" },
            document.RootElement.EnumerateObject().Select(x => x.Name));
        Assert.Equal(new[] { "tenantId", "companyId", "sourceBindingId" },
            document.RootElement.GetProperty("source").EnumerateObject().Select(x => x.Name));
    }

    [Theory]
    [InlineData("type")]
    [InlineData("content-type")]
    [InlineData("message-id")]
    [InlineData("uppercase-id")]
    [InlineData("hyphenated-id")]
    public void ForeignBrokerDomainOrMessageIdentityCannotChooseSqlReference(string change)
    {
        var reference = Reference(); var id = reference.EventId.ToString("N");
        var type = change == "type" ? "minhhuy.work.v1" : GroupIngressDispatchReference.MessageType;
        var mime = change == "content-type" ? "text/plain" : "application/json";
        id = change switch
        {
            "message-id" => Guid.NewGuid().ToString("N"),
            "uppercase-id" => id.ToUpperInvariant(),
            "hyphenated-id" => reference.EventId.ToString("D"),
            _ => id
        };
        Assert.Equal("Group dispatch reference is not available.", Assert.Throws<InvalidOperationException>(() =>
            GroupIngressDispatchReference.Parse(reference.ToBytes(), type, id, mime)).Message);
    }

    [Theory]
    [InlineData("decoded-duplicate")]
    [InlineData("scope-duplicate")]
    [InlineData("unknown-content")]
    [InlineData("unknown-scope")]
    [InlineData("missing-version")]
    [InlineData("null-scope")]
    [InlineData("invalid-utf8")]
    [InlineData("oversized")]
    [InlineData("portal-task")]
    public void MalformedPrivateOrCrossKindPayloadIsRefusedBeforeDelivery(string change)
    {
        var reference = Reference(); var text = Encoding.UTF8.GetString(reference.ToBytes());
        var changed = change switch
        {
            "decoded-duplicate" => text.Insert(1, "\"v\\u0065rsion\":1,"),
            "scope-duplicate" => text.Replace("\"source\":{", "\"source\":{\"tenantId\":\"" + reference.Source.TenantId + "\",", StringComparison.Ordinal),
            "unknown-content" => text.Insert(1, "\"text\":\"owned-private-probe\","),
            "unknown-scope" => text.Replace("\"source\":{", "\"source\":{\"userId\":\"" + Guid.NewGuid() + "\",", StringComparison.Ordinal),
            "missing-version" => text.Replace("\"version\":1,", "", StringComparison.Ordinal),
            "null-scope" => text.Replace(JsonSerializer.Serialize(reference.Source, new JsonSerializerOptions(JsonSerializerDefaults.Web)), "null", StringComparison.Ordinal),
            "oversized" => text + new string(' ', GroupIngressDispatchReference.MaximumBytes),
            "portal-task" => "{\"tenantId\":\"" + reference.Source.TenantId + "\",\"taskId\":\"" + Guid.NewGuid() + "\"}",
            _ => text
        };
        var bytes = change == "invalid-utf8" ? new byte[] { 0x7B, 0xFF, 0x7D } : Encoding.UTF8.GetBytes(changed);
        var error = Assert.Throws<InvalidOperationException>(() => Parse(reference, bytes));
        Assert.Equal("Group dispatch reference is not available.", error.Message);
        Assert.DoesNotContain("owned-private-probe", error.ToString());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("scope")]
    [InlineData("event")]
    [InlineData("message")]
    [InlineData("revision")]
    [InlineData("sequence")]
    public void MissingOrFutureReferenceMetadataCannotSerializeAsCommitted(string change)
    {
        var reference = Reference();
        var changed = change switch
        {
            "version" => reference with { Version = 2 },
            "scope" => reference with { Source = reference.Source with { CompanyId = Guid.Empty } },
            "event" => reference with { EventId = Guid.Empty },
            "message" => reference with { MessageId = Guid.Empty },
            "revision" => reference with { Revision = 0 },
            _ => reference with { CommittedSequence = -1 }
        };
        Assert.Throws<InvalidOperationException>(() => changed.ToBytes());
    }

    private static GroupIngressDispatchReference Parse(GroupIngressDispatchReference reference, byte[] bytes) =>
        GroupIngressDispatchReference.Parse(bytes, GroupIngressDispatchReference.MessageType, reference.EventId.ToString("N"), "application/json");
}
