using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using MinhHuyAiOffice.Shared.Contracts;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Agent.Worker;

public sealed record PilotAiRuntimeDescriptor(string ProviderId, string ModelId);

/// <summary>
/// Trusted scope for the bounded AI pilot. The operation remains anchored to one server-resolved,
/// read-only ERP data source; prompt text cannot widen the selected resource or action.
/// </summary>
public sealed class PilotAiQuestionToolMetadataProvider(PlatformDbContext dbContext)
    : ITrustedToolExecutionMetadataProvider
{
    public async Task<TrustedToolExecutionMetadata> GetAsync(
        WorkDispatchEnvelope envelope,
        WorkLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);

        var request = await PilotDataSourceWorkRequest.LoadAsync(dbContext, envelope, cancellationToken);
        return new TrustedToolExecutionMetadata(
            $"erp-data-source:{request.DataSourceId:N}",
            "ai-reasoning-readonly",
            ToolRiskLevel.Low);
    }
}

/// <summary>
/// Authorizes only the explicitly bounded AI/read-only pilot operation and only for an active
/// company-scoped data source that is readable and not writable.
/// </summary>
public sealed class PilotAiQuestionToolPermissionProvider(PlatformDbContext dbContext)
    : IToolPermissionProvider
{
    public async Task<IReadOnlyCollection<ToolPermission>> GetAsync(
        ToolAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Action, "ai-reasoning-readonly", StringComparison.Ordinal)
            || !TryReadDataSourceId(request.Resource, out var dataSourceId))
        {
            return Array.Empty<ToolPermission>();
        }

        var source = await dbContext.DataSources
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.TenantId == request.TenantId
                && item.CompanyId == request.CompanyId
                && item.Id == dataSourceId
                && item.IsEnabled
                && item.AllowRead
                && !item.AllowWrite
                && item.MaxConcurrency >= 1
                && item.MaxConcurrency <= 1024,
                cancellationToken);

        if (source is null || !SecretReference.TryParse(source.ConnectionSecretReference, out _))
        {
            return Array.Empty<ToolPermission>();
        }

        return new[]
        {
            new ToolPermission(
                request.TenantId,
                request.CompanyId,
                request.UserId,
                request.Resource,
                request.Action,
                ToolRiskLevel.Low)
        };
    }

    private static bool TryReadDataSourceId(string resource, out Guid dataSourceId)
    {
        const string prefix = "erp-data-source:";
        dataSourceId = Guid.Empty;
        return resource.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParse(resource[prefix.Length..], out dataSourceId)
            && dataSourceId != Guid.Empty;
    }
}

/// <summary>
/// Model-backed pilot executor grounded by bounded, server-authored read-only ERP catalog evidence.
/// Customer text never becomes SQL. Connection strings and secret references never enter model context,
/// and business row contents remain outside this first grounded slice.
/// </summary>
public sealed class PilotAiQuestionExecutor(
    PlatformDbContext dbContext,
    CompositeSecretResolver secretResolver,
    IDataSourceConnectionProbe connectionProbe,
    IPilotErpEvidenceReader evidenceReader,
    IAiGateway aiGateway,
    PilotAiRuntimeDescriptor runtime) : IRawWorkStepExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkStepExecutionResult> ExecuteAsync(
        WorkDispatchEnvelope envelope,
        WorkLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);

        var probe = new PilotDataSourceProbeExecutor(dbContext, secretResolver, connectionProbe);
        var probeResult = await probe.ExecuteAsync(envelope, lease, cancellationToken).ConfigureAwait(false);
        if (probeResult.Outcome is not WorkDeliveryOutcome.Completed)
        {
            return probeResult;
        }

        PilotDataSourceWorkRequest request;
        try
        {
            request = await PilotDataSourceWorkRequest.LoadAsync(dbContext, envelope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return new WorkStepExecutionResult(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Validation,
                1);
        }

        var source = await dbContext.DataSources
            .AsNoTracking()
            .SingleAsync(item =>
                item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.Id == request.DataSourceId,
                cancellationToken)
            .ConfigureAwait(false);

        if (!SecretReference.TryParse(source.ConnectionSecretReference, out var secretReference))
        {
            return new WorkStepExecutionResult(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Authorization,
                request.MaxAttempts);
        }

        PilotErpEvidence erpEvidence;
        try
        {
            var connectionString = await secretResolver
                .ResolveAsync(secretReference!, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new WorkStepExecutionResult(
                    WorkDeliveryOutcome.Failed,
                    WorkFailureClass.Transient,
                    request.MaxAttempts);
            }

            erpEvidence = await evidenceReader
                .ReadAsync(connectionString, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new WorkStepExecutionResult(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Transient,
                request.MaxAttempts);
        }

        var aiRequest = new AiGatewayRequest(
            envelope.TenantId.ToString("N"),
            envelope.CompanyId.ToString("N"),
            envelope.TaskId.ToString("N"),
            $"pilot-ai:{envelope.TaskId:N}:{envelope.StepId:N}",
            AiCapability.Reasoning,
            BuildBoundedInput(request.Question, erpEvidence));

        AiGatewayResponse response;
        try
        {
            response = await aiGateway.ExecuteAsync(aiRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AiProviderExecutionException exception)
        {
            return new WorkStepExecutionResult(
                WorkDeliveryOutcome.Failed,
                exception.IsTransient ? WorkFailureClass.Transient : WorkFailureClass.Permanent,
                request.MaxAttempts);
        }
        catch (InvalidOperationException)
        {
            return new WorkStepExecutionResult(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Permanent,
                request.MaxAttempts);
        }

        var totalTokens = checked(response.InputTokens + response.OutputTokens);
        var checkpoint = new
        {
            status = "completed",
            dataSourceId = source.Id,
            logicalName = source.LogicalName,
            questionLength = request.Question.Length,
            answer = response.Output,
            provider = response.ProviderId ?? runtime.ProviderId,
            model = response.Model,
            usage = new
            {
                inputTokens = response.InputTokens,
                outputTokens = response.OutputTokens,
                totalTokens
            },
            billingStatus = "usage-observed-not-settled",
            erpEvidence = new
            {
                databaseName = erpEvidence.DatabaseName,
                tableCount = erpEvidence.TableCount,
                sampledTableCount = erpEvidence.TopTables.Count,
                topTables = erpEvidence.TopTables.Select(table => new
                {
                    schema = table.SchemaName,
                    table = table.TableName,
                    approximateRowCount = table.ApproximateRowCount
                })
            },
            evidence = "ai-provider-reasoning-after-bounded-read-only-erp-catalog"
        };

        return new WorkStepExecutionResult(
            WorkDeliveryOutcome.Completed,
            null,
            request.MaxAttempts,
            1,
            JsonSerializer.Serialize(checkpoint, JsonOptions));
    }

    private static string BuildBoundedInput(string question, PilotErpEvidence evidence)
    {
        evidence.Validate();
        var tableEvidence = evidence.TopTables.Count == 0
            ? "(no non-system tables returned)"
            : string.Join(
                Environment.NewLine,
                evidence.TopTables.Select(table =>
                    $"- {table.SchemaName}.{table.TableName}: approximateRows={table.ApproximateRowCount}"));

        return $"""
        You are the bounded reasoning agent for Minh Huy AI Office.
        The following evidence was retrieved from the authorized ERP SQL Server by a fixed,
        server-authored read-only catalog query. Customer text and model output were not used as SQL.

        Database: {evidence.DatabaseName}
        Non-system table count: {evidence.TableCount}
        Top tables by approximate row count (maximum {PilotErpEvidence.MaximumTables}):
        {tableEvidence}

        Use only this evidence and the user's question. Do not invent database values. Do not claim
        that business row contents were read: this slice exposes catalog names and approximate row
        counts only. If the question requires balances, invoices, inventory quantities, customer data
        or other business values, say that a separately authorized business-query capability is needed.

        User question:
        {question}
        """;
    }
}
