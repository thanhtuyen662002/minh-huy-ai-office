using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MinhHuy.AIOffice.Shared.Contracts;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class CompanyInvitationService(PlatformDbContext database, IAuthorizationDirectory directory)
{
    private static readonly SemaphoreSlim NonSqlGate = new(1, 1);

    public async Task<CompanyInvitationResult> CreateAsync(AuthorizationContext authority, CompanyInvitationInput input,
        CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(authority, cancellationToken);
        var email = CompanyInvitationPolicy.NormalizeEmail(input.Email);
        var roles = CompanyInvitationPolicy.ValidateRoles(input.Roles);
        return await MutateAsync(async () =>
        {
            await LockCompanyAsync(authority.TenantId, authority.CompanyId, cancellationToken);
            await RequireAdminAsync(authority, cancellationToken);
            await new CompanyInvitationAuditPermissionVerifier(database).RequireAppendOnlyAsync(cancellationToken);
            if (await database.CompanyMemberships.Join(database.Users,
                member => new { member.TenantId, Id = member.UserId }, user => new { user.TenantId, user.Id },
                (member, user) => new { member.TenantId, member.CompanyId, user.NormalizedEmail }).AnyAsync(row =>
                    row.TenantId == authority.TenantId && row.CompanyId == authority.CompanyId && row.NormalizedEmail == email, cancellationToken))
                throw new CompanyInvitationConflictException("existing-member");
            var now = DateTimeOffset.UtcNow;
            var existing = await database.CompanyInvitations.SingleOrDefaultAsync(row => row.TenantId == authority.TenantId &&
                row.CompanyId == authority.CompanyId && row.NormalizedEmail == email && row.AcceptedAtUtc == null && row.RevokedAtUtc == null, cancellationToken);
            if (existing is not null)
            {
                if (existing.ExpiresAtUtc > now) throw new CompanyInvitationConflictException("duplicate-invitation");
                existing.RevokedAtUtc = now; existing.Version++;
                Audit(existing, "expired", authority.UserId, now);
                // Release the filtered unique slot before inserting its replacement.
                await database.SaveChangesAsync(cancellationToken);
            }
            var invitation = NewInvitation(authority.TenantId, authority.CompanyId, email, roles, authority.UserId, now);
            database.CompanyInvitations.Add(invitation); Audit(invitation, "created", authority.UserId, now);
            await RequireAdminAsync(authority, cancellationToken);
            return CompanyInvitationPolicy.Result(invitation);
        }, cancellationToken);
    }

    public async Task<CompanyInvitationPage> ListAsync(AuthorizationContext authority, int offset = 0, int limit = 25,
        CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(authority, cancellationToken);
        if (offset is < 0 or > 1000 || limit is < 1 or > 100) throw new ArgumentException("Invalid invitation page.");
        var now = DateTimeOffset.UtcNow;
        var rows = await database.CompanyInvitations.AsNoTracking().Where(row => row.TenantId == authority.TenantId &&
            row.CompanyId == authority.CompanyId && row.AcceptedAtUtc == null && row.RevokedAtUtc == null && row.ExpiresAtUtc > now)
            .OrderByDescending(row => row.CreatedAtUtc).ThenBy(row => row.Id).Skip(offset).Take(limit + 1).ToArrayAsync(cancellationToken);
        await RequireAdminAsync(authority, cancellationToken);
        return new(authority.CompanyId, rows.Take(limit).Select(CompanyInvitationPolicy.Result).ToArray(), offset, limit, rows.Length > limit);
    }

    public async Task<CompanyInvitationResult> RevokeAsync(AuthorizationContext authority, Guid id, CancellationToken cancellationToken = default)
    {
        await RequireAdminAsync(authority, cancellationToken);
        return await MutateAsync(async () =>
        {
            await LockCompanyAsync(authority.TenantId, authority.CompanyId, cancellationToken);
            await RequireAdminAsync(authority, cancellationToken);
            await new CompanyInvitationAuditPermissionVerifier(database).RequireAppendOnlyAsync(cancellationToken);
            var row = await database.CompanyInvitations.SingleOrDefaultAsync(row => row.TenantId == authority.TenantId &&
                row.CompanyId == authority.CompanyId && row.Id == id, cancellationToken) ?? throw new CompanyMembershipNotFoundException();
            if (row.AcceptedAtUtc is not null) throw new CompanyInvitationConflictException("accepted-invitation");
            if (row.RevokedAtUtc is null)
            {
                row.RevokedAtUtc = DateTimeOffset.UtcNow; row.Version++;
                Audit(row, "revoked", authority.UserId, row.RevokedAtUtc.Value);
            }
            await RequireAdminAsync(authority, cancellationToken);
            return CompanyInvitationPolicy.Result(row);
        }, cancellationToken);
    }

    // Called only from the authenticated principal boundary. Browser company,
    // tenant, user and role claims are deliberately absent from this contract.
    public async Task AcceptAsync(string identityProvider, string subject, string? email, bool emailVerified,
        CancellationToken cancellationToken = default)
    {
        if (!emailVerified || string.IsNullOrWhiteSpace(identityProvider) || identityProvider.Length > 100 ||
            string.IsNullOrWhiteSpace(subject) || subject.Length > 200) return;
        string normalized;
        try { normalized = CompanyInvitationPolicy.NormalizeEmail(email!); }
        catch (ArgumentException) { return; }
        await MutateAsync(async () =>
        {
            // SQL's conservative candidate equality also refuses padding/case
            // aliases of existing opaque identities; it never updates their roles.
            if (await database.Users.AnyAsync(row => row.IdentityProvider == identityProvider && row.Subject == subject, cancellationToken)) return false;
            var now = DateTimeOffset.UtcNow;
            var invitations = await database.CompanyInvitations.Where(row => row.NormalizedEmail == normalized &&
                row.AcceptedAtUtc == null && row.RevokedAtUtc == null && row.ExpiresAtUtc > now)
                .OrderBy(row => row.TenantId).ThenBy(row => row.CompanyId).Take(1001).ToArrayAsync(cancellationToken);
            if (invitations.Length > 1000) throw new InvalidOperationException("Invitation acceptance is unavailable.");
            if (invitations.Length == 0) return false;
            await new CompanyInvitationAuditPermissionVerifier(database).RequireAppendOnlyAsync(cancellationToken);
            var users = new Dictionary<Guid, PlatformUserRecord>();
            foreach (var row in invitations)
            {
                await LockCompanyAsync(row.TenantId, row.CompanyId, cancellationToken);
                if (!await database.Companies.AnyAsync(company => company.TenantId == row.TenantId && company.Id == row.CompanyId && company.IsActive, cancellationToken)) continue;
                // Two provider identities with the same verified email cannot
                // claim a second membership after the first invitation is spent.
                if (await database.CompanyMemberships.Join(database.Users,
                    member => new { member.TenantId, Id = member.UserId }, user => new { user.TenantId, user.Id },
                    (member, user) => new { member.TenantId, member.CompanyId, user.NormalizedEmail }).AnyAsync(member =>
                        member.TenantId == row.TenantId && member.CompanyId == row.CompanyId && member.NormalizedEmail == normalized, cancellationToken)) continue;
                var roles = CompanyInvitationPolicy.ValidateRoles(JsonSerializer.Deserialize<string[]>(row.RolesJson)!);
                if (!users.TryGetValue(row.TenantId, out var user))
                {
                    user = new()
                    {
                        TenantId = row.TenantId,
                        Id = Guid.NewGuid(),
                        IdentityProvider = identityProvider,
                        Subject = subject,
                        DisplayName = normalized,
                        NormalizedEmail = normalized,
                        CreatedAtUtc = now
                    };
                    database.Users.Add(user); users.Add(row.TenantId, user);
                }
                database.CompanyMemberships.Add(new() { TenantId = row.TenantId, CompanyId = row.CompanyId, UserId = user.Id, CreatedAtUtc = now });
                foreach (var role in roles) database.RoleAssignments.Add(new()
                { TenantId = row.TenantId, CompanyId = row.CompanyId, UserId = user.Id, RoleKey = role, CreatedAtUtc = now });
                row.AcceptedAtUtc = now; row.Version++;
                Audit(row, "accepted", user.Id, now);
            }
            return true;
        }, cancellationToken);
    }

    public async Task<CompanyInvitationResult> BootstrapAsync(Guid tenantId, Guid companyId, string code, string name, string email,
        CancellationToken cancellationToken = default)
    {
        var normalized = CompanyInvitationPolicy.NormalizeEmail(email);
        if (tenantId == Guid.Empty || companyId == Guid.Empty || string.IsNullOrWhiteSpace(code) || code.Length > 100 ||
            string.IsNullOrWhiteSpace(name) || name.Length > 200 || code != code.Trim() || name != name.Trim() || code.Any(char.IsControl) || name.Any(char.IsControl))
            throw new ArgumentException("Invalid company.");
        return await MutateAsync(async () =>
        {
            await LockCompanyAsync(tenantId, companyId, cancellationToken);
            var company = await database.Companies.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.Id == companyId, cancellationToken);
            if (company is not null)
            {
                // Repeat only our own bootstrap. Never add an administrator to an
                // arbitrary existing company or silently change an earlier email.
                var original = await database.CompanyInvitations.SingleOrDefaultAsync(row => row.TenantId == tenantId && row.CompanyId == companyId && row.InvitedByUserId == null, cancellationToken);
                if (!company.IsActive || company.Code != code || company.Name != name || original?.NormalizedEmail != normalized || original.RolesJson != "[\"admin\"]")
                    throw new CompanyInvitationConflictException("bootstrap-conflict");
                return CompanyInvitationPolicy.Result(original);
            }
            var now = DateTimeOffset.UtcNow;
            database.Companies.Add(new() { TenantId = tenantId, Id = companyId, Code = code, Name = name, CreatedAtUtc = now });
            var invitation = NewInvitation(tenantId, companyId, normalized, ["admin"], null, now);
            database.CompanyInvitations.Add(invitation); Audit(invitation, "bootstrapped", null, now);
            return CompanyInvitationPolicy.Result(invitation);
        }, cancellationToken);
    }

    private async Task<T> MutateAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Invitation storage is unavailable.");
        database.ChangeTracker.Clear();
        var nonSql = !database.Database.IsSqlServer();
        if (nonSql) await NonSqlGate.WaitAsync(cancellationToken);
        try
        {
            await using var transaction = await DataSourceRegistrationTransaction.BeginAsync(database, cancellationToken);
            if (!nonSql)
                // A single transaction-owned lock orders acceptance, creation,
                // expiry, bootstrap and revoke across hosts and DbContexts.
                await LockAsync("aioffice:company-invitations", cancellationToken);
            var result = await operation();
            await database.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch { database.ChangeTracker.Clear(); throw; }
        finally { if (nonSql) NonSqlGate.Release(); }
    }

    private Task LockCompanyAsync(Guid tenant, Guid company, CancellationToken cancellationToken) => database.Database.IsSqlServer()
        ? LockAsync($"aioffice:membership:{tenant:D}:{company:D}", cancellationToken) : Task.CompletedTask;

    private Task LockAsync(string name, CancellationToken cancellationToken) => database.Database.ExecuteSqlRawAsync("""
        DECLARE @result int;
        EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=5000;
        IF @result<0 THROW 51030,'Company invitations are temporarily unavailable.',1;
        """, [new SqlParameter("@resource", name)], cancellationToken);

    private async Task RequireAdminAsync(AuthorizationContext authority, CancellationToken cancellationToken)
    {
        var current = await directory.ResolveAsync(authority, cancellationToken);
        if (current?.Context != authority || !current.Roles.Contains("admin", StringComparer.Ordinal)) throw new UnauthorizedAccessException();
    }

    private static CompanyInvitationRecord NewInvitation(Guid tenant, Guid company, string email, string[] roles, Guid? actor, DateTimeOffset now) => new()
    {
        TenantId = tenant,
        CompanyId = company,
        Id = Guid.NewGuid(),
        NormalizedEmail = email,
        RolesJson = JsonSerializer.Serialize(roles),
        InvitedByUserId = actor,
        CreatedAtUtc = now,
        ExpiresAtUtc = now.AddDays(7)
    };

    private void Audit(CompanyInvitationRecord row, string action, Guid? actor, DateTimeOffset now) => database.CompanyInvitationAudits.Add(new()
    {
        TenantId = row.TenantId,
        CompanyId = row.CompanyId,
        Id = Guid.NewGuid(),
        InvitationId = row.Id,
        ActorUserId = actor,
        Action = action,
        InvitationVersion = row.Version,
        OccurredAtUtc = now
    });
}
