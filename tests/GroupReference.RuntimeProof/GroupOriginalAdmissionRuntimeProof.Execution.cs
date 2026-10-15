extern alias RuntimeWorker;

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuyAiOffice.Shared.Contracts;
using RuntimeWorker::MinhHuy.AIOffice.Agent.Worker;

namespace MinhHuy.AIOffice.GroupReference.RuntimeProof;

internal static partial class GroupOriginalAdmissionRuntimeProof
{
    // Actual committed owned-SQL permits + the actual existing Gateway, but
    // an explicitly synthetic in-process adapter. No HTTP/model evaluation.
    private static (GroupModelAttemptAdmissionPreparer Issuer, AdmittedExecutionAdapter Adapter) ExecutionIssuer(
        PlatformDbContext db, TimeProvider clock, AdmittedExecutionSqlProbe probe,
        GroupAutomaticInputPlan.RestoredChunk chunk)
    {
        var adapter = new AdmittedExecutionAdapter(db, probe, Output(chunk.Current.Preparation));
        var gateway = new OrderedFailoverAiGateway(adapter); var metadata = gateway.DescribeStructuredGateway(); var route = metadata.Routes.Single();
        var request = chunk.Current.ModelContext!.CreateGatewayRequest(chunk.Original.OperationId);
        var candidate = new StructuredModelProfileCandidate(new AiModelProfile(route.ProviderId, route.ConfiguredModel,
            new HashSet<AiCapability> { AiCapability.StructuredGeneration }, 1, 0, 1), metadata.MetadataSha256, 0,
            route.EndpointIdentitySha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ResponseSchema!))),
            StructuredTextTokenCounter.Version, 128000, 1024, 2048, [route.ConfiguredModel], Profile);
        var gate = new StructuredModelAdmission(gateway, StructuredModelProfileCatalog.ForSyntheticControls(candidate),
            new BoundedStructuredTextTokenCounter(), clock);
        return (new(db, gate, clock), adapter);
    }

    private static async Task<GroupModelAttemptResponse> RequireAdmittedExecutionAsync(PlatformDbContext db,
        GroupExtractionWorkerBinding worker, GroupNoteRuntimeProof.OwnedClock clock, GroupBatchSourceReader sources,
        GroupBrainCurrentReader brain, GroupModelAttemptAdmissionPreparer issuer, AdmittedExecutionAdapter adapter,
        AdmittedExecutionSqlProbe probe, GroupAutomaticInputPlan plan, GroupAutomaticInputPlan.RestoredChunk chunk,
        GroupModelAttemptPermit permit, CancellationToken token)
    {
        var journal = new GroupModelAttemptJournalStore(db, worker, clock, sources, brain, issuer);
        var budget = await journal.ReadOriginalBudgetAsync(plan, chunk, token);
        if (budget.Disposition != GroupModelAttemptDisposition.Wait || budget.ConsumedAttempts != 1 || adapter.Executions != 0)
            throw new InvalidOperationException();
        Phase = "admitted-execution-current"; probe.Armed = true;
        try
        {
            var response = await journal.ExecuteAdmittedOriginalAsync(permit, token);
            probe.RequireCompleted(); RequireClean(db);
            if (!ReferenceEquals(response.Permit, permit) || response.Disposition != GroupModelAttemptResponseDisposition.Parsed
                || response.Observed?.ProviderId != AdmittedExecutionAdapter.Provider || adapter.Executions != 1)
                throw new InvalidOperationException();
            Phase = "admitted-execution-replay";
            await RefusedAsync(() => journal.ExecuteAdmittedOriginalAsync(permit, token));
            if (adapter.Executions != 1) throw new InvalidOperationException();
            RequireClean(db); return response;
        }
        finally { probe.Armed = false; }
    }

    private static async Task RequireAcceptedExecutionRefusedAsync(PlatformDbContext db, GroupExtractionWorkerBinding worker,
        GroupNoteRuntimeProof.OwnedClock clock, GroupBatchSourceReader sources, GroupBrainCurrentReader brain,
        GroupModelAttemptAdmissionPreparer issuer, AdmittedExecutionAdapter adapter, AdmittedExecutionSqlProbe probe,
        GroupModelAttemptPermit permit, CancellationToken token)
    {
        Phase = "admitted-execution-accepted-refusal"; var reads = probe.AfterCompanionReads; probe.Armed = true;
        probe.Accepted.Begin();
        try
        {
            await RefusedAsync(() => new GroupModelAttemptJournalStore(db, worker, clock, sources, brain, issuer)
                .ExecuteAdmittedOriginalAsync(permit, token));
            if (adapter.Executions != 1 || probe.AfterCompanionReads <= reads) throw new InvalidOperationException();
            Phase = "admitted-execution-accepted-transaction";
            probe.Accepted.RequireRefused(db); RequireClean(db);
        }
        finally { probe.Accepted.End(); probe.Armed = false; }
    }

    private sealed class AdmittedExecutionAdapter(PlatformDbContext db, AdmittedExecutionSqlProbe probe, string output)
        : IAiProviderAdapter, IAiStructuredProviderMetadataSource
    {
        internal const string Provider = "owned-synthetic-execution";
        internal int Executions { get; private set; }
        public string ProviderId => Provider;
        public bool Supports(AiCapability capability) => capability == AiCapability.StructuredGeneration;
        public AiStructuredRouteMetadata DescribeStructuredRoute() => new(Provider, Provider, new('E', 64), 2048,
            TimeSpan.FromSeconds(60), new("owned-synthetic-execution-v1", 128000, 32000, 64000, 262144, 32, 10, 4));
        public async Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe.ProviderStarted(IsOutsideSql()); Executions++;
            // Exercise an actual asynchronous boundary without a provider,
            // external transport, fake clock advance or SQL lock held over it.
            await Task.Yield(); cancellationToken.ThrowIfCancellationRequested();
            probe.ProviderReturned(IsOutsideSql());
            return new(request.RequestId, request.Capability, output, Provider, 123, 7) { ProviderId = Provider };
        }
        private bool IsOutsideSql() => db.Database.CurrentTransaction is null && System.Transactions.Transaction.Current is null
            && !db.ChangeTracker.HasChanges() && db.Database.GetDbConnection().State == ConnectionState.Closed;
    }

    // Test-only observation, never a SQL/Gateway capability or a production
    // authorization decision. Strings are inspected in memory and not logged.
    internal sealed class AdmittedExecutionSqlProbe : DbCommandInterceptor
    {
        internal readonly AcceptedTransactionObservation Accepted = new();
        internal bool Armed;
        internal int BeforeCompanionReads { get; private set; }
        internal int AfterCompanionReads { get; private set; }
        private bool released, returned;
        internal void ObserveCommand(string command)
        {
            if (!Armed) return;
            if (released && !returned) throw Unavailable();
            if (!command.Contains("FROM aioffice.GroupModelAttemptAdmissions", StringComparison.Ordinal)) return;
            if (released) AfterCompanionReads++; else BeforeCompanionReads++;
        }
        internal void ProviderStarted(bool outsideSql)
        {
            if (!Armed || !outsideSql || released || BeforeCompanionReads == 0) throw Unavailable();
            released = true;
        }
        internal void ProviderReturned(bool outsideSql)
        {
            if (!Armed || !outsideSql || !released || returned) throw Unavailable();
            returned = true;
        }
        internal void RequireCompleted()
        { if (!released || !returned || BeforeCompanionReads == 0 || AfterCompanionReads == 0) throw Unavailable(); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Accepted.Query(command.Transaction, command.CommandText); ObserveCommand(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { ObserveCommand(command.CommandText); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<object> result, CancellationToken cancellationToken = default)
        { ObserveCommand(command.CommandText); return ValueTask.FromResult(result); }
        private static InvalidOperationException Unavailable() => new("Owned admitted execution observation is not available.");
    }
}
