namespace MinhHuyAiOffice.Shared.Contracts;

// Opaque runtime handle only. Implementing this interface, or presenting a
// profile/hash, does not authorize a provider call or any SQL/source effect.
public interface IAiPreparedStructuredRequest { }

public interface IAiStructuredRequestAdmission
{
    Task<IAiPreparedStructuredRequest> PrepareAsync(AiGatewayRequest request, CancellationToken cancellationToken = default);
    AiStructuredAdmissionSnapshot DescribePrepared(IAiPreparedStructuredRequest prepared, CancellationToken cancellationToken = default);
    Task<AiGatewayResponse> ExecutePreparedAsync(IAiPreparedStructuredRequest prepared, CancellationToken cancellationToken = default);
}
