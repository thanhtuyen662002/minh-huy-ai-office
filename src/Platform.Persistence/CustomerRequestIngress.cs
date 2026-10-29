using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class CustomerRequestIngress(PlatformDbContext database, CustomerRequestAuthenticator authentication, TimeProvider clock)
{
    public async Task<CustomerRequestReceipt> AcceptAsync(CustomerRequestSignature signature, ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken = default)
    {
        if (body.Length is < 1 or > CustomerRequestJson.MaximumBodyBytes) throw new ArgumentException("Invalid customer request.");
        // Parse and authenticate the same private snapshot across asynchronous work.
        var captured = body.ToArray();
        try
        {
            var input = CustomerRequestJson.ReadInput(captured);
            var binding = await authentication.AuthenticateAsync(signature, captured, input.SourceGroupExternalId, cancellationToken);
            if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Customer request ingestion is unavailable.");
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            // Service lock also protects nonces if a service is later remapped to another company.
            await CustomerRequestLocks.AcquireAsync(database, $"service:{signature.ServiceId:D}", cancellationToken);
            await CustomerRequestLocks.AcquireAsync(database, $"company:{binding.TenantId:D}:{binding.CompanyId:D}", cancellationToken);
            if (!await database.Companies.AsNoTracking().AnyAsync(x => x.TenantId == binding.TenantId && x.Id == binding.CompanyId && x.IsActive, cancellationToken))
                throw CustomerRequestServiceDirectory.Denied();
            await CustomerRequestGroupGrants.RequireAsync(database, binding, input.SourceGroupExternalId, cancellationToken);
            authentication.RequireFresh(signature.Timestamp);
            var now = clock.GetUtcNow();
            await CustomerRequestReplayProtection.ConsumeAsync(database, signature, clock, cancellationToken);
            var existing = await database.CustomerRequests.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId && x.IdempotencyKey == input.IdempotencyKey, cancellationToken);
            if (existing is not null)
            {
                await database.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(existing.Code, existing.Id, existing.Status);
            }
            var counter = await database.CustomerRequestCounters.SingleOrDefaultAsync(x => x.TenantId == binding.TenantId && x.CompanyId == binding.CompanyId, cancellationToken);
            if (counter is null)
            {
                counter = new() { TenantId = binding.TenantId, CompanyId = binding.CompanyId };
                database.CustomerRequestCounters.Add(counter);
            }
            counter.LastNumber = checked(counter.LastNumber + 1);
            var ticket = new CustomerRequestRecord
            {
                TenantId = binding.TenantId,
                CompanyId = binding.CompanyId,
                Id = Guid.NewGuid(),
                Code = "YC-" + counter.LastNumber.ToString("D4", CultureInfo.InvariantCulture),
                SourceGroupExternalId = input.SourceGroupExternalId,
                SourceGroupName = input.SourceGroupName,
                CustomerName = input.CustomerName,
                ReportedBy = input.ReportedBy,
                Summary = input.Summary,
                CustomerDesiredDueText = input.CustomerDesiredDueText,
                Status = input.OpenQuestions.Length == 0 ? "new" : "needs_clarification",
                IdempotencyKey = input.IdempotencyKey,
                SourceWindowFrom = input.SourceWindowFrom,
                SourceWindowTo = input.SourceWindowTo,
                CreatedAt = now,
                UpdatedAt = now
            };
            // The nonrelational test provider does not generate SQL Server rowversions.
            if (!database.Database.IsSqlServer()) ticket.RowVersion = RandomNumberGenerator.GetBytes(8);
            database.CustomerRequests.Add(ticket);
            database.CustomerRequestItems.AddRange(input.Items.Select((item, index) => new CustomerRequestItemRecord
            {
                TenantId = binding.TenantId,
                CompanyId = binding.CompanyId,
                RequestId = ticket.Id,
                Ordinal = index,
                Content = item.Content,
                Priority = item.Priority,
                Kind = item.Kind
            }));
            database.CustomerRequestEvidence.AddRange(input.Evidence.Select((item, index) => new CustomerRequestEvidenceRecord
            {
                TenantId = binding.TenantId,
                CompanyId = binding.CompanyId,
                RequestId = ticket.Id,
                Ordinal = index,
                Quote = item.Quote,
                SourceMessageId = item.SourceMessageId,
                SenderName = item.SenderName,
                MessageTimestamp = item.MessageTimestamp
            }));
            database.CustomerRequestOpenQuestions.AddRange(input.OpenQuestions.Select((question, index) => new CustomerRequestOpenQuestionRecord
            { TenantId = binding.TenantId, CompanyId = binding.CompanyId, RequestId = ticket.Id, Ordinal = index, Content = question }));
            database.CustomerRequestEvents.Add(new()
            {
                TenantId = binding.TenantId,
                CompanyId = binding.CompanyId,
                RequestId = ticket.Id,
                Id = Guid.NewGuid(),
                EventType = "created",
                ActorType = "service",
                ActorId = signature.ServiceId,
                NewValue = ticket.Status,
                Timestamp = now
            });
            authentication.RequireFresh(signature.Timestamp);
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ticket.Code, ticket.Id, ticket.Status);
        }
        finally { CryptographicOperations.ZeroMemory(captured); }
    }
}

internal static class CustomerRequestLocks
{
    internal static async Task AcquireAsync(PlatformDbContext database, string scope, CancellationToken cancellationToken)
    {
        if (!database.Database.IsSqlServer()) return;
        var resource = new SqlParameter("@resource", "aioffice:customer-request:" + scope);
        await database.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
            IF @result<0 THROW 51031,'Customer request is temporarily unavailable.',1;
            """, [resource], cancellationToken);
    }
}
