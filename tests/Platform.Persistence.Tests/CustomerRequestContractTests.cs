using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class CustomerRequestContractTests
{
    internal static CustomerRequestInput Sample() => new("summary-001", "customer-group-1", "Khách hàng A", "Customer A", "Lan",
        "Không lưu được đơn hàng", DateTimeOffset.Parse("2026-10-26T01:00:00Z"), DateTimeOffset.Parse("2026-10-26T02:00:00Z"),
        [new("Sửa lỗi lưu đơn", "high", "bug")], [new("Không lưu được", "msg-123", "Lan", DateTimeOffset.Parse("2026-10-26T01:15:00Z"))], [], "Khách mong muốn ngày mai");
    internal static byte[] Bytes(CustomerRequestInput? input = null) => JsonSerializer.SerializeToUtf8Bytes(input ?? Sample(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void BridgeMigrationIsAdditiveAndCompanyKeysAndSqlRowVersionAreMapped()
    {
        using var db = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>().UseSqlServer("Server=localhost;Database=ModelOnly;Integrated Security=true").Options);
        var entity = db.Model.FindEntityType(typeof(CustomerRequestRecord))!;
        Assert.Equal("aioffice", entity.GetSchema());
        Assert.Equal(new[] { "TenantId", "CompanyId", "Id" }, entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(["TenantId", "CompanyId", "IdempotencyKey"]));
        Assert.Contains(entity.GetIndexes(), x => x.IsUnique && x.Properties.Select(p => p.Name).SequenceEqual(["TenantId", "CompanyId", "Code"]));
        var version = entity.FindProperty("RowVersion")!;
        Assert.True(version.IsConcurrencyToken); Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
        Assert.Equal("rowversion", version.GetColumnType());
        var assembly = db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Single(x => x.Key.EndsWith("_AddCustomerRequestBridge", StringComparison.Ordinal)).Value, "Microsoft.EntityFrameworkCore.SqlServer");
        var tables = migration.UpOperations.OfType<CreateTableOperation>().ToArray();
        Assert.Equal(7, tables.Length);
        Assert.All(tables, x => { Assert.Equal("aioffice", x.Schema); Assert.StartsWith("CustomerRequest", x.Name); });
        Assert.DoesNotContain(migration.UpOperations, x => x is DropTableOperation or AlterColumnOperation or DropColumnOperation);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void ContractPreservesSourceEvidenceAndCustomerDesireWithoutCommitment()
    {
        var parsed = CustomerRequestJson.ReadInput(Bytes());
        Assert.Equal("Khách hàng A", parsed.SourceGroupName); Assert.Equal("customer-group-1", parsed.SourceGroupExternalId);
        Assert.Equal(Sample().CustomerDesiredDueText, parsed.CustomerDesiredDueText);
        Assert.Equal("msg-123", Assert.Single(parsed.Evidence).SourceMessageId);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("company")]
    [InlineData("origin")]
    [InlineData("status")]
    [InlineData("missing")]
    [InlineData("case")]
    [InlineData("empty")]
    [InlineData("long-summary")]
    [InlineData("long-group")]
    [InlineData("long-due")]
    [InlineData("items-null")]
    [InlineData("item-null")]
    [InlineData("items-empty")]
    [InlineData("items-many")]
    [InlineData("evidence-many")]
    [InlineData("questions-many")]
    [InlineData("question-null")]
    [InlineData("priority")]
    [InlineData("kind")]
    [InlineData("item-field")]
    [InlineData("from-offset")]
    [InlineData("from-after-to")]
    [InlineData("evidence-time")]
    [InlineData("opaque-padding")]
    public void InvalidInputsCannotReachPersistence(string change)
    {
        var node = JsonNode.Parse(Bytes())!;
        switch (change)
        {
            case "unknown": node["unexpected"] = true; break;
            case "company": node["companyId"] = Guid.NewGuid(); break;
            case "origin": node["origin"] = "human_confirmed"; break;
            case "status": node["status"] = "done"; break;
            case "missing": node.AsObject().Remove("summary"); break;
            case "case": node["Summary"] = "extra"; break;
            case "empty": node["customerName"] = "   "; break;
            case "long-summary": node["summary"] = new string('x', 8001); break;
            case "long-group": node["sourceGroupExternalId"] = new string('x', 201); break;
            case "long-due": node["customerDesiredDueText"] = new string('x', 1001); break;
            case "items-null": node["items"] = null; break;
            case "item-null": node["items"] = new JsonArray((JsonNode?)null); break;
            case "items-empty": node["items"] = new JsonArray(); break;
            case "items-many": node["items"] = new JsonArray(Enumerable.Range(0, 51).Select(_ => node["items"]![0]!.DeepClone()).ToArray()); break;
            case "evidence-many": node["evidence"] = new JsonArray(Enumerable.Range(0, 101).Select(_ => node["evidence"]![0]!.DeepClone()).ToArray()); break;
            case "questions-many": node["openQuestions"] = new JsonArray(Enumerable.Range(0, 51).Select(_ => JsonValue.Create("Question") as JsonNode).ToArray()); break;
            case "question-null": node["openQuestions"] = new JsonArray((JsonNode?)null); break;
            case "priority": node["items"]![0]!["priority"] = "urgent"; break;
            case "kind": node["items"]![0]!["kind"] = "execute"; break;
            case "item-field": node["items"]![0]!["assigneeUserId"] = Guid.NewGuid(); break;
            case "from-offset": node["sourceWindowFrom"] = "2026-10-26T08:00:00+07:00"; break;
            case "from-after-to": node["sourceWindowFrom"] = "2026-10-27T01:00:00Z"; break;
            case "evidence-time": node["evidence"]![0]!["messageTimestamp"] = "2026-10-26T08:15:00+07:00"; break;
            case "opaque-padding": node["idempotencyKey"] = "summary-001 "; break;
        }
        Assert.ThrowsAny<Exception>(() => CustomerRequestJson.ReadInput(Encoding.UTF8.GetBytes(node.ToJsonString())));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"rowVersion\":\"AAAAAAAAAAA=\",\"status\":\"done\",\"status\":\"new\"}")]
    [InlineData("{\"rowVersion\":\"AAAAAAAAAAA=\",\"status\":\"human_confirmed\"}")]
    [InlineData("{\"rowVersion\":\"AAAAAAAAAAA=\",\"status\":null}")]
    [InlineData("{\"rowVersion\":\"AAAAAAAAAAA=\"}")]
    [InlineData("{\"status\":\"done\"}")]
    [InlineData("{\"rowVersion\":\"bad\",\"status\":\"done\"}")]
    [InlineData("{\"rowVersion\":\"AAAAAAAAAAA=\",\"origin\":\"human_confirmed\",\"status\":\"done\"}")]
    [InlineData("{\"rowVersion\":\"AAAAAAAAAAA=\",\"assigneeUserId\":\"00000000-0000-0000-0000-000000000000\"}")]
    public void PatchRejectsInvalidOrExtraProperties(string json) =>
        Assert.ThrowsAny<Exception>(() => CustomerRequestJson.ReadPatch(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void DuplicateNestedPropertiesAndInvalidUtf8AreRefused()
    {
        var json = Encoding.UTF8.GetString(Bytes()).Replace("\"priority\":\"high\"", "\"priority\":\"high\",\"priority\":\"low\"", StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => CustomerRequestJson.ReadInput(Encoding.UTF8.GetBytes(json)));
        Assert.Throws<DecoderFallbackException>(() => CustomerRequestJson.ReadInput(new byte[] { 0xFF }));
        Assert.Throws<ArgumentException>(() => CustomerRequestJson.ReadInput(new byte[65537]));
    }

    [Fact]
    public void PatchDistinguishesOmittedAndExplicitNullAssignee()
    {
        Assert.False(CustomerRequestJson.ReadPatch(Encoding.UTF8.GetBytes("{\"rowVersion\":\"AAAAAAAAAAA=\",\"status\":\"done\"}")).HasAssignee);
        var patch = CustomerRequestJson.ReadPatch(Encoding.UTF8.GetBytes("{\"rowVersion\":\"AAAAAAAAAAA=\",\"assigneeUserId\":null}"));
        Assert.True(patch.HasAssignee); Assert.Null(patch.AssigneeUserId);
    }
}
