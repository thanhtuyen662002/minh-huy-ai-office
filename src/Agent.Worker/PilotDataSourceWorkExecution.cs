using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuy.AIOffice.Shared.Contracts;
using Platform.Persistence;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>
/// Supplies the only tool scope accepted by the first pilot executor. The resource is derived
/// from the durable request event, never from broker text or a customer question.
/// </summary>
public sealed class PilotDataSourceToolMetadataProvider(PlatformDbContext dbContext)
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
            "connection-test",
            ToolRiskLevel.Low);
    }
}

/// <summary>
/// Grants the read-only pilot operation only when the durable task's data source is enabled,
/// company-scoped and explicitly readable. The permission is reconstructed from SQL state for
/// every attempt; no role or company authority is accepted from the broker payload.
/// </summary>
public sealed class PilotDataSourceToolPermissionProvider(PlatformDbContext dbContext)
    : IToolPermissionProvider
{
    public async Task<IReadOnlyCollection<ToolPermission>> GetAsync(
        ToolAuthorizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.Equals(request.Action, "connection-test", StringComparison.Ordinal)
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
/// Executes a deterministic, read-only SQL connectivity probe for the approved pilot data source.
/// It returns only a redacted checkpoint. A real AI/provider executor can be added later behind the
/// same authorization and audit boundary without changing task authority.
/// </summary>
public sealed class PilotDataSourceProbeExecutor(
    PlatformDbContext dbContext,
    CompositeSecretResolver secretResolver,
    IDataSourceConnectionProbe connectionProbe)
    : IRawWorkStepExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<WorkStepExecutionResult> ExecuteAsync(
        WorkDispatchEnvelope envelope,
        WorkLeaseSnapshot lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(lease);

        PilotDataSourceWorkRequest request;
        try
        {
            request = await PilotDataSourceWorkRequest.LoadAsync(dbContext, envelope, cancellationToken);
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
            .SingleOrDefaultAsync(item =>
                item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.Id == request.DataSourceId,
                cancellationToken);

        if (source is null || !source.IsEnabled || !source.AllowRead
            || source.MaxConcurrency is < 1 or > 1024
            || !SecretReference.TryParse(source.ConnectionSecretReference, out var reference))
        {
            return new WorkStepExecutionResult(
                WorkDeliveryOutcome.Failed,
                WorkFailureClass.Authorization,
                request.MaxAttempts);
        }

        string connectionString;
        try
        {
            connectionString = await secretResolver.ResolveAsync(reference!, cancellationToken);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return new WorkStepExecutionResult(
                    WorkDeliveryOutcome.Failed,
                    WorkFailureClass.Transient,
                    request.MaxAttempts);
            }
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

        try
        {
            await connectionProbe.ProbeAsync(connectionString, cancellationToken);
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

        var checkpoint = new
        {
            status = "connected",
            dataSourceId = source.Id,
            logicalName = source.LogicalName,
            questionLength = request.Question.Length,
            aiCredits = 0,
            evidence = "read-only-connection-probe"
        };

        return new WorkStepExecutionResult(
            WorkDeliveryOutcome.Completed,
            null,
            request.MaxAttempts,
            1,
            JsonSerializer.Serialize(checkpoint, JsonOptions));
    }
}

internal sealed record PilotDataSourceWorkRequest(
    string IdempotencyKey,
    Guid DataSourceId,
    string Question,
    int MaxAttempts)
{
    public static async Task<PilotDataSourceWorkRequest> LoadAsync(
        PlatformDbContext dbContext,
        WorkDispatchEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var payload = await dbContext.TaskEvents
            .AsNoTracking()
            .Where(item => item.TenantId == envelope.TenantId
                && item.CompanyId == envelope.CompanyId
                && item.TaskId == envelope.TaskId
                && item.StepId == envelope.StepId
                && item.EventType == PilotTaskRequestEvent.EventType)
            .OrderBy(item => item.Sequence)
            .Select(item => item.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Durable pilot task request event is missing.");

        var request = JsonSerializer.Deserialize<PilotTaskRequestEvent>(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Durable pilot task request event is empty.");
        request.Validate();

        return new PilotDataSourceWorkRequest(
            request.IdempotencyKey,
            request.DataSourceId,
            request.Question,
            request.MaxAttempts);
    }
}
