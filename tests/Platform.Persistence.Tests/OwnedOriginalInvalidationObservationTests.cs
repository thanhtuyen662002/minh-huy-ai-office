extern alias GroupReferenceProof;

using System.Data;
using System.Data.Common;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;
using Xunit;
using CommitObservation = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalInvalidationRuntimeProof.CheckpointObservation;
using RefusalObservation = GroupReferenceProof::MinhHuy.AIOffice.GroupReference.RuntimeProof.GroupOriginalInvalidationRuntimeProof.ActivityRefusalObservation;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

// Pure controls for the actual runtime observer state machines. These callbacks
// do not authenticate a SQL transaction, prove a commit or run an owner.
public sealed class OwnedOriginalInvalidationObservationTests
{
    private static readonly GroupScope Scope = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    private static readonly Guid Batch = Guid.NewGuid(), Operation = Guid.NewGuid();
    private const string PlanHash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ActivitySql = "SELECT CASE WHEN EXISTS (SELECT 1 FROM [aioffice].[GroupOriginalInvalidations]) THEN 1 ELSE 0 END";

    [Fact]
    public void OwnedCheckpointOnlyCompleteOrderedCallbacksCanProduceTheDeliberateReplyLoss()
    {
        var evidence = Begin(); var row = Row(); evidence.Saving(row, 1); evidence.Saved(row, 1); evidence.Committing();
        var error = Assert.ThrowsAny<IOException>(() => evidence.Committed());
        Assert.Null(error.InnerException); evidence.End(); evidence.RequireLostReply(true);
    }

    [Theory]
    [InlineData("unarmed")]
    [InlineData("save-without-saving")]
    [InlineData("commit-before-save")]
    [InlineData("committed-before-committing")]
    [InlineData("saving-twice")]
    [InlineData("saved-twice")]
    [InlineData("committing-twice")]
    [InlineData("arm-twice")]
    public void MissingDuplicateOrReorderedCheckpointCallbacksRefuse(string fault)
    {
        var evidence = fault == "unarmed" ? new CommitObservation() : Begin(); var row = Row();
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (fault)
            {
                case "unarmed": evidence.Saving(row, 1); break;
                case "save-without-saving": evidence.Saved(row, 1); break;
                case "commit-before-save": evidence.Committing(); break;
                case "committed-before-committing": evidence.Committed(); break;
                case "saving-twice": evidence.Saving(row, 1); evidence.Saving(row, 1); break;
                case "saved-twice": evidence.Saving(row, 1); evidence.Saved(row, 1); evidence.Saved(row, 1); break;
                case "committing-twice": evidence.Saving(row, 1); evidence.Saved(row, 1); evidence.Committing(); evidence.Committing(); break;
                default: evidence.Begin(Scope, Batch, Operation, 1, PlanHash); break;
            }
        });
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("company")]
    [InlineData("binding")]
    [InlineData("batch")]
    [InlineData("operation")]
    [InlineData("epoch")]
    [InlineData("plan")]
    [InlineData("digest")]
    [InlineData("mixed-write")]
    public void AnotherScopeClaimPlanOrMixedWriteCannotQualifyAnObservedCheckpoint(string fault)
    {
        var evidence = Begin(); var row = Row(); var count = 1;
        switch (fault)
        {
            case "tenant": row.TenantId = Guid.NewGuid(); break;
            case "company": row.CompanyId = Guid.NewGuid(); break;
            case "binding": row.BindingId = Guid.NewGuid(); break;
            case "batch": row.BatchId = Guid.NewGuid(); break;
            case "operation": row.ClaimOperationId = Guid.NewGuid(); break;
            case "epoch": row.ClaimEpoch = 2; break;
            case "plan": row.PlanSha256 = new string('C', 64); break;
            case "digest": row.CheckpointSha256 = new string('D', 64); break;
            case "mixed-write": count = 2; break;
        }
        if (fault != "digest") row.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(row);
        Assert.Throws<InvalidOperationException>(() => evidence.Saving(row, count));
    }

    [Theory]
    [InlineData("row-changed")]
    [InlineData("zero-saved")]
    [InlineData("mixed-saved")]
    [InlineData("reply-not-caught")]
    [InlineData("no-commit")]
    public void ChangedFlushedBytesOrIncompleteReplyEvidenceNeverQualifies(string fault)
    {
        var evidence = Begin(); var row = Row(); evidence.Saving(row, 1);
        if (fault == "row-changed")
        {
            row.ObservedDependenciesSha256 = new string('C', 64); row.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(row);
            Assert.Throws<InvalidOperationException>(() => evidence.Saved(row, 1)); return;
        }
        if (fault is "zero-saved" or "mixed-saved")
        { Assert.Throws<InvalidOperationException>(() => evidence.Saved(row, fault == "zero-saved" ? 0 : 2)); return; }
        evidence.Saved(row, 1);
        if (fault == "reply-not-caught")
        {
            evidence.Committing(); Assert.ThrowsAny<IOException>(() => evidence.Committed()); evidence.End();
            Assert.Throws<InvalidOperationException>(() => evidence.RequireLostReply(false)); return;
        }
        evidence.End(); Assert.Throws<InvalidOperationException>(() => evidence.RequireLostReply(true));
    }

    [Fact]
    public async Task ActualTrueScalarConsumptionAndDisposalQualifiesOnlyItsOwnInvocation()
    {
        using var f = new ActivityFixture(); var observation = Dispatch(f);
        await using (var reader = observation.Executed(f.Command, f.Transaction, Values(true)))
        {
            Assert.True(await reader.ReadAsync()); Assert.True(reader.GetBoolean(0)); Assert.False(await reader.ReadAsync());
        }
        observation.End(); observation.RequireRefused(true);
        observation.Begin(Batch); observation.End(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(true));
    }

    [Theory]
    [InlineData("query-only")]
    [InlineData("dispatch-only")]
    [InlineData("command-failed-before-result")]
    [InlineData("command-failed-after-result")]
    [InlineData("no-value-consumption")]
    [InlineData("no-end-of-rows")]
    [InlineData("reader-still-open")]
    public async Task DispatchOrMappedStoreUnavailableNeverSubstitutesForSuccessfulTrueRead(string fault)
    {
        using var f = new ActivityFixture(); var observation = new RefusalObservation(); observation.Begin(Batch);
        observation.Query(ActivitySql);
        if (fault == "query-only")
        { observation.End(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(true)); return; }
        observation.Dispatched(f.Command, f.Transaction);
        if (fault == "dispatch-only" || fault == "command-failed-before-result")
        {
            if (fault == "command-failed-before-result") observation.CommandFailed();
            observation.End(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(true)); return;
        }
        var reader = observation.Executed(f.Command, f.Transaction, Values(true));
        try
        {
            Assert.True(await reader.ReadAsync());
            if (fault != "no-value-consumption") Assert.True(reader.GetBoolean(0));
            if (fault != "no-end-of-rows") Assert.False(await reader.ReadAsync());
            if (fault != "reader-still-open") await reader.DisposeAsync();
            if (fault == "command-failed-after-result") observation.CommandFailed();
            observation.End(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(true));
        }
        finally { await reader.DisposeAsync(); }
    }

    [Theory]
    [InlineData("false")]
    [InlineData("missing")]
    [InlineData("multiple")]
    [InlineData("null")]
    [InlineData("nonboolean")]
    [InlineData("two-columns")]
    [InlineData("another-result")]
    public async Task FalseMissingMultipleOrNonBooleanResultsAreDelegatedAndNeverQualify(string fault)
    {
        using var f = new ActivityFixture(); var observation = Dispatch(f);
        using var table = new DataTable();
        table.Columns.Add("Value", fault == "nonboolean" ? typeof(int) : typeof(bool));
        if (fault == "two-columns") table.Columns.Add("Unexpected", typeof(bool));
        if (fault != "missing") table.Rows.Add(fault == "null" ? DBNull.Value : fault == "nonboolean" ? 1 : fault != "false");
        if (fault == "multiple") table.Rows.Add(true);
        using var second = new DataTable(); second.Columns.Add("Value", typeof(bool)); second.Rows.Add(true);
        var inner = fault == "another-result" ? new DataTableReader([table, second]) : table.CreateDataReader();
        await using (var reader = observation.Executed(f.Command, f.Transaction, inner))
        {
            while (await reader.ReadAsync())
            {
                var actual = reader.GetValue(0);
                if (fault == "false") Assert.Equal(false, actual);
                if (fault == "null") Assert.Equal(DBNull.Value, actual);
                if (fault == "nonboolean") Assert.Equal(1, actual);
            }
            if (fault == "another-result") Assert.True(await reader.NextResultAsync());
        }
        observation.End(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(true));
    }

    [Theory]
    [InlineData("other-command")]
    [InlineData("other-transaction")]
    [InlineData("changed-command-transaction")]
    [InlineData("changed-command-text")]
    [InlineData("duplicate-dispatch")]
    [InlineData("duplicate-executed")]
    public void SuccessfulResultCallbackMustMatchTheExactDispatchedCommandAndTransaction(string fault)
    {
        using var f = new ActivityFixture(); using var other = new ActivityFixture(); var observation = Dispatch(f);
        using var reader = Values(true);
        Assert.Throws<InvalidOperationException>(() =>
        {
            switch (fault)
            {
                case "other-command": observation.Executed(other.Command, f.Transaction, reader); break;
                case "other-transaction": observation.Executed(f.Command, other.Transaction, reader); break;
                case "changed-command-transaction": f.Command.Transaction = other.Transaction; observation.Executed(f.Command, other.Transaction, reader); break;
                case "changed-command-text": f.Command.CommandText = "SELECT CAST(1 AS bit)"; observation.Executed(f.Command, f.Transaction, reader); break;
                case "duplicate-dispatch": observation.Dispatched(f.Command, f.Transaction); break;
                default:
                    using (observation.Executed(f.Command, f.Transaction, reader)) { }
                    observation.Executed(f.Command, f.Transaction, reader); break;
            }
        });
        observation.End(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(true));
    }

    [Theory]
    [InlineData("read")]
    [InlineData("scalar")]
    [InlineData("async-scalar")]
    [InlineData("wrong-ordinal")]
    public async Task ActualDelegatedReaderFailuresRemainFailuresEvenIfAnOwnerMapsTheException(string fault)
    {
        using var f = new ActivityFixture(); var observation = Dispatch(f); var inner = Values(true);
        await using (var reader = observation.Executed(f.Command, f.Transaction, inner))
        {
            if (fault == "read")
            { inner.Close(); await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync()); }
            else
            {
                Assert.True(await reader.ReadAsync());
                if (fault == "wrong-ordinal") Assert.NotNull(Record.Exception(() => reader.GetBoolean(1)));
                else
                {
                    inner.Close();
                    if (fault == "async-scalar") await Assert.ThrowsAsync<InvalidOperationException>(() => reader.GetFieldValueAsync<bool>(0));
                    else Assert.Throws<InvalidOperationException>(() => reader.GetBoolean(0));
                }
            }
        }
        observation.End(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(true));
    }

    [Theory]
    [InlineData("object")]
    [InlineData("generic")]
    [InlineData("async-generic")]
    public async Task OtherActualScalarConsumptionApisPreserveTheSameTrueValue(string api)
    {
        using var f = new ActivityFixture(); var observation = Dispatch(f);
        await using (var reader = observation.Executed(f.Command, f.Transaction, Values(true)))
        {
            Assert.True(await reader.ReadAsync());
            if (api == "object") Assert.Equal(true, reader.GetValue(0));
            else if (api == "generic") Assert.True(reader.GetFieldValue<bool>(0));
            else Assert.True(await reader.GetFieldValueAsync<bool>(0));
            Assert.False(await reader.ReadAsync());
        }
        observation.End(); observation.RequireRefused(true);
    }

    [Theory]
    [InlineData("unarmed")]
    [InlineData("no-query")]
    [InlineData("wrong-query")]
    [InlineData("query-no-exists")]
    [InlineData("original-content")]
    [InlineData("protected-content")]
    [InlineData("committed")]
    [InlineData("not-refused")]
    [InlineData("double-arm")]
    [InlineData("empty-batch")]
    public async Task MissingAmbiguousOrMutatingActivityEvidenceCannotQualifyRefusal(string fault)
    {
        using var f = new ActivityFixture(); var observation = new RefusalObservation();
        if (fault is "empty-batch" or "double-arm" or "query-no-exists" or "original-content" or "protected-content")
        {
            if (fault != "empty-batch") observation.Begin(Batch);
            Assert.Throws<InvalidOperationException>(() =>
            {
                if (fault is "empty-batch" or "double-arm") observation.Begin(fault == "empty-batch" ? Guid.Empty : Batch);
                else observation.Query(fault == "query-no-exists" ? "SELECT * FROM [aioffice].[GroupOriginalInvalidations]"
                    : fault == "original-content" ? "SELECT [Plan] FROM aioffice.GroupAutomaticInputPlans"
                    : "SELECT ProtectedContent FROM aioffice.GroupMessageRevisions");
            });
            return;
        }
        if (fault != "unarmed") observation.Begin(Batch);
        if (fault is "committed" or "not-refused")
        {
            observation.Query(ActivitySql); observation.Dispatched(f.Command, f.Transaction);
            await using (var reader = observation.Executed(f.Command, f.Transaction, Values(true)))
            { Assert.True(await reader.ReadAsync()); Assert.True(reader.GetBoolean(0)); Assert.False(await reader.ReadAsync()); }
            if (fault == "committed") observation.Committed();
        }
        else if (fault == "wrong-query") observation.Query("SELECT 1");
        observation.End(); Assert.Throws<InvalidOperationException>(() => observation.RequireRefused(fault != "not-refused"));
    }

    private static RefusalObservation Dispatch(ActivityFixture fixture)
    {
        var observation = new RefusalObservation(); observation.Begin(Batch); observation.Query(ActivitySql);
        observation.Dispatched(fixture.Command, fixture.Transaction); return observation;
    }
    private static DataTableReader Values(params bool[] values)
    {
        var table = new DataTable(); table.Columns.Add("Value", typeof(bool));
        foreach (var value in values) table.Rows.Add(value); return table.CreateDataReader();
    }
    private sealed class ActivityFixture : IDisposable
    {
        private readonly BindingStorePermissionTests.SyntheticConnection connection = new(1);
        internal DbTransaction Transaction { get; }
        internal DbCommand Command { get; }
        internal ActivityFixture()
        {
            connection.Open(); Transaction = connection.BeginTransaction(IsolationLevel.Serializable);
            Command = connection.CreateCommand(); Command.CommandText = ActivitySql; Command.Transaction = Transaction;
        }
        public void Dispose() { Command.Dispose(); Transaction.Dispose(); connection.Dispose(); }
    }

    private static CommitObservation Begin()
    { var observation = new CommitObservation(); observation.Begin(Scope, Batch, Operation, 1, PlanHash); return observation; }
    private static GroupOriginalInvalidationRecord Row()
    {
        var row = new GroupOriginalInvalidationRecord
        {
            TenantId = Scope.TenantId,
            CompanyId = Scope.CompanyId,
            BindingId = Scope.SourceBindingId,
            BatchId = Batch,
            FormatVersion = 1,
            PlanSha256 = PlanHash,
            OriginalClaimOperationId = Operation,
            OriginalClaimEpoch = 1,
            ClaimOperationId = Operation,
            ClaimEpoch = 1,
            ObservedDependenciesSha256 = new string('B', 64),
            CreatedAtUtc = new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero)
        };
        row.CheckpointSha256 = GroupOriginalInvalidationProof.Digest(row); return row;
    }
}
