using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MinhHuy.AIOffice.Platform.Persistence;
using MinhHuyAiOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Agent.Worker;

/// <summary>The question being answered in this delivery scope, set by the executor once the asker is known.</summary>
public sealed class AiQuestionUsageScope
{
    public AiUsageContext? Current { get; set; }
}

/// <summary>
/// Every model call goes through here: the customer's budget is checked first (a long agent run stops as soon as
/// the budget is spent) and each answered call is written to the usage ledger with its tokens and price.
/// </summary>
public sealed class MeteredAiGateway(IAiGateway inner, AiQuestionUsageScope scope, AiUsageRecorder ledger, ILogger<MeteredAiGateway> logger)
    : IAiGateway
{
    public async Task<AiGatewayResponse> ExecuteAsync(AiGatewayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var context = scope.Current ?? Fallback(request);
        try
        {
            await ledger.RequireBudgetAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (AiUsageLimitException limit)
        {
            throw new AiProviderExecutionException(limit.Message, false, limit);
        }

        var response = await inner.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            // The provider has already charged for this call: record it even if the question is being cancelled.
            await ledger.RecordAsync(context, response.ProviderId ?? "unknown", response.Model,
                response.InputTokens, response.CachedInputTokens, response.OutputTokens, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            logger.LogError(error, "AI usage for task {TaskId} ({Input} in / {Output} out tokens, model {Model}) could not be recorded",
                context.TaskId, response.InputTokens, response.OutputTokens, response.Model);
        }
        return response;
    }

    private static AiUsageContext Fallback(AiGatewayRequest request) => new(
        Guid.Parse(request.TenantId), Guid.Parse(request.CompanyId),
        Guid.TryParse(request.TaskId, out var task) ? task : null, null, null, "unscoped");
}

/// <summary>
/// Caps how many questions run against one ERP data source at the same time (its "Đồng thời" setting), so many
/// people asking at once cannot overload a customer's database. The worker is a single process, so an in-memory
/// gate is exact; the slot is held for the whole question.
/// </summary>
public sealed class DataSourceConcurrencyLimiter
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(90);
    private readonly ConcurrentDictionary<(Guid Tenant, Guid Company, Guid Source, int Capacity), SemaphoreSlim> gates = new();

    /// <summary>A slot, or null when none freed up within <paramref name="wait"/>.</summary>
    public async Task<IDisposable?> TryEnterAsync(Guid tenantId, Guid companyId, Guid sourceId, int capacity, TimeSpan wait,
        CancellationToken token)
    {
        var size = Math.Clamp(capacity, 1, 1024);
        var gate = gates.GetOrAdd((tenantId, companyId, sourceId, size), _ => new SemaphoreSlim(size, size));
        return await gate.WaitAsync(wait, token).ConfigureAwait(false) ? new Slot(gate) : null;
    }

    private sealed class Slot(SemaphoreSlim gate) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) gate.Release();
        }
    }
}
