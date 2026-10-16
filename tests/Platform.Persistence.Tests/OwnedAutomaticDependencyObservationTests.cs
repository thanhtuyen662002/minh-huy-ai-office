extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedAutomaticDependencyObservationTests
{
    [Theory]
    [InlineData("TenantId")]
    [InlineData("CompanyId")]
    [InlineData("BindingId")]
    [InlineData("BatchId")]
    [InlineData("Epoch")]
    [InlineData("OwnerId")]
    [InlineData("OperationId")]
    [InlineData("IssuedAtUtc")]
    [InlineData("ExpiresAtUtc")]
    [InlineData("ExpiryObservedAtUtc")]
    public void CurrentCheckPinsEveryOriginalClaimStateField(string field)
    {
        var value = new GroupBatchClaimStateRecord
        {
            TenantId = Guid.NewGuid(),
            CompanyId = Guid.NewGuid(),
            BindingId = Guid.NewGuid(),
            BatchId = Guid.NewGuid(),
            Epoch = 3,
            OwnerId = Guid.NewGuid(),
            OperationId = Guid.NewGuid(),
            IssuedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2)
        };
        var original = GroupAutomaticDependencyRuntimeProof.StateFingerprint(value);
        var property = typeof(GroupBatchClaimStateRecord).GetProperty(field)!;
        object changed = property.PropertyType == typeof(Guid) ? Guid.NewGuid() : property.PropertyType == typeof(long) ? 4L
            : ((DateTimeOffset?)property.GetValue(value) ?? value.ExpiresAtUtc).AddTicks(1);
        property.SetValue(value, changed);
        Assert.NotEqual(original, GroupAutomaticDependencyRuntimeProof.StateFingerprint(value));
    }

    [Theory]
    [InlineData("GroupWorkCommitReceipts")]
    [InlineData("GroupWorkSourceDispositions")]
    [InlineData("GroupWorkRawDispositions")]
    [InlineData("GroupMessageRevisions")]
    [InlineData("extra")]
    public void SuccessfulObservationRequiresAllFourMaterialQueriesAndNoForeignCategory(string change)
    {
        var evidence = new GroupAutomaticDependencyRuntimeProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        evidence.Observed.UnionWith(["GroupWorkCommitReceipts", "GroupWorkSourceDispositions", "GroupWorkRawDispositions", "GroupMessageRevisions"]);
        evidence.RequireComplete();
        if (change == "extra") evidence.Observed.Add("GroupCustomerRequests"); else evidence.Observed.Remove(change);
        Assert.Throws<InvalidOperationException>(evidence.RequireComplete);
    }

    [Theory]
    [InlineData("GroupWorkCommitReceipts")]
    [InlineData("GroupWorkSourceDispositions")]
    [InlineData("GroupWorkRawDispositions")]
    [InlineData("GroupMessageRevisions")]
    public async Task ArmedMaterialQueryCannotRunWithoutTheActualSerializableTransaction(string table)
    {
        var evidence = new GroupAutomaticDependencyRuntimeProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())) { Armed = true };
        using var command = new SqlCommand("SELECT TOP(1) * FROM [aioffice].[" + table + "]");
        var interceptor = new GroupAutomaticDependencyRuntimeProof.ReadProbe(evidence);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await interceptor.ReaderExecutingAsync(command, null!, default));
        Assert.Empty(evidence.Observed); Assert.Null(command.Connection);
    }

    [Fact]
    public async Task InactiveAndUnrelatedQueriesCannotManufactureACompleteObservation()
    {
        var evidence = new GroupAutomaticDependencyRuntimeProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        var interceptor = new GroupAutomaticDependencyRuntimeProof.ReadProbe(evidence);
        using var command = new SqlCommand("SELECT TOP(1) * FROM [aioffice].[GroupWorkCommitReceipts]");
        await interceptor.ReaderExecutingAsync(command, null!, default);
        evidence.Armed = true; command.CommandText = "SELECT OBJECT_ID(N'[aioffice].[GroupWorkCommitReceipts]')";
        await interceptor.ReaderExecutingAsync(command, null!, default);
        Assert.Empty(evidence.Observed); Assert.Throws<InvalidOperationException>(evidence.RequireComplete);
        Assert.Equal(["GroupMessageRevisions"], GroupAutomaticDependencyRuntimeProof.MaterialTables("SELECT * FROM [aioffice].[GroupMessages] m JOIN\n[aioffice].[GroupMessageRevisions] r ON r.MessageId=m.Id"));
        Assert.Equal(["GroupWorkRawDispositions"], GroupAutomaticDependencyRuntimeProof.MaterialTables("select * from [aioffice].[groupworkrawdispositions]"));
        Assert.Empty(GroupAutomaticDependencyRuntimeProof.MaterialTables("SELECT * FROM [other].[GroupWorkRawDispositions]"));
        Assert.Null(command.Connection);
    }
}
