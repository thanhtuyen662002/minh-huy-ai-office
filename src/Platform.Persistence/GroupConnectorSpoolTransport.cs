using System.Security.Cryptography;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// One host-configured encryption key, not a selector from a retained file,
// customer event, HTTP request or model. Signing uses a separate reference.
public sealed record GroupConnectorSpoolKeyBinding(Guid TenantId, Guid CompanyId, Guid ConnectorAccountId,
    Guid ServiceId, string KeyId, SecretReference Reference);

// Constructed only after the current backend Renew and exact event ACK. A
// recovery host retains the actual latest lease rather than an older snapshot.
public sealed class GroupConnectorReplayCommit
{
    internal GroupConnectorReplayCommit(GroupIngressCommittedReceipt receipt, GroupListenerLeaseSnapshot lease)
    { Receipt = receipt; Lease = lease; }
    public GroupIngressCommittedReceipt Receipt { get; }
    public GroupListenerLeaseSnapshot Lease { get; }
}

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
        if (!spool.IsBoundTo(new(binding.TenantId, binding.CompanyId, binding.ConnectorAccountId), binding.ServiceId))
            throw new GroupConnectorTransportException();
        this.spool = spool; this.transport = transport; this.binding = binding; this.secrets = secrets; this.clock = clock;
    }

    public Task<GroupIngressCommittedReceipt> ReplayAsync(GroupSpoolItemReference reference, GroupConnectorEnrollment current,
        GroupListenerLeaseSnapshot lease, CancellationToken cancellationToken = default) =>
        ReplayCoreAsync(reference, current, lease, null, cancellationToken);

    private async Task<GroupIngressCommittedReceipt> ReplayCoreAsync(GroupSpoolItemReference reference, GroupConnectorEnrollment current,
        GroupListenerLeaseSnapshot lease, DateTimeOffset? authorityCheckedAtUtc, CancellationToken cancellationToken)
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
            RequireFreshMetadata(authorityCheckedAtUtc);
            transport.RequireRecovery(context, current, lease);
            var stored = spool.Load(reference);
            var encoded = await secrets.ResolveAsync(binding.Reference, linked.Token).AsTask().WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (encoded.Length != 44) throw new GroupConnectorTransportException();
            key = Convert.FromBase64String(encoded);
            if (key.Length != 32 || Convert.ToBase64String(key) != encoded) throw new GroupConnectorTransportException();
            RequireFreshMetadata(authorityCheckedAtUtc);
            var admission = transport.Recover(stored, key, current, lease);
            using var prepared = await transport.PrepareEventAsync(admission, linked.Token);
            var committed = await transport.SendEventAsync(prepared, linked.Token);
            // Cancellation/unknown transport leaves every retained byte intact.
            // The deletion binds the original file reference, not rewrapped
            // ownership or an independently supplied receipt.
            linked.Token.ThrowIfCancellationRequested();
            RequireFreshMetadata(authorityCheckedAtUtc);
            spool.Acknowledge(reference, committed);
            return committed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new GroupConnectorTransportException(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException)
        { if (error is GroupConnectorTransportException known) throw known; throw new GroupConnectorTransportException(); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    // Operational recovery fetches current backend enrollment and renews the
    // actual owned lease before disk load or encryption-key resolution. A stale
    // mechanical snapshot cannot enter this path. Each call owns one deadline.
    public async Task<GroupIngressCommittedReceipt> ReplayWithCurrentAuthorityAsync(GroupSpoolItemReference reference,
        GroupConnectorEnrollmentRequest request, GroupListenerLeaseSnapshot ownedLease, CancellationToken cancellationToken = default) =>
        (await ReplayWithCurrentAuthorityAndLeaseAsync(reference, request, ownedLease, cancellationToken)).Receipt;

    public async Task<GroupConnectorReplayCommit> ReplayWithCurrentAuthorityAndLeaseAsync(GroupSpoolItemReference reference,
        GroupConnectorEnrollmentRequest request, GroupListenerLeaseSnapshot ownedLease, CancellationToken cancellationToken = default)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (reference?.Context is not { } context || request?.Source != context.Source || ownedLease?.Account is null ||
                context.Source.TenantId != binding.TenantId || context.Source.CompanyId != binding.CompanyId ||
                context.ConnectorAccountId != binding.ConnectorAccountId || context.ServiceId != binding.ServiceId || context.KeyId != binding.KeyId ||
                ownedLease.Account != new GroupListenerAccountScope(binding.TenantId, binding.CompanyId, binding.ConnectorAccountId) ||
                ownedLease.OwnerId == Guid.Empty || ownedLease.Epoch <= 0) throw new GroupConnectorTransportException();
            var current = await transport.FetchEnrollmentAsync(request, linked.Token);
            // The stored source/grant/deletion/credential versions are checked
            // before sending Renew as well as before private load/decryption.
            transport.RequireRecovery(context, current.Enrollment, ownedLease);
            using var renewal = await transport.PrepareListenerAsync(current.Enrollment,
                new(ownedLease.OwnerId, GroupListenerOperation.Renew, ownedLease.Epoch), linked.Token);
            var renewed = await transport.SendListenerAsync(renewal, linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            var receipt = await ReplayCoreAsync(reference, current.Enrollment, renewed.Lease, current.CheckedAtUtc, linked.Token);
            return new(receipt, renewed.Lease);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new GroupConnectorTransportException(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException)
        { if (error is GroupConnectorTransportException known) throw known; throw new GroupConnectorTransportException(); }
    }

    private void RequireFreshMetadata(DateTimeOffset? checkedAtUtc)
    {
        if (checkedAtUtc is not { } checkedAt) return;
        var now = clock.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || checkedAt.Offset != TimeSpan.Zero || checkedAt > now ||
            now - checkedAt > GroupConnectorEnrollmentSnapshot.MaximumAge) throw new GroupConnectorTransportException();
    }
}
