using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class CustomerRequestPortal(PlatformDbContext database, IAuthorizationDirectory directory)
{
    public async Task<CustomerRequestPage> ListAsync(AuthorizationContext authority, Guid companyId, string? status = null,
        string? customer = null, int offset = 0, int limit = 50, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(authority, companyId, cancellationToken);
        if (offset is < 0 or > 100000 || limit is < 1 or > 100 || (status is not null && !CustomerRequestJson.IsStatus(status)))
            throw new ArgumentException("Invalid customer request filters.");
        if (customer is not null) CustomerRequestJson.Text(customer, 200);
        var query = Scoped(authority);
        if (status is not null) query = query.Where(x => x.Status == status);
        if (customer is not null) query = query.Where(x => x.CustomerName.Contains(customer));
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(offset).Take(limit + 1).ToArrayAsync(cancellationToken);
        await RequireMemberAsync(authority, companyId, cancellationToken);
        return new(rows.Take(limit).Select(View).ToArray(), offset, limit, rows.Length > limit);
    }

    public async Task<CustomerRequestDetail> GetAsync(AuthorizationContext authority, Guid companyId, string code, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(authority, companyId, cancellationToken);
        CustomerRequestJson.Text(code, 24, opaque: true);
        var ticket = await Scoped(authority).SingleOrDefaultAsync(x => x.Code == code, cancellationToken) ?? throw new CustomerRequestNotFoundException();
        var items = await database.CustomerRequestItems.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == companyId && x.RequestId == ticket.Id)
            .OrderBy(x => x.Ordinal).Select(x => new CustomerRequestItemInput(x.Content, x.Priority, x.Kind)).ToArrayAsync(cancellationToken);
        var evidence = await database.CustomerRequestEvidence.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == companyId && x.RequestId == ticket.Id)
            .OrderBy(x => x.Ordinal).Select(x => new CustomerRequestEvidenceInput(x.Quote, x.SourceMessageId, x.SenderName, x.MessageTimestamp)).ToArrayAsync(cancellationToken);
        var questions = await database.CustomerRequestOpenQuestions.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == companyId && x.RequestId == ticket.Id)
            .OrderBy(x => x.Ordinal).Select(x => x.Content).ToArrayAsync(cancellationToken);
        var events = await database.CustomerRequestEvents.AsNoTracking().Where(x => x.TenantId == authority.TenantId && x.CompanyId == companyId && x.RequestId == ticket.Id)
            .OrderBy(x => x.Timestamp).ThenBy(x => x.Id).Select(x => new CustomerRequestEventView(x.EventType, x.ActorType, x.ActorId, x.OldValue, x.NewValue, x.Timestamp, x.ActorName)).ToArrayAsync(cancellationToken);
        await RequireMemberAsync(authority, companyId, cancellationToken);
        return new(View(ticket), ticket.ReportedBy, ticket.CustomerDesiredDueText, ticket.SourceWindowFrom, ticket.SourceWindowTo, items, evidence, questions, events);
    }

    public async Task<CustomerRequestListItem> PatchAsync(AuthorizationContext authority, Guid companyId, string code,
        CustomerRequestPatch patch, CancellationToken cancellationToken = default)
    {
        await RequireMemberAsync(authority, companyId, cancellationToken);
        CustomerRequestJson.Text(code, 24, opaque: true);
        var expected = CustomerRequestJson.Version(patch.RowVersion);
        if (patch.Status is not null && !CustomerRequestJson.IsStatus(patch.Status) ||
            patch.Status is null && !patch.HasAssignee || patch.AssigneeUserId == Guid.Empty)
            throw new ArgumentException("Invalid customer request update.");
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Customer request update is unavailable.");
        await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
        await CustomerRequestLocks.AcquireAsync(database, $"company:{authority.TenantId:D}:{companyId:D}", cancellationToken);
        await RequireMemberAsync(authority, companyId, cancellationToken);
        var ticket = await Scoped(authority).SingleOrDefaultAsync(x => x.Code == code, cancellationToken) ?? throw new CustomerRequestNotFoundException();
        if (!ticket.RowVersion.SequenceEqual(expected)) throw new CustomerRequestConflictException();
        if (patch.HasAssignee && patch.AssigneeUserId is Guid userId)
        {
            var assignee = AuthorizationContext.Create(authority.TenantId, companyId, userId);
            if ((await directory.ResolveAsync(assignee, cancellationToken))?.Context != assignee)
                throw new ArgumentException("Assignee must be an active company member.");
        }
        // A request-scoped context normally has no tracked ticket. Detach a previously
        // read instance so comparisons always use the current SQL snapshot.
        var tracked = database.ChangeTracker.Entries<CustomerRequestRecord>().SingleOrDefault(x =>
            x.Entity.TenantId == authority.TenantId && x.Entity.CompanyId == companyId && x.Entity.Id == ticket.Id);
        if (tracked is not null) tracked.State = EntityState.Detached;
        database.Attach(ticket);
        var now = DateTimeOffset.UtcNow;
        if (patch.Status is not null && patch.Status != ticket.Status)
        {
            Event("status_changed", ticket.Status, patch.Status);
            ticket.Status = patch.Status;
        }
        if (patch.HasAssignee && patch.AssigneeUserId != ticket.AssigneeUserId)
        {
            Event("assignee_changed", ticket.AssigneeUserId?.ToString("D"), patch.AssigneeUserId?.ToString("D"));
            ticket.AssigneeUserId = patch.AssigneeUserId;
        }
        if (database.ChangeTracker.HasChanges())
        {
            ticket.UpdatedAt = now < ticket.UpdatedAt ? ticket.UpdatedAt : now;
            if (!database.Database.IsSqlServer()) ticket.RowVersion = RandomNumberGenerator.GetBytes(8);
            await RequireMemberAsync(authority, companyId, cancellationToken);
            try { await database.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { throw new CustomerRequestConflictException(); }
        }
        await transaction.CommitAsync(cancellationToken);
        return View(ticket);

        void Event(string type, string? before, string? after) => database.CustomerRequestEvents.Add(new()
        {
            TenantId = authority.TenantId,
            CompanyId = companyId,
            RequestId = ticket.Id,
            Id = Guid.NewGuid(),
            EventType = type,
            ActorType = "user",
            ActorId = authority.UserId,
            OldValue = before,
            NewValue = after,
            Timestamp = now
        });
    }

    private async Task RequireMemberAsync(AuthorizationContext authority, Guid companyId, CancellationToken cancellationToken)
    {
        if (authority.CompanyId != companyId || (await directory.ResolveAsync(authority, cancellationToken))?.Context != authority)
            throw new UnauthorizedAccessException("Company membership is unavailable.");
    }

    private IQueryable<CustomerRequestRecord> Scoped(AuthorizationContext authority) => database.CustomerRequests.AsNoTracking()
        .Where(x => x.TenantId == authority.TenantId && x.CompanyId == authority.CompanyId);

    private static CustomerRequestListItem View(CustomerRequestRecord ticket) => new(ticket.Code, ticket.Id, ticket.Status, ticket.Origin,
        ticket.CustomerName, ticket.Summary, ticket.SourceGroupExternalId, ticket.SourceGroupName, ticket.AssigneeUserId,
        ticket.CreatedAt, ticket.UpdatedAt, Convert.ToBase64String(ticket.RowVersion));
}
