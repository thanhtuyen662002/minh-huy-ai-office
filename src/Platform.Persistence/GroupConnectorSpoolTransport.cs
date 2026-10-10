using System.Security.Cryptography;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// One host-configured encryption key, not a selector from a retained file,
// customer event, HTTP request or model. Signing uses a separate reference.
public sealed record GroupConnectorSpoolKeyBinding(Guid TenantId, Guid CompanyId, Guid ConnectorAccountId,
    Guid ServiceId, string KeyId, SecretReference Reference);

// This operation joins the exact retained capture to its authenticated HTTP
// commit reply. A public receipt DTO cannot delete a file through this API.
// The host must obtain fresh backend enrollment/lease before each invocation.
// There is no provider, enrollment fabrication, polling or DI activation here.
public sealed class GroupConnectorSpoolTransport
{
    private readonly GroupConnectorFileSpool spool;
    private readonly GroupConnectorTransportClient transport;
    private readonly GroupConnectorSpoolKeyBinding binding;
    private readonly CompositeSecretResolver secrets;
    private readonly TimeProvider clock;

    public GroupConnectorSpoolTransport(GroupConnectorFileSpool spool, GroupConnectorTransportClient transport,
        GroupConnectorSpoolKeyBinding binding, CompositeSecretResolver secrets, TimeProvider clock)
    {
        if (spool is null || transport is null || binding is null || secrets is null || clock is null ||
            binding.TenantId == Guid.Empty || binding.CompanyId == Guid.Empty || binding.ConnectorAccountId == Guid.Empty || binding.ServiceId == Guid.Empty ||
            binding.Reference is null || string.IsNullOrEmpty(binding.KeyId) || binding.KeyId.Length > 64 ||
            binding.KeyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) throw new GroupConnectorTransportException();
        transport.RequireSpoolBinding(binding);
        this.spool = spool; this.transport = transport; this.binding = binding; this.secrets = secrets; this.clock = clock;
    }

    public async Task<GroupIngressCommittedReceipt> ReplayAsync(GroupSpoolItemReference reference, GroupConnectorEnrollment current,
        GroupListenerLeaseSnapshot lease, CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        byte[]? key = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (reference?.Context is not { } context || context.Source.TenantId != binding.TenantId || context.Source.CompanyId != binding.CompanyId ||
                context.ConnectorAccountId != binding.ConnectorAccountId || context.ServiceId != binding.ServiceId || context.KeyId != binding.KeyId)
                throw new GroupConnectorTransportException();
            // Includes the actual transport's Live/owned-fixture policy and
            // fixed service epoch, before disk load, key resolution or decrypt.
            transport.RequireRecovery(context, current, lease);
            var stored = spool.Load(reference);
            var encoded = await secrets.ResolveAsync(binding.Reference, linked.Token).AsTask().WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (encoded.Length != 44) throw new GroupConnectorTransportException();
            key = Convert.FromBase64String(encoded);
            if (key.Length != 32 || Convert.ToBase64String(key) != encoded) throw new GroupConnectorTransportException();
            var admission = transport.Recover(stored, key, current, lease);
            using var prepared = await transport.PrepareEventAsync(admission, linked.Token);
            var committed = await transport.SendEventAsync(prepared, linked.Token);
            // Cancellation/unknown transport leaves every retained byte intact.
            // The deletion binds the original file reference, not rewrapped
            // ownership or an independently supplied receipt.
            linked.Token.ThrowIfCancellationRequested();
            spool.Acknowledge(reference, committed);
            return committed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new GroupConnectorTransportException(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException)
        { if (error is GroupConnectorTransportException known) throw known; throw new GroupConnectorTransportException(); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
}
