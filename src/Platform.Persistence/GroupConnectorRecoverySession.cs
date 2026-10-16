using System.Security.Cryptography;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed record GroupConnectorRecoveryPass(int Committed, int Refused, int Remaining);

// One connector process/account owns capture and recovery together. The owner
// changes on process restart; current backend receipts, not RAM, fence it.
// This library does not connect a provider, open a volume or activate a host.
public sealed class GroupConnectorRecoverySession
{
    private readonly GroupConnectorFileSpool spool;
    private readonly GroupConnectorTransportClient transport;
    private readonly GroupConnectorSpoolTransport replay;
    private readonly GroupConnectorSpoolKeyBinding binding;
    private readonly CompositeSecretResolver secrets;
    private readonly TimeProvider clock;
    private readonly GroupIngressRuntimePolicy policy;
    private readonly GroupConnectorEnrollmentRequest[] sources;
    private readonly Guid owner = Guid.NewGuid();
    private readonly SemaphoreSlim serial = new(1, 1);
    private GroupListenerLeaseSnapshot? lease;
    private string? afterEventHash;
    private int heartbeatSource;

    public GroupConnectorRecoverySession(GroupConnectorFileSpool spool, GroupConnectorTransportClient transport,
        GroupConnectorSpoolKeyBinding binding, CompositeSecretResolver secrets, TimeProvider clock,
        GroupIngressRuntimePolicy policy, IReadOnlyList<GroupConnectorEnrollmentRequest> sources)
    {
        if (spool is null || transport is null || binding is null || secrets is null || clock is null || policy is null ||
            sources is null || sources.Count is < 1 or > 256) throw Unavailable();
        var configured = sources.ToArray();
        foreach (var source in configured)
        {
            if (source?.Source is null || source.Identity is null || source.Source.TenantId != binding.TenantId ||
                source.Source.CompanyId != binding.CompanyId) throw Unavailable();
            source.Validate();
            if (source.Identity.Provider != configured[0].Identity.Provider || source.Identity.AccountId != configured[0].Identity.AccountId)
                throw Unavailable();
        }
        if (configured.Select(item => item.Source.SourceBindingId).Distinct().Count() != configured.Length ||
            configured.Select(item => item.Identity).Distinct().Count() != configured.Length) throw Unavailable();
        this.replay = new(spool, transport, binding, secrets, clock);
        this.spool = spool; this.transport = transport; this.binding = binding;
        this.secrets = secrets; this.clock = clock; this.policy = policy; this.sources = configured;
    }

    // Success means an authenticated SQL commit, not just local persistence.
    // Every unknown response retains the encrypted original capture for replay.
    public async Task<GroupIngressCommittedReceipt> CaptureAsync(GroupIngressPayload payload, CancellationToken cancellationToken = default)
    {
        if (payload is null || !payload.IsGroup || payload.IsSelf || payload.IsKnownReportEcho || payload.Event?.Identity is null)
            throw Unavailable();
        var source = sources.SingleOrDefault(item => item.Identity == payload.Event.Identity) ?? throw Unavailable();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var entered = false; byte[]? key = null;
        try
        {
            await serial.WaitAsync(linked.Token); entered = true;
            var current = await RefreshAsync(source, linked.Token);
            var admitted = GroupConnectorSpoolAdmission.Filter(current.Enrollment,
                payload with { ListenerOwnerId = lease!.OwnerId, ListenerEpoch = lease.Epoch }, lease, clock.GetUtcNow(), policy);
            RequireFresh(current);
            var encoded = await secrets.ResolveAsync(binding.Reference, linked.Token).AsTask().WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (encoded is null || encoded.Length != 44) throw Unavailable();
            key = Convert.FromBase64String(encoded);
            if (key.Length != 32 || Convert.ToBase64String(key) != encoded) throw Unavailable();
            RequireFresh(current);
            GroupConnectorSpoolAdmission.RequireCurrent(current.Enrollment, lease!, clock.GetUtcNow(), policy, payload.Event.Kind);
            linked.Token.ThrowIfCancellationRequested();
            var protectedContent = new GroupSpoolContentProtector().Protect(admitted, key, binding.KeyId);
            linked.Token.ThrowIfCancellationRequested();
            var capture = spool.Append(protectedContent);
            var committed = await replay.ReplayWithCurrentAuthorityAndLeaseAsync(capture, source, lease!, linked.Token);
            lease = committed.Lease;
            return committed.Receipt;
        }
        catch (OperationCanceledException)
        { if (entered) lease = null; if (cancellationToken.IsCancellationRequested) throw; throw Unavailable(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException)
        { if (entered) lease = null; throw Unavailable(); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); if (entered) serial.Release(); }
    }

    // Fair metadata traversal is replaceable RAM. Exact encrypted backlog and
    // committed receipts stay on disk/SQL; one refused item never deletes it.
    public async Task<GroupConnectorRecoveryPass> RecoverOnceAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var entered = false;
        try
        {
            await serial.WaitAsync(linked.Token); entered = true;
            var pending = spool.Pending().OrderBy(item => item.Context.EventIdentityHash, StringComparer.Ordinal).ToArray();
            var selected = pending.Where(item => afterEventHash is null || string.CompareOrdinal(item.Context.EventIdentityHash, afterEventHash) > 0).Take(32).ToArray();
            if (selected.Length == 0 && pending.Length > 0) { afterEventHash = null; selected = pending.Take(32).ToArray(); }
            var committedCount = 0; var refused = 0;
            if (pending.Length == 0)
            {
                var source = sources[heartbeatSource]; heartbeatSource = (heartbeatSource + 1) % sources.Length;
                await RefreshAsync(source, linked.Token);
            }
            foreach (var item in selected)
            {
                linked.Token.ThrowIfCancellationRequested();
                afterEventHash = item.Context.EventIdentityHash;
                try
                {
                    var source = sources.SingleOrDefault(candidate => candidate.Source == item.Context.Source) ?? throw Unavailable();
                    if (item.Context.ConnectorAccountId != binding.ConnectorAccountId || item.Context.ServiceId != binding.ServiceId ||
                        item.Context.KeyId != binding.KeyId) throw Unavailable();
                    if (lease is null || lease.ExpiresAtUtc <= clock.GetUtcNow()) await RefreshAsync(source, linked.Token);
                    var committed = await replay.ReplayWithCurrentAuthorityAndLeaseAsync(item, source, lease!, linked.Token);
                    lease = committed.Lease; committedCount++;
                }
                catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException)
                { lease = null; refused++; }
            }
            linked.Token.ThrowIfCancellationRequested();
            return new(committedCount, refused, spool.Pending().Count);
        }
        catch (OperationCanceledException)
        { if (entered) lease = null; if (cancellationToken.IsCancellationRequested) throw; throw Unavailable(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or FormatException or NotSupportedException)
        { if (entered) lease = null; throw Unavailable(); }
        finally { if (entered) serial.Release(); }
    }

    private async Task<GroupConnectorCurrentEnrollment> RefreshAsync(GroupConnectorEnrollmentRequest source, CancellationToken token)
    {
        var current = await transport.FetchEnrollmentAsync(source, token);
        if (current.Enrollment.Source.ConnectorAccountId != binding.ConnectorAccountId) throw Unavailable();
        var operation = lease is null || lease.ExpiresAtUtc <= clock.GetUtcNow() ? GroupListenerOperation.Acquire : GroupListenerOperation.Renew;
        using var prepared = await transport.PrepareListenerAsync(current.Enrollment,
            new(owner, operation, operation == GroupListenerOperation.Acquire ? 0 : lease!.Epoch), token);
        var receipt = await transport.SendListenerAsync(prepared, token);
        if (receipt.Lease.Account != new GroupListenerAccountScope(binding.TenantId, binding.CompanyId, binding.ConnectorAccountId)) throw Unavailable();
        token.ThrowIfCancellationRequested(); RequireFresh(current);
        lease = receipt.Lease;
        return current;
    }

    private void RequireFresh(GroupConnectorCurrentEnrollment current)
    {
        var now = clock.GetUtcNow();
        if (now.Offset != TimeSpan.Zero || current.CheckedAtUtc > now || now - current.CheckedAtUtc > GroupConnectorEnrollmentSnapshot.MaximumAge)
            throw Unavailable();
    }

    private static GroupConnectorTransportException Unavailable() => new();
}
