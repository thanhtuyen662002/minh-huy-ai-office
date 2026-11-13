using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Platform.Configuration;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>
/// A web ERP that calls the chat API server to server. It asks as its own service member (no roles), only about the data
/// sources of its billed customer, and signs every request with an HMAC key that is stored encrypted and shown once.
/// </summary>
public sealed class ErpChatClientRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Guid BillingAccountId { get; set; }
    public Guid ServiceUserId { get; set; }
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public bool IsEnabled { get; set; } = true;
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>Request nonces seen in the last minutes: a signed request can be used once.</summary>
public sealed class ErpChatNonceRecord
{
    public Guid ClientId { get; set; }
    public Guid Nonce { get; set; }
    public DateTimeOffset SeenAtUtc { get; set; }
}

public static class ErpChatModel
{
    public static void ConfigureErpChat(this ModelBuilder model)
    {
        model.Entity<ErpChatClientRecord>(entity =>
        {
            entity.ToTable("ErpChatClients", table => table.HasCheckConstraint("CK_ErpChatClients_Version", "[Version] > 0"));
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.HasIndex(row => row.Id).IsUnique();
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.Name }).IsUnique();
            entity.Property(row => row.Name).HasMaxLength(200);
            entity.Property(row => row.Ciphertext).HasMaxLength(256);
            entity.Property(row => row.Nonce).HasMaxLength(12).IsFixedLength();
            entity.Property(row => row.Tag).HasMaxLength(16).IsFixedLength();
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<AiBillingAccountRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, row.BillingAccountId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CompanyMembershipRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, row.ServiceUserId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ErpChatNonceRecord>(entity =>
        {
            entity.ToTable("ErpChatNonces");
            entity.HasKey(row => new { row.ClientId, row.Nonce });
            entity.HasIndex(row => row.SeenAtUtc);
        });
    }
}

public sealed record ErpChatClientView(Guid Id, string Name, Guid BillingAccountId, bool IsEnabled, string Version, DateTimeOffset CreatedAtUtc);
/// <summary>The signing key is returned only when a client is created or its key is replaced.</summary>
public sealed record ErpChatClientSecret(ErpChatClientView Client, string SigningKey);
public sealed record ErpChatClientCreateRequest(string Name, Guid BillingAccountId);
public sealed record ErpChatClientUpdateRequest(bool IsEnabled, bool RotateKey, string Version);

public sealed record ErpChatSignature(Guid ClientId, long Timestamp, Guid Nonce, string SignatureHex);
public sealed record ErpChatAskRequest(string RequestId, string ExternalUserId, string Question, Guid? DataSourceId = null, Guid? ConversationId = null);
public sealed record ErpChatAccepted(Guid MessageId, Guid ConversationId, string Status);
public sealed record ErpChatUsage(long InputTokens, long OutputTokens, decimal? Cost, string Currency);
public sealed record ErpChatMessage(Guid MessageId, Guid ConversationId, string Status, string? Answer, string? Failure, ErpChatUsage Usage);

/// <summary>Refusals the API maps to a status code and a stable machine-readable code.</summary>
public sealed class ErpChatException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public sealed class ErpChatService(PlatformDbContext database, IAuthorizationDirectory directory, ProtectedSecretEncryption encryption,
    TimeProvider? timeProvider = null)
{
    public const string Domain = "aioffice-erp-chat-v1";
    public const string IdentityProvider = "erp-chat";
    public static readonly TimeSpan MaximumClockSkew = TimeSpan.FromMinutes(5);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // ----- Administration (an AI Office admin, through the portal) -----

    private async Task RequireAdminAsync(AuthorizationContext authority, CancellationToken token)
    {
        var current = await directory.ResolveAsync(authority, token);
        if (current is null || current.Context != authority || !current.Roles.Contains("admin", StringComparer.Ordinal))
            throw new UnauthorizedAccessException();
    }

    public async Task<IReadOnlyList<ErpChatClientView>> ListClientsAsync(AuthorizationContext authority, CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var rows = await database.ErpChatClients.AsNoTracking()
            .Where(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId)
            .OrderBy(row => row.Name).ToListAsync(token);
        return rows.Select(View).ToArray();
    }

    /// <summary>Creates the client, its service member and its signing key (returned once).</summary>
    public async Task<ErpChatClientSecret> CreateClientAsync(AuthorizationContext authority, ErpChatClientCreateRequest request,
        CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        var name = request?.Name?.Trim() ?? "";
        if (name.Length is 0 or > 200 || name.Any(char.IsControl) || request!.BillingAccountId == Guid.Empty) throw new ArgumentException("Invalid client.");
        if (!await database.AiBillingAccounts.AnyAsync(row => row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId
                && row.Id == request.BillingAccountId, token)) throw new ArgumentException("Unknown customer.");
        var now = clock.GetUtcNow();
        var client = new ErpChatClientRecord
        {
            TenantId = authority.TenantId,
            CompanyId = authority.CompanyId,
            Id = Guid.NewGuid(),
            Name = name,
            BillingAccountId = request.BillingAccountId,
            ServiceUserId = Guid.NewGuid(),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        database.Users.Add(new PlatformUserRecord
        {
            TenantId = authority.TenantId,
            Id = client.ServiceUserId,
            IdentityProvider = IdentityProvider,
            Subject = client.Id.ToString("N"),
            DisplayName = $"{name} (web ERP)",
            IsActive = true,
            CreatedAtUtc = now
        });
        database.CompanyMemberships.Add(new CompanyMembershipRecord
        { TenantId = authority.TenantId, CompanyId = authority.CompanyId, UserId = client.ServiceUserId, IsActive = true, CreatedAtUtc = now });
        var key = NewKey(client);
        database.ErpChatClients.Add(client);
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException) { database.ChangeTracker.Clear(); throw; }
        return new(View(client), key);
    }

    /// <summary>Turns a client on or off and, when asked, replaces its signing key (the old one stops working at once).</summary>
    public async Task<ErpChatClientSecret?> UpdateClientAsync(AuthorizationContext authority, Guid clientId, ErpChatClientUpdateRequest request,
        CancellationToken token = default)
    {
        await RequireAdminAsync(authority, token);
        ArgumentNullException.ThrowIfNull(request);
        var client = await database.ErpChatClients.SingleOrDefaultAsync(row => row.TenantId == authority.TenantId
            && row.CompanyId == authority.CompanyId && row.Id == clientId, token) ?? throw new KeyNotFoundException();
        if (client.Version.ToString(CultureInfo.InvariantCulture) != request.Version)
            throw new DbUpdateConcurrencyException("The client changed since it was read.");
        client.IsEnabled = request.IsEnabled;
        var key = request.RotateKey ? NewKey(client) : null;
        client.Version++;
        client.UpdatedAtUtc = clock.GetUtcNow();
        var member = await database.CompanyMemberships.SingleAsync(row => row.TenantId == client.TenantId && row.CompanyId == client.CompanyId
            && row.UserId == client.ServiceUserId, token);
        member.IsActive = request.IsEnabled;
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException) { database.ChangeTracker.Clear(); throw; }
        return key is null ? null : new(View(client), key);
    }

    private string NewKey(ErpChatClientRecord client)
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var key = Convert.ToBase64String(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        var sealedKey = encryption.Encrypt(client.TenantId, client.CompanyId, client.Id, key, ProtectedSecretEncryption.ErpChatPurpose);
        (client.Ciphertext, client.Nonce, client.Tag) = (sealedKey.Ciphertext, sealedKey.Nonce, sealedKey.Tag);
        return key;
    }

    private static ErpChatClientView View(ErpChatClientRecord row) =>
        new(row.Id, row.Name, row.BillingAccountId, row.IsEnabled, row.Version.ToString(CultureInfo.InvariantCulture), row.CreatedAtUtc);

    // ----- The API (a web ERP server, signed) -----

    /// <summary>The exact bytes a client signs: domain, client, method, path and query, time, nonce and the body's SHA-256.</summary>
    public static byte[] SigningBytes(ErpChatSignature signature, string method, string pathAndQuery, ReadOnlySpan<byte> body) =>
        Encoding.ASCII.GetBytes(FormattableString.Invariant(
            $"{Domain}\n{signature.ClientId:D}\n{method}\n{pathAndQuery}\n{signature.Timestamp}\n{signature.Nonce:D}\n{Convert.ToHexString(SHA256.HashData(body))}"));

    /// <summary>Verifies the signature, the clock and that the nonce was not used before; returns the enabled client.</summary>
    public async Task<ErpChatClientRecord> AuthenticateAsync(ErpChatSignature signature, string method, string pathAndQuery,
        ReadOnlyMemory<byte> body, CancellationToken token)
    {
        if (signature.ClientId == Guid.Empty || signature.Nonce == Guid.Empty || signature.SignatureHex is not { Length: 64 }
            || !signature.SignatureHex.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')) throw Denied();
        var now = clock.GetUtcNow();
        DateTimeOffset signedAt;
        try { signedAt = DateTimeOffset.FromUnixTimeSeconds(signature.Timestamp); }
        catch (ArgumentOutOfRangeException) { throw Denied(); }
        if ((now - signedAt).Duration() > MaximumClockSkew) throw Denied();
        var signing = SigningBytes(signature, method, pathAndQuery, body.Span);
        var client = await database.ErpChatClients.AsNoTracking().SingleOrDefaultAsync(row => row.Id == signature.ClientId, token);
        if (client is null || !client.IsEnabled) throw Denied();
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encryption.Decrypt(client.TenantId, client.CompanyId, client.Id,
            new(client.Ciphertext, client.Nonce, client.Tag), ProtectedSecretEncryption.ErpChatPurpose));
        }
        catch (Exception error) when (error is FormatException or InvalidOperationException or CryptographicException) { throw Denied(); }
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(key, signing), Convert.FromHexString(signature.SignatureHex)))
                throw Denied();
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        // One use per nonce; old nonces are pruned as new ones arrive (they are outside the clock window anyway).
        await database.ErpChatNonces.Where(row => row.SeenAtUtc < now - 2 * MaximumClockSkew).ExecuteDeleteIfRelationalAsync(database, token);
        if (await database.ErpChatNonces.AnyAsync(row => row.ClientId == client.Id && row.Nonce == signature.Nonce, token))
            throw new ErpChatException(401, "replayed", "This request was already used.");
        database.ErpChatNonces.Add(new ErpChatNonceRecord { ClientId = client.Id, Nonce = signature.Nonce, SeenAtUtc = now });
        // The primary key settles a race between two copies of the same request.
        try { await database.SaveChangesAsync(token); }
        catch (DbUpdateException) { database.ChangeTracker.Clear(); throw new ErpChatException(401, "replayed", "This request was already used."); }
        return client;
    }

    /// <summary>Queues a question from a web ERP user. The same <see cref="ErpChatAskRequest.RequestId"/> returns the same message.</summary>
    public async Task<ErpChatAccepted> AskAsync(ErpChatClientRecord client, ErpChatAskRequest request, PilotTaskSubmissionService submission,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Text(request.RequestId, 100) || !Text(request.ExternalUserId, 200) || string.IsNullOrWhiteSpace(request.Question)
            || request.ConversationId == Guid.Empty || request.DataSourceId == Guid.Empty)
            throw new ErpChatException(400, "invalid_fields", "requestId, externalUserId and question are required.");
        var sources = await database.AiBillingAccountSources.AsNoTracking()
            .Where(row => row.TenantId == client.TenantId && row.CompanyId == client.CompanyId && row.AccountId == client.BillingAccountId)
            .Select(row => row.SourceId).ToListAsync(token);
        var source = request.DataSourceId ?? (sources.Count == 1 ? sources[0]
            : throw new ErpChatException(400, "data_source_required", "This customer has several databases: send dataSourceId."));
        if (!sources.Contains(source)) throw new ErpChatException(403, "data_source_denied", "This data source is not available to this client.");

        var authority = AuthorizationContext.Create(client.TenantId, client.CompanyId, client.ServiceUserId);
        var accepted = await submission.SubmitAsync(authority, new PilotTaskSubmissionRequest(
            $"erp-chat-v1-{client.Id:N}-{request.RequestId}", source, request.Question.Trim(), 3)
        { ConversationId = request.ConversationId, ExternalUserId = request.ExternalUserId }, token);
        var task = await database.Tasks.AsNoTracking().SingleAsync(row => row.TenantId == client.TenantId && row.CompanyId == client.CompanyId
            && row.Id == accepted.TaskId, token);
        if (task.ExternalUserId != request.ExternalUserId)
            throw new ErpChatException(409, "request_conflict", "This requestId was used by another user.");
        return new(task.Id, task.ConversationId ?? task.Id, Status(task.Status));
    }

    /// <summary>A question this client's user asked: its status, answer or failure, and what it cost so far.</summary>
    public async Task<ErpChatMessage> GetAsync(ErpChatClientRecord client, Guid messageId, string externalUserId, PilotTaskResultService results,
        CancellationToken token)
    {
        if (!Text(externalUserId, 200)) throw new ErpChatException(400, "invalid_fields", "externalUserId is required.");
        var task = await database.Tasks.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == client.TenantId && row.CompanyId == client.CompanyId
            && row.Id == messageId && row.CreatedByUserId == client.ServiceUserId && row.ExternalUserId == externalUserId, token)
            ?? throw new ErpChatException(404, "not_found", "No such message for this user.");
        var snapshot = await results.GetAsync(AuthorizationContext.Create(client.TenantId, client.CompanyId, client.ServiceUserId), messageId, token);
        var usage = await database.AiUsageRecords.AsNoTracking()
            .Where(row => row.TenantId == client.TenantId && row.CompanyId == client.CompanyId && row.TaskId == messageId)
            .GroupBy(row => 1).Select(group => new
            {
                Input = group.Sum(row => row.InputTokens),
                Output = group.Sum(row => row.OutputTokens),
                Cost = group.Sum(row => row.Cost ?? 0),
                Unpriced = group.Count(row => row.Cost == null)
            }).SingleOrDefaultAsync(token);
        return new(task.Id, task.ConversationId ?? task.Id, Status(snapshot?.TaskStatus ?? task.Status), Answer(snapshot?.ResultPayloadJson),
            snapshot?.TaskStatus == TaskExecutionStatus.Failed ? snapshot.FailureReason ?? "Không trả lời được." : null,
            new(usage?.Input ?? 0, usage?.Output ?? 0, usage is null ? 0 : usage.Unpriced > 0 ? null : usage.Cost, AiUsageService.Currency));
    }

    private static string? Answer(string? payload)
    {
        if (payload is null) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("answer", out var answer)
                && answer.ValueKind == JsonValueKind.String ? answer.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string Status(TaskExecutionStatus status) => status switch
    {
        TaskExecutionStatus.Completed => "answered",
        TaskExecutionStatus.Failed => "failed",
        _ => "pending"
    };

    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum
        && value == value.Trim() && !value.Any(char.IsControl);

    private static ErpChatException Denied() => new(401, "unauthorized", "The request signature is not valid.");
}

internal static class ErpChatQueryExtensions
{
    /// <summary>Bulk delete on SQL Server; the in-memory test provider has no ExecuteDelete, so it removes tracked rows instead.</summary>
    public static async Task ExecuteDeleteIfRelationalAsync<T>(this IQueryable<T> query, PlatformDbContext database, CancellationToken token)
        where T : class
    {
        if (database.Database.IsRelational()) await query.ExecuteDeleteAsync(token);
        else
        {
            database.RemoveRange(await query.ToListAsync(token));
            await database.SaveChangesAsync(token);
        }
    }
}
