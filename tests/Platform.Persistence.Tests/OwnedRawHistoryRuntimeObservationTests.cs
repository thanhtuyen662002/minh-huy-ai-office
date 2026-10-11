extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedRawHistoryRuntimeObservationTests
{
    [Fact]
    public void OwnedHistoryRequiresExactlyTwoMessagesAnd501OrderedOriginalReferences()
    {
        var (scope, values) = References();
        GroupRawHistoryRuntimeProof.RequireReferences(scope, values[0].Id, values);
        Assert.Equal(499, values[499].Revision);
        Assert.Equal(500, values[500].Revision);
        Assert.Equal(values[0].MessageId, values[500].MessageId);
        Assert.NotEqual(values[0].MessageId, values[1].MessageId);
    }

    [Theory]
    [InlineData("empty-operation")]
    [InlineData("foreign-operation")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("sequence-gap")]
    [InlineData("extra-message")]
    [InlineData("wrong-revision")]
    [InlineData("empty-event")]
    [InlineData("duplicate-event")]
    [InlineData("foreign-tenant")]
    [InlineData("foreign-company")]
    [InlineData("foreign-binding")]
    public void OwnedHistoryRefusesIncompleteForeignOrMalformedReferenceGraphs(string fault)
    {
        var (scope, values) = References(); var operation = values[0].Id;
        switch (fault)
        {
            case "empty-operation": operation = Guid.Empty; break;
            case "foreign-operation": operation = Guid.NewGuid(); break;
            case "missing": values.RemoveAt(500); break;
            case "extra": values.Add(new()); break;
            case "sequence-gap": values[499].CommittedSequence = 501; break;
            case "extra-message": values[499].MessageId = Guid.NewGuid(); break;
            case "wrong-revision": values[500].Revision = 499; break;
            case "empty-event": values[500].Id = Guid.Empty; break;
            case "duplicate-event": values[500].Id = values[0].Id; break;
            case "foreign-tenant": values[500].TenantId = Guid.NewGuid(); break;
            case "foreign-company": values[500].CompanyId = Guid.NewGuid(); break;
            case "foreign-binding": values[500].BindingId = Guid.NewGuid(); break;
            default: throw new InvalidOperationException();
        }
        Assert.Throws<InvalidOperationException>(() => GroupRawHistoryRuntimeProof.RequireReferences(scope, operation, values));
    }

    private static (GroupScope Scope, List<GroupIngressOutboxRecord> Values) References()
    {
        var scope = new GroupScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        return (scope, Enumerable.Range(0, 501).Select(index => new GroupIngressOutboxRecord
        {
            TenantId = scope.TenantId,
            CompanyId = scope.CompanyId,
            BindingId = scope.SourceBindingId,
            Id = Guid.NewGuid(),
            MessageId = index == 1 ? second : first,
            Revision = index < 2 ? 1 : index,
            CommittedSequence = index + 1
        }).ToList());
    }
}
