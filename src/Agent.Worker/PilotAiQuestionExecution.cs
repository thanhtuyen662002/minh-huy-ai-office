using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
public sealed class PilotAiQuestionToolPermissionProvider(PlatformDbContext dbContext,
    DataSourceSecretBindingService? bindingService = null)
    : IToolPermissionProvider
{
    private readonly DataSourceSecretBindingService bindings = bindingService ?? new(dbContext, new EfAuthorizationDirectory(dbContext));
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

        try
        {
            var authority = await bindings.TaskAuthorityAsync(request.TenantId, request.CompanyId, request.TaskId, cancellationToken);
            if (authority.UserId != request.UserId) return Array.Empty<ToolPermission>();
            await bindings.RequireSourceAsync(authority, dataSourceId, readOnly: true, cancellationToken);
        }
        catch (UnauthorizedAccessException) { return Array.Empty<ToolPermission>(); }

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
    PilotAiRuntimeDescriptor runtime,
    DataSourceSecretBindingService? bindingService = null,
    IErpQueryAgentBackend? queryAgentBackend = null,
    ILogger<PilotAiQuestionExecutor>? logger = null,
    AiQuestionUsageScope? usageScope = null,
    DataSourceConcurrencyLimiter? sourceLimiter = null) : IRawWorkStepExecutor
{
    /// <summary>
    /// The fixed, secret-free provider message (status code, provider error text) shown to the task owner
    /// so a failed answer says why. Connection strings and keys never reach these messages.
    /// </summary>
    private WorkStepExecutionResult ProviderFailure(WorkDispatchEnvelope envelope, AiProviderExecutionException exception, int maxAttempts)
    {
        logger?.LogWarning("Trợ lý task {TaskId} AI provider failure (transient={Transient}): {Detail}",
            envelope.TaskId, exception.IsTransient, exception.Message);
        return new WorkStepExecutionResult(
            WorkDeliveryOutcome.Failed,
            exception.IsTransient ? WorkFailureClass.Transient : WorkFailureClass.Permanent,
            maxAttempts,
            FailureDetail: exception.Message);
    }

    private WorkStepExecutionResult UnexpectedFailure(WorkDispatchEnvelope envelope, Exception exception, int maxAttempts)
    {
        // Exception text from drivers can name servers; only the type is shown, the log keeps the message.
        logger?.LogWarning(exception, "Trợ lý task {TaskId} failed with {Type}", envelope.TaskId, exception.GetType().Name);
        return new WorkStepExecutionResult(WorkDeliveryOutcome.Failed, WorkFailureClass.Transient, maxAttempts,
            FailureDetail: exception is ErpReadOnlyCheckTimeoutException ? ErpAccessFailure.CheckTimeout
                : $"Lỗi khi đọc ERP ({exception.GetType().Name}).");
    }

    private WorkStepExecutionResult Denied(WorkDispatchEnvelope envelope, UnauthorizedAccessException exception, int maxAttempts)
    {
        var detail = ErpAccessFailure.For(exception);
        logger?.LogWarning("Trợ lý task {TaskId} was refused ERP access: {Detail}", envelope.TaskId, detail);
        return new WorkStepExecutionResult(WorkDeliveryOutcome.Failed, WorkFailureClass.Authorization, maxAttempts, FailureDetail: detail);
    }

    private readonly DataSourceSecretBindingService bindings = bindingService ?? new(dbContext, new EfAuthorizationDirectory(dbContext));
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

        AuthorizedDataSourceBinding initialBinding;
        try
        {
            var authority = await bindings.TaskAuthorityAsync(envelope.TenantId, envelope.CompanyId, envelope.TaskId, cancellationToken);
            initialBinding = await bindings.RequireSourceAsync(authority, request.DataSourceId, readOnly: true, cancellationToken);
        }
        catch (UnauthorizedAccessException error)
        {
            return Denied(envelope, error, request.MaxAttempts);
        }
        if (usageScope is not null)
            usageScope.Current = new AiUsageContext(envelope.TenantId, envelope.CompanyId, envelope.TaskId,
                initialBinding.Authority.UserId, request.DataSourceId, "assistant");

        // Hold one of the source's concurrent slots for the whole question (connection check, schema reads, queries).
        using var slot = sourceLimiter is null ? null : await sourceLimiter.TryEnterAsync(envelope.TenantId, envelope.CompanyId,
            request.DataSourceId, await dbContext.DataSources.AsNoTracking()
                .Where(item => item.TenantId == envelope.TenantId && item.CompanyId == envelope.CompanyId && item.Id == request.DataSourceId)
                .Select(item => item.MaxConcurrency).SingleAsync(cancellationToken).ConfigureAwait(false),
            DataSourceConcurrencyLimiter.DefaultWait, cancellationToken).ConfigureAwait(false);
        if (sourceLimiter is not null && slot is null)
        {
            logger?.LogWarning("Trợ lý task {TaskId} waited too long for a free slot on data source {SourceId}", envelope.TaskId, request.DataSourceId);
            return new WorkStepExecutionResult(WorkDeliveryOutcome.Failed, WorkFailureClass.Transient, request.MaxAttempts,
                FailureDetail: "Nguồn ERP đang bận trả lời nhiều câu hỏi cùng lúc. Hệ thống sẽ tự thử lại.");
        }

        var probe = new PilotDataSourceProbeExecutor(dbContext, secretResolver, connectionProbe, bindings);
        var probeResult = await probe.ExecuteAsync(envelope, lease, cancellationToken).ConfigureAwait(false);
        if (probeResult.Outcome is not WorkDeliveryOutcome.Completed)
        {
            logger?.LogWarning("Trợ lý task {TaskId} stopped at the ERP connection check ({Class}): {Detail}",
                envelope.TaskId, probeResult.FailureClass, probeResult.FailureDetail);
            return probeResult;
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
                request.MaxAttempts,
                FailureDetail: ErpAccessFailure.SourceUnavailable);
        }

        if (queryAgentBackend is not null)
        {
            return await ExecuteQueryAgentAsync(envelope, request, initialBinding, source, queryAgentBackend, cancellationToken)
                .ConfigureAwait(false);
        }

        PilotErpEvidence erpEvidence;
        try
        {
            var authority = await bindings.TaskAuthorityAsync(envelope.TenantId, envelope.CompanyId, envelope.TaskId, cancellationToken);
            if (authority.UserId != initialBinding.Authority.UserId) throw DataSourceSecretBindingService.Unavailable();
            await bindings.RevalidateAsync(initialBinding, readOnly: true, cancellationToken);
            erpEvidence = await new ScopedDataSourceSecretResolver(bindings, secretResolver).UseAsync(
                authority, request.DataSourceId, readOnly: true,
                async (connectionString, token) => await evidenceReader.ReadAsync(connectionString, token), cancellationToken, taskId: envelope.TaskId);
        }
        catch (UnauthorizedAccessException error)
        {
            return Denied(envelope, error, request.MaxAttempts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return UnexpectedFailure(envelope, exception, request.MaxAttempts);
        }

        var (questionText, pictures) = await QuestionAsync(envelope, initialBinding.Authority.UserId, request.Question, cancellationToken)
            .ConfigureAwait(false);
        var aiRequest = new AiGatewayRequest(
            envelope.TenantId.ToString("N"),
            envelope.CompanyId.ToString("N"),
            envelope.TaskId.ToString("N"),
            $"pilot-ai:{envelope.TaskId:N}:{envelope.StepId:N}",
            AiCapability.Reasoning,
            BuildBoundedInput(questionText, erpEvidence))
        { Images = pictures };

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
            return ProviderFailure(envelope, exception, request.MaxAttempts);
        }
        catch (InvalidOperationException exception)
        {
            return ProviderFailure(envelope, new AiProviderExecutionException($"AI provider is not usable: {exception.Message}", false, exception), request.MaxAttempts);
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

    /// <summary>
    /// Agent mode: the model may search the schema and run validated, rollback-only SELECTs on the
    /// same read-only connection; every query it ran is recorded in the checkpoint.
    /// </summary>
    private async Task<WorkStepExecutionResult> ExecuteQueryAgentAsync(
        WorkDispatchEnvelope envelope,
        PilotDataSourceWorkRequest request,
        AuthorizedDataSourceBinding initialBinding,
        DataSourceRecord source,
        IErpQueryAgentBackend backend,
        CancellationToken cancellationToken)
    {
        PilotErpEvidence erpEvidence = null!;
        ErpQueryAgentResult outcome;
        var (questionText, pictures) = await QuestionAsync(envelope, initialBinding.Authority.UserId, request.Question, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var authority = await bindings.TaskAuthorityAsync(envelope.TenantId, envelope.CompanyId, envelope.TaskId, cancellationToken);
            if (authority.UserId != initialBinding.Authority.UserId) throw DataSourceSecretBindingService.Unavailable();
            await bindings.RevalidateAsync(initialBinding, readOnly: true, cancellationToken);
            outcome = await new ScopedDataSourceSecretResolver(bindings, secretResolver).UseAsync(
                authority, request.DataSourceId, readOnly: true,
                async (connectionString, token) =>
                {
                    erpEvidence = await evidenceReader.ReadAsync(connectionString, token);
                    return await backend.UseAsync(
                        connectionString,
                        session => new ErpQueryAgent(aiGateway).RunAsync(envelope, questionText, erpEvidence, session, token, pictures),
                        token);
                }, cancellationToken, taskId: envelope.TaskId);
        }
        catch (UnauthorizedAccessException error)
        {
            return Denied(envelope, error, request.MaxAttempts);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AiProviderExecutionException exception)
        {
            return ProviderFailure(envelope, exception, request.MaxAttempts);
        }
        catch (Exception exception)
        {
            return UnexpectedFailure(envelope, exception, request.MaxAttempts);
        }

        var totalTokens = checked(outcome.InputTokens + outcome.OutputTokens);
        var checkpoint = new
        {
            status = "completed",
            dataSourceId = source.Id,
            logicalName = source.LogicalName,
            questionLength = request.Question.Length,
            answer = outcome.Answer,
            provider = outcome.ProviderId ?? runtime.ProviderId,
            model = string.IsNullOrWhiteSpace(outcome.Model) ? runtime.ModelId : outcome.Model,
            usage = new
            {
                inputTokens = outcome.InputTokens,
                outputTokens = outcome.OutputTokens,
                totalTokens
            },
            billingStatus = "usage-observed-not-settled",
            modelTurns = outcome.ModelTurns,
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
            erpQueries = outcome.Queries.Select(query => new
            {
                purpose = query.Purpose,
                sql = query.Sql,
                rowCount = query.RowCount,
                truncated = query.Truncated,
                error = query.Error,
                elapsedMs = query.ElapsedMilliseconds
            }),
            evidence = "ai-agent-read-only-erp-queries"
        };

        return new WorkStepExecutionResult(
            WorkDeliveryOutcome.Completed,
            null,
            request.MaxAttempts,
            1,
            JsonSerializer.Serialize(checkpoint, JsonOptions));
    }

    /// <summary>
    /// The question without picture tokens, and the attached pictures the asker uploaded. A picture that is gone
    /// (deleted or not the asker's) is skipped, and the model is told so.
    /// </summary>
    private async Task<(string Text, IReadOnlyList<AiImageInput> Images)> QuestionAsync(
        WorkDispatchEnvelope envelope, Guid askerUserId, string question, CancellationToken cancellationToken)
    {
        var (text, ids) = AssistantImageTokens.Parse(question);
        if (ids.Count == 0) return (question, []);
        var stored = await AssistantService.ForTaskAsync(dbContext, envelope.TenantId, envelope.CompanyId, askerUserId, ids, cancellationToken)
            .ConfigureAwait(false);
        if (text.Length == 0) text = "(Người hỏi chỉ gửi ảnh, không kèm câu chữ: hãy xem ảnh và trả lời điều ảnh cho thấy.)";
        if (stored.Count < ids.Count) text += $" ({ids.Count - stored.Count} ảnh đính kèm không còn trên hệ thống.)";
        logger?.LogInformation("Trợ lý task {TaskId} carries {Count} picture(s)", envelope.TaskId, stored.Count);
        return (text, stored.Select(image => new AiImageInput(image.ContentType, image.Content)).ToArray());
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
