using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts.GroupIntake;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Host configuration, never an HTTP/model-supplied secret selector. Signing
// material is purpose separate from source-content and spool encryption keys.
public sealed record GroupConnectorSigningBinding(Guid TenantId, Guid CompanyId, Guid ServiceId,
    long CredentialEpoch, SecretReference Reference);

public sealed class GroupConnectorTransportException(HttpStatusCode? status = null)
    : IOException("Connector response is unavailable; preserve the same event and reconcile its commit.")
{ public HttpStatusCode? StatusCode { get; } = status; }

public sealed class GroupConnectorPreparedRequest : IDisposable
{
    private readonly object sync = new();
    private byte[]? body;
    internal GroupConnectorPreparedRequest(byte[] body, GroupServiceSignature signature, GroupScope source,
        GroupListenerAccountScope account, GroupListenerCommand? command)
    { this.body = body; Signature = signature; Source = source; Account = account; Command = command; }
    internal GroupServiceSignature Signature { get; }
    internal GroupScope Source { get; }
    internal GroupListenerAccountScope Account { get; }
    internal GroupListenerCommand? Command { get; }
    internal byte[] Capture() { lock (sync) { return body?.ToArray() ?? throw new ObjectDisposedException(nameof(GroupConnectorPreparedRequest)); } }
    public void Dispose() { lock (sync) { if (body is { } bytes) CryptographicOperations.ZeroMemory(bytes); body = null; } }
}

// Default-off library client. There is no DI/provider/listener activation here.
// A trusted host supplies current enrollment and qualified admission; backend
// HMAC, current authority and SQL commit remain the final acceptance boundary.
public sealed class GroupConnectorTransportClient : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly Uri origin;
    private readonly GroupConnectorSigningBinding binding;
    private readonly CompositeSecretResolver secrets;
    private readonly TimeProvider clock;
    private readonly GroupIngressRuntimePolicy policy;
    private readonly HttpClient http;

    public GroupConnectorTransportClient(Uri origin, GroupConnectorSigningBinding binding, CompositeSecretResolver secrets,
        TimeProvider clock, GroupIngressRuntimePolicy policy)
        : this(origin, binding, secrets, clock, policy, new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        })
    { }

    internal GroupConnectorTransportClient(Uri origin, GroupConnectorSigningBinding binding, CompositeSecretResolver secrets,
        TimeProvider clock, GroupIngressRuntimePolicy policy, HttpMessageHandler handler)
    {
        if (origin is null || !origin.IsAbsoluteUri || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 ||
            origin.Fragment.Length != 0 || policy is null || (origin.Scheme != "https" && !(policy.IsSyntheticFixture && origin.Scheme == "http" && origin.IsLoopback)) ||
            binding is null || binding.TenantId == Guid.Empty || binding.CompanyId == Guid.Empty || binding.ServiceId == Guid.Empty ||
            binding.CredentialEpoch <= 0 || binding.Reference is null || secrets is null || clock is null) throw new GroupConnectorTransportException();
        this.origin = origin; this.binding = binding; this.secrets = secrets; this.clock = clock; this.policy = policy;
        http = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public Task<GroupConnectorPreparedRequest> PrepareEventAsync(GroupConnectorSpoolAdmission admission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        return PrepareAsync(admission.Enrollment, admission.Payload, null, cancellationToken, admission);
    }

    internal void RequireSpoolBinding(GroupConnectorSpoolKeyBinding spool)
    {
        if (spool.TenantId != binding.TenantId || spool.CompanyId != binding.CompanyId || spool.ServiceId != binding.ServiceId ||
            spool.Reference == binding.Reference) throw new GroupConnectorTransportException();
    }

    internal void RequireRecovery(GroupSpoolContentContext context, GroupConnectorEnrollment current, GroupListenerLeaseSnapshot lease)
    {
        if (current?.Authentication is null || current.Authentication.ServiceId != binding.ServiceId ||
            current.Authentication.CredentialEpoch != binding.CredentialEpoch || current.Source?.Scope.TenantId != binding.TenantId ||
            current.Source.Scope.CompanyId != binding.CompanyId) throw new GroupConnectorTransportException();
        GroupSpoolContentProtector.RequireRecovery(context, current, lease, clock.GetUtcNow(), policy);
    }

    internal GroupConnectorSpoolAdmission Recover(GroupSpoolProtectedContent stored, ReadOnlySpan<byte> key,
        GroupConnectorEnrollment current, GroupListenerLeaseSnapshot lease)
    {
        RequireRecovery(stored.Context, current, lease);
        return new GroupSpoolContentProtector().Recover(stored, key, current, lease, clock.GetUtcNow(), policy);
    }

    public Task<GroupConnectorPreparedRequest> PrepareListenerAsync(GroupConnectorEnrollment enrollment,
        GroupListenerCommand command, CancellationToken cancellationToken = default)
    {
        if (enrollment?.Source is null || command is null) throw new GroupConnectorTransportException();
        command.Validate();
        return PrepareAsync(enrollment, new GroupListenerPayload(enrollment.Source.ExternalIdentity, command), command, cancellationToken);
    }

    private async Task<GroupConnectorPreparedRequest> PrepareAsync(GroupConnectorEnrollment enrollment, object payload,
        GroupListenerCommand? command, CancellationToken cancellationToken, GroupConnectorSpoolAdmission? admission = null)
    {
        using var timeout = new CancellationTokenSource(Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        byte[]? key = null; byte[]? body = null;
        try
        {
            if (admission is not null)
                GroupConnectorSpoolAdmission.RequireCurrent(admission.Enrollment, admission.Lease, clock.GetUtcNow(), policy, admission.Payload.Event.Kind);
            else GroupConnectorSpoolAdmission.RequireQualifiedEnrollment(enrollment, clock.GetUtcNow(), policy);
            var source = enrollment.Source;
            GroupRoutingPolicy.AuthorizeIngest(enrollment.Authentication, enrollment.Principal, enrollment.Grant, source,
                source.ExternalIdentity, isGroup: true, isKnownReportEcho: false);
            if (source.Scope.TenantId != binding.TenantId || source.Scope.CompanyId != binding.CompanyId ||
                enrollment.Authentication.ServiceId != binding.ServiceId || enrollment.Authentication.CredentialEpoch != binding.CredentialEpoch)
                throw new GroupConnectorTransportException();
            body = JsonSerializer.SerializeToUtf8Bytes(payload, GroupServiceAuthenticator.JsonOptions);
            if (body.Length is < 1 || body.Length > (command is null ? GroupServiceAuthenticator.MaximumBodyBytes : GroupServiceAuthenticator.MaximumListenerBodyBytes))
                throw new GroupConnectorTransportException();
            var encoded = await secrets.ResolveAsync(binding.Reference, linked.Token).AsTask().WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (encoded.Length != 44) throw new GroupConnectorTransportException();
            key = Convert.FromBase64String(encoded);
            if (key.Length != 32 || Convert.ToBase64String(key) != encoded) throw new GroupConnectorTransportException();
            var now = clock.GetUtcNow();
            if (now.Offset != TimeSpan.Zero || now.ToUnixTimeSeconds() < 0) throw new GroupConnectorTransportException();
            if (admission is not null)
                GroupConnectorSpoolAdmission.RequireCurrent(admission.Enrollment, admission.Lease, now, policy, admission.Payload.Event.Kind);
            else GroupConnectorSpoolAdmission.RequireQualifiedEnrollment(enrollment, now, policy);
            var signature = new GroupServiceSignature(binding.ServiceId, binding.CredentialEpoch, now.ToUnixTimeSeconds(), Guid.NewGuid(), "");
            var signing = command is null ? GroupServiceAuthenticator.SigningBytes(signature, body) : GroupServiceAuthenticator.ListenerSigningBytes(signature, body);
            signature = signature with { SignatureHex = Convert.ToHexString(HMACSHA256.HashData(key, signing)) };
            linked.Token.ThrowIfCancellationRequested();
            var prepared = new GroupConnectorPreparedRequest(body, signature, source.Scope,
                new(source.Scope.TenantId, source.Scope.CompanyId, source.ConnectorAccountId), command);
            body = null; return prepared;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new GroupConnectorTransportException(); }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or ArgumentException or FormatException or IOException or NotSupportedException)
        { throw new GroupConnectorTransportException(); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); if (body is not null) CryptographicOperations.ZeroMemory(body); }
    }

    public async Task<GroupIngressCommittedReceipt> SendEventAsync(GroupConnectorPreparedRequest prepared, CancellationToken cancellationToken = default)
    {
        if (prepared?.Command is not null) throw new GroupConnectorTransportException();
        var receipt = await SendAsync<GroupIngressCommittedReceipt>(prepared!, "/internal/group-ingress/events", cancellationToken);
        if (receipt?.Source != prepared!.Source || receipt.MessageId == Guid.Empty || receipt.Revision <= 0 || receipt.CommittedSequence <= 0 ||
            receipt.CommittedAtUtc.Offset != TimeSpan.Zero || receipt.CommittedAtUtc > clock.GetUtcNow()) throw new GroupConnectorTransportException();
        cancellationToken.ThrowIfCancellationRequested();
        return receipt;
    }

    public async Task<GroupListenerCommittedReceipt> SendListenerAsync(GroupConnectorPreparedRequest prepared, CancellationToken cancellationToken = default)
    {
        if (prepared?.Command is not { } command) throw new GroupConnectorTransportException();
        var receipt = await SendAsync<GroupListenerCommittedReceipt>(prepared, "/internal/group-ingress/listener", cancellationToken);
        var lease = receipt?.Lease; var now = clock.GetUtcNow();
        if (lease?.Account != prepared.Account || lease.OwnerId != command.OwnerId || lease.Epoch <= 0 ||
            command.Operation != GroupListenerOperation.Acquire && lease.Epoch != command.ExpectedEpoch ||
            lease.HeartbeatAtUtc.Offset != TimeSpan.Zero || lease.ExpiresAtUtc.Offset != TimeSpan.Zero || receipt!.CommittedAtUtc.Offset != TimeSpan.Zero ||
            lease.HeartbeatAtUtc > now || lease.ExpiresAtUtc < lease.HeartbeatAtUtc || receipt.CommittedAtUtc < lease.HeartbeatAtUtc || receipt.CommittedAtUtc > now ||
            receipt.CoverageRecorded && (!receipt.Changed || command.Operation == GroupListenerOperation.Renew) ||
            lease.ExpiresAtUtc - lease.HeartbeatAtUtc > GroupListenerLeasePolicy.LeaseDuration ||
            (command.Operation == GroupListenerOperation.Stop ? lease.ExpiresAtUtc != lease.HeartbeatAtUtc : lease.ExpiresAtUtc <= now))
            throw new GroupConnectorTransportException();
        cancellationToken.ThrowIfCancellationRequested();
        return receipt;
    }

    private async Task<T> SendAsync<T>(GroupConnectorPreparedRequest prepared, string path, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(Deadline, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        byte[]? body = null; var captured = new byte[8193];
        try
        {
            if (prepared is null || prepared.Signature.ServiceId != binding.ServiceId || prepared.Signature.CredentialEpoch != binding.CredentialEpoch ||
                prepared.Source.TenantId != binding.TenantId || prepared.Source.CompanyId != binding.CompanyId) throw new GroupConnectorTransportException();
            GroupServiceAuthenticator.RequireFreshSigningTime(DateTimeOffset.FromUnixTimeSeconds(prepared.Signature.SignedAtUnixSeconds), clock.GetUtcNow());
            linked.Token.ThrowIfCancellationRequested(); body = prepared.Capture();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, path)) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Add("X-AIOffice-Group-Service", prepared.Signature.ServiceId.ToString("D"));
            request.Headers.Add("X-AIOffice-Group-Epoch", prepared.Signature.CredentialEpoch.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-AIOffice-Group-Signed-At", prepared.Signature.SignedAtUnixSeconds.ToString(CultureInfo.InvariantCulture));
            request.Headers.Add("X-AIOffice-Group-Nonce", prepared.Signature.Nonce.ToString("D"));
            request.Headers.Add("X-AIOffice-Group-Signature", prepared.Signature.SignatureHex);
            using var response = await WithinAsync(http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token), linked.Token, value => value.Dispose());
            linked.Token.ThrowIfCancellationRequested();
            if (response.StatusCode != HttpStatusCode.OK) throw new GroupConnectorTransportException(response.StatusCode);
            if (response.RequestMessage?.RequestUri != request.RequestUri || response.Content.Headers.ContentLength is > 8192 ||
                response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentType.CharSet is { } charset && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ||
                response.Headers.CacheControl?.NoStore != true) throw new GroupConnectorTransportException();
            using var stream = await WithinAsync(response.Content.ReadAsStreamAsync(linked.Token), linked.Token, value => value.Dispose());
            var length = 0;
            while (length < captured.Length)
            {
                linked.Token.ThrowIfCancellationRequested();
                var count = await WithinAsync(stream.ReadAsync(captured.AsMemory(length), linked.Token).AsTask(), linked.Token,
                    _ => CryptographicOperations.ZeroMemory(captured), () => CryptographicOperations.ZeroMemory(captured));
                linked.Token.ThrowIfCancellationRequested(); if (count == 0) break; length += count;
            }
            if (length is < 1 or > 8192) throw new GroupConnectorTransportException();
            _ = new UTF8Encoding(false, true).GetCharCount(captured.AsSpan(0, length));
            using var json = JsonDocument.Parse(captured.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 8 });
            Unique(json.RootElement);
            var result = json.Deserialize<T>(GroupServiceAuthenticator.JsonOptions) ?? throw new GroupConnectorTransportException();
            linked.Token.ThrowIfCancellationRequested(); return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new GroupConnectorTransportException(); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or FormatException or NotSupportedException)
        { if (error is GroupConnectorTransportException known) throw known; throw new GroupConnectorTransportException(); }
        finally { if (body is not null) CryptographicOperations.ZeroMemory(body); CryptographicOperations.ZeroMemory(captured); }
    }

    // Even a noncooperative test/host dependency cannot return a late ACK.
    // Abandoned header/stream results are disposed; a late read target is zeroed
    // again after it finishes, since it can still alias the canceled buffer.
    private static async Task<T> WithinAsync<T>(Task<T> task, CancellationToken cancellationToken, Action<T> cleanup, Action? finallyCleanup = null)
    {
        try { return await task.WaitAsync(cancellationToken); }
        catch
        {
            _ = CleanLateAsync(task, cleanup, finallyCleanup);
            throw;
        }
    }
    private static async Task CleanLateAsync<T>(Task<T> task, Action<T> cleanup, Action? finallyCleanup)
    {
        try { cleanup(await task); }
        catch { /* No dependency exceptions/response content are logged. */ }
        finally { finallyCleanup?.Invoke(); }
    }

    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new GroupConnectorTransportException(); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Unique(child);
    }
    public void Dispose() => http.Dispose();
}
