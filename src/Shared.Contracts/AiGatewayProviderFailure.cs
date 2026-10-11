namespace MinhHuyAiOffice.Shared.Contracts;

// Host-owned Gateway failure metadata only. A caller must still distinguish
// cancellation/local faults and verify current authority before any durable write.
// Exception messages, provider payloads and inner exceptions are not this contract.
public interface IAiGatewayProviderFailure
{
    bool IsTransient { get; }
}
