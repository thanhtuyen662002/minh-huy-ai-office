extern alias GroupReferenceProof;

using GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof;
using Microsoft.Data.SqlClient;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class OwnedOriginalEffectGraphObservationTests
{
    private static readonly string[] Tables = ["GroupBatchClaimReceipts", "GroupCustomerRequests", "GroupRequestRevisions",
        "GroupRequestEvidence", "GroupNotesCommittedOutbox", "GroupNotesCommittedItems"];

    [Theory]
    [InlineData("GroupBatchClaimReceipts")]
    [InlineData("GroupCustomerRequests")]
    [InlineData("GroupRequestRevisions")]
    [InlineData("GroupRequestEvidence")]
    [InlineData("GroupNotesCommittedOutbox")]
    [InlineData("GroupNotesCommittedItems")]
    [InlineData("extra")]
    [InlineData("metadata")]
    [InlineData("payload")]
    public void CurrentEffectObservationRequiresAllSixCategoriesAndBothOrderedRevisionReads(string fault)
    {
        var evidence = Complete(); evidence.RequireEffectsComplete();
        if (fault == "extra") evidence.ObservedEffects.Add("GroupMessageRevisions");
        else if (fault == "metadata") evidence.RevisionMetadataObserved = false;
        else if (fault == "payload") evidence.RevisionPayloadObserved = false;
        else evidence.ObservedEffects.Remove(fault);
        Assert.Throws<InvalidOperationException>(evidence.RequireEffectsComplete);
    }

    [Theory]
    [InlineData("GroupBatchClaimReceipts")]
    [InlineData("GroupCustomerRequests")]
    [InlineData("GroupRequestRevisions")]
    [InlineData("GroupRequestEvidence")]
    [InlineData("GroupNotesCommittedOutbox")]
    [InlineData("GroupNotesCommittedItems")]
    public async Task EachAddedCategoryRefusesBeforeReaderWithoutSerializableTransaction(string table)
    {
        var evidence = new GroupAutomaticDependencyRuntimeProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())) { Armed = true };
        using var command = new SqlCommand("SELECT TOP(1) * FROM [aioffice].[" + table + "]");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await new GroupAutomaticDependencyRuntimeProof.ReadProbe(evidence)
            .ReaderExecutingAsync(command, null!, default));
        Assert.Empty(evidence.Observed); Assert.Empty(evidence.ObservedEffects); Assert.Null(command.Connection);
        Assert.False(evidence.RevisionMetadataObserved); Assert.False(evidence.RevisionPayloadObserved);
    }

    [Theory]
    [InlineData("GroupBatchClaimReceipts")]
    [InlineData("GroupCustomerRequests")]
    [InlineData("GroupRequestRevisions")]
    [InlineData("GroupRequestEvidence")]
    [InlineData("GroupNotesCommittedOutbox")]
    [InlineData("GroupNotesCommittedItems")]
    public void OnlyActualAiofficeFromOrJoinCanObserveAddedTables(string table)
    {
        Assert.Equal([table], GroupAutomaticDependencyRuntimeProof.EffectTables("select * from [aioffice].[" + table.ToLowerInvariant() + "]"));
        Assert.Equal([table], GroupAutomaticDependencyRuntimeProof.EffectTables("SELECT * FROM [aioffice].[GroupMessages] m JOIN\n[aioffice].[" + table + "] e ON 1=0"));
        Assert.Empty(GroupAutomaticDependencyRuntimeProof.EffectTables("SELECT * FROM [other].[" + table + "]"));
        Assert.Empty(GroupAutomaticDependencyRuntimeProof.EffectTables("SELECT OBJECT_ID(N'[aioffice].[" + table + "]')"));
    }

    [Fact]
    public void RevisionPayloadCannotBeObservedBeforeActualLengthQuery()
    {
        var evidence = new GroupAutomaticDependencyRuntimeProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<InvalidOperationException>(() => evidence.ObserveEffects("SELECT [r].[ProtectedContent] FROM [aioffice].[GroupRequestRevisions] r"));
        Assert.Empty(evidence.ObservedEffects); Assert.False(evidence.RevisionPayloadObserved);
        evidence.ObserveEffects("SELECT DATALENGTH([r].[ProtectedContent]) FROM [aioffice].[GroupRequestRevisions] r");
        Assert.True(evidence.RevisionMetadataObserved); Assert.False(evidence.RevisionPayloadObserved);
        Assert.Throws<InvalidOperationException>(evidence.RequireEffectsComplete);
        evidence.ObserveEffects("SELECT [r].[ProtectedContent] FROM [aioffice].[GroupRequestRevisions] r");
        Assert.True(evidence.RevisionPayloadObserved);
    }

    [Fact]
    public void OriginalFourCategorySuccessCannotManufactureOriginalEffectGraphSuccess()
    {
        var evidence = new GroupAutomaticDependencyRuntimeProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        evidence.Observed.UnionWith(["GroupWorkCommitReceipts", "GroupWorkSourceDispositions", "GroupWorkRawDispositions", "GroupMessageRevisions"]);
        evidence.RequireComplete(); Assert.Throws<InvalidOperationException>(evidence.RequireEffectsComplete);
        evidence.ObserveEffects("SELECT DATALENGTH([r].[ProtectedContent]) FROM [other].[GroupRequestRevisions] r");
        Assert.False(evidence.RevisionMetadataObserved); Assert.Empty(evidence.ObservedEffects);
    }

    private static GroupAutomaticDependencyRuntimeProof.Evidence Complete()
    {
        var evidence = new GroupAutomaticDependencyRuntimeProof.Evidence(new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        evidence.ObserveEffects("SELECT DATALENGTH([r].[ProtectedContent]) FROM [aioffice].[GroupRequestRevisions] r");
        foreach (var table in Tables) evidence.ObserveEffects("SELECT * FROM [aioffice].[" + table + "]");
        return evidence;
    }
}
