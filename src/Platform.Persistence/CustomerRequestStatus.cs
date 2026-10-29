using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

// Signed status change from a command in the internal technical Zalo group (#316). The bot parses
// `#xong YC-0012`; this boundary only applies an allowed status to an in-scope ticket.
public sealed class CustomerRequestStatusUpdater(PlatformDbContext database, CustomerRequestAuthenticator authentication, TimeProvider clock)
{
    public const string Path = CustomerRequestAuthenticator.Path + "/status";
    public const string ActorType = "zalo_member";

    public async Task<CustomerRequestStatusReceipt> UpdateAsync(CustomerRequestSignature signature, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        if (body.Length is < 1 or > CustomerRequestJson.MaximumStatusBytes) throw new ArgumentException("Invalid customer request.");
        var captured = body.ToArray();
        try
        {
            var input = CustomerRequestJson.ReadStatus(captured);
            var binding = await authentication.AuthenticateAsync(signature, captured, "POST", Path, null, cancellationToken);
            if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Customer request update is unavailable.");
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            await CustomerRequestLocks.AcquireAsync(database, $"service:{signature.ServiceId:D}", cancellationToken);
            await CustomerRequestLocks.AcquireAsync(database, $"company:{binding.TenantId:D}:{binding.CompanyId:D}", cancellationToken);
            if (!await database.Companies.AsNoTracking().AnyAsync(x => x.TenantId == binding.TenantId && x.Id == binding.CompanyId && x.IsActive, cancellationToken))
                throw CustomerRequestServiceDirectory.Denied();
            authentication.RequireFresh(signature.Timestamp);
            await CustomerRequestReplayProtection.ConsumeAsync(database, signature, clock, cancellationToken);
            var groups = binding.AllowedSourceGroupExternalIds.ToArray();
            // Tickets of other companies or groups the service may not read are indistinguishable from missing codes.
            var ticket = await database.CustomerRequests.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == binding.TenantId &&
                x.CompanyId == binding.CompanyId && x.Code == input.Code && groups.Contains(x.SourceGroupExternalId), cancellationToken)
                ?? throw new CustomerRequestNotFoundException();
            var replayed = await database.CustomerRequestEvents.AsNoTracking().AnyAsync(x => x.TenantId == binding.TenantId &&
                x.CompanyId == binding.CompanyId && x.RequestId == ticket.Id && x.SourceMessageId == input.SourceMessageId, cancellationToken);
            var previous = ticket.Status;
            if (!replayed && input.Status != ticket.Status)
            {
                var now = clock.GetUtcNow();
                database.Attach(ticket);
                database.CustomerRequestEvents.Add(new()
                {
                    TenantId = binding.TenantId,
                    CompanyId = binding.CompanyId,
                    RequestId = ticket.Id,
                    Id = Guid.NewGuid(),
                    EventType = "status_changed",
                    ActorType = ActorType,
                    ActorId = signature.ServiceId,
                    ActorName = input.ActorName,
                    ActorExternalId = input.ActorExternalId,
                    SourceMessageId = input.SourceMessageId,
                    OldValue = previous,
                    NewValue = input.Status,
                    Timestamp = now
                });
                ticket.Status = input.Status;
                ticket.UpdatedAt = now < ticket.UpdatedAt ? ticket.UpdatedAt : now;
                if (!database.Database.IsSqlServer()) ticket.RowVersion = RandomNumberGenerator.GetBytes(8);
            }
            authentication.RequireFresh(signature.Timestamp);
            try { await database.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { throw new CustomerRequestConflictException(); }
            await transaction.CommitAsync(cancellationToken);
            return new(ticket.Code, ticket.Status, previous, ticket.Status != previous);
        }
        finally { CryptographicOperations.ZeroMemory(captured); }
    }
}
