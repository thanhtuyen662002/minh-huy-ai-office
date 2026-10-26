using System.Globalization;
using System.Net.Mail;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

public sealed class CompanyInvitationRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string NormalizedEmail { get; set; } = "";
    public string RolesJson { get; set; } = "";
    public Guid? InvitedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public DateTimeOffset? RevokedAtUtc { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class CompanyInvitationAuditRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid InvitationId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string Action { get; set; } = "";
    public long InvitationVersion { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
}

public sealed record CompanyInvitationInput(string Email, string[] Roles);
public sealed record CompanyInvitationResult(Guid CompanyId, Guid Id, string Email, string[] Roles,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? AcceptedAtUtc,
    DateTimeOffset? RevokedAtUtc, string Version);
public sealed record CompanyInvitationPage(Guid CompanyId, IReadOnlyList<CompanyInvitationResult> Items, int Offset, int Limit, bool HasMore);
public sealed class CompanyInvitationConflictException(string code) : Exception("Invitation conflicts with current state.")
{
    public string Code { get; } = code;
}

public static class CompanyInvitationPolicy
{
    public static string NormalizeEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254 || email.Any(c => c > 127 || char.IsControl(c)))
            throw new ArgumentException("Invalid email.");
        var value = email.Trim();
        if (!MailAddress.TryCreate(value, out var parsed) || parsed.Address != value || !value.Contains('@'))
            throw new ArgumentException("Invalid email.");
        return value.ToLowerInvariant();
    }

    public static string[] ValidateRoles(string[] roles)
    {
        if (roles is null || roles.Length is < 1 or > 2 || roles.Any(role => role is not ("admin" or "viewer"))
            || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length)
            throw new ArgumentException("Invalid invitation roles.");
        return roles.Order(StringComparer.Ordinal).ToArray();
    }

    internal static CompanyInvitationResult Result(CompanyInvitationRecord row) => new(row.CompanyId, row.Id,
        row.NormalizedEmail, ValidateRoles(System.Text.Json.JsonSerializer.Deserialize<string[]>(row.RolesJson)!),
        row.CreatedAtUtc, row.ExpiresAtUtc, row.AcceptedAtUtc, row.RevokedAtUtc, row.Version.ToString(CultureInfo.InvariantCulture));
}

internal static class CompanyInvitationMapping
{
    public static void ConfigureCompanyInvitations(this ModelBuilder model)
    {
        model.Entity<PlatformUserRecord>().Property(row => row.NormalizedEmail).HasMaxLength(254).UseCollation("Latin1_General_100_BIN2");
        model.Entity<CompanyInvitationRecord>(entity =>
        {
            entity.ToTable("CompanyInvitations", table =>
            {
                table.HasCheckConstraint("CK_CompanyInvitations_Version", "[Version] > 0");
                table.HasCheckConstraint("CK_CompanyInvitations_Lifetime", "[ExpiresAtUtc] > [CreatedAtUtc] AND ([AcceptedAtUtc] IS NULL OR [RevokedAtUtc] IS NULL)");
            });
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.Property(row => row.NormalizedEmail).HasMaxLength(254).UseCollation("Latin1_General_100_BIN2");
            entity.Property(row => row.RolesJson).HasMaxLength(100);
            entity.Property(row => row.Version).IsConcurrencyToken();
            // Expired rows are explicitly revoked before replacement. Time cannot
            // appear in a SQL Server filtered unique index predicate.
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.NormalizedEmail }).IsUnique()
                .HasFilter("[AcceptedAtUtc] IS NULL AND [RevokedAtUtc] IS NULL");
            entity.HasIndex(row => row.NormalizedEmail);
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.CompanyId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.InvitedByUserId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<CompanyInvitationAuditRecord>(entity =>
        {
            entity.ToTable("CompanyInvitationAudits");
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.Property(row => row.Action).HasMaxLength(30);
            entity.HasOne<CompanyInvitationRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, Id = row.InvitationId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany().HasForeignKey(row => new { row.TenantId, Id = row.ActorUserId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
