using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;

namespace MinhHuy.AIOffice.Platform.Persistence;

/// <summary>Shared SQL Server address and read-only login for several customer ERP databases. The ciphertext holds an
/// <see cref="ErpServerConnection"/>; each linked source still keeps its own complete protected connection string.</summary>
public sealed class ErpServerRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public long Version { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

/// <summary>A protected customer ERP source whose connection is built from a shared server plus this database.</summary>
public sealed class ErpServerSourceRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid SourceId { get; set; }
    public Guid ServerId { get; set; }
    public string Database { get; set; } = "";
}

/// <summary>Append-only record of a shared server save; also makes a retried save idempotent.</summary>
public sealed class ErpServerAuditRecord
{
    public Guid TenantId { get; set; }
    public Guid CompanyId { get; set; }
    public Guid Id { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid ServerId { get; set; }
    public long ServerVersion { get; set; }
    public int SourceCount { get; set; }
    public Guid OperationId { get; set; }
    public string RequestHash { get; set; } = "";
    public DateTimeOffset OccurredAtUtc { get; set; }
}

/// <summary>The encrypted part of a shared server: everything of a connection except the database.</summary>
public sealed class ErpServerConnection
{
    public string Host { get; init; } = "";
    public int Port { get; init; } = 1433;
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    public override string ToString() => "ERP server credentials (redacted)";

    public CustomerErpConnectionInput For(string database) => new()
    {
        Host = Host, Port = Port, Database = database, UserName = UserName, Password = Password,
        Encrypt = Encrypt, TrustServerCertificate = TrustServerCertificate
    };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ErpServerSaveRequest
{
    public Guid OperationId { get; init; }
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public int Port { get; init; } = 1433;
    /// <summary>Blank on an update keeps the saved login.</summary>
    public string UserName { get; init; } = "";
    /// <summary>Blank on an update keeps the saved password.</summary>
    public string Password { get; init; } = "";
    public bool Encrypt { get; init; } = true;
    public bool TrustServerCertificate { get; init; }
    /// <summary>Required on an update, absent on create.</summary>
    public string? Version { get; init; }
    public override string ToString() => "ERP server save request (redacted)";
}

/// <summary>Creates a source on a shared server (<see cref="LogicalName"/>) or moves an existing source onto one
/// (<see cref="BindingVersion"/>). A blank database on a move keeps the source's current database.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ErpServerSourceRequest
{
    public Guid OperationId { get; init; }
    public Guid ServerId { get; init; }
    public string Database { get; init; } = "";
    public string? LogicalName { get; init; }
    public string? BindingVersion { get; init; }
}

/// <summary>Never carries the password.</summary>
public sealed record ErpServer(Guid Id, string Name, string Host, int Port, string UserName, bool Encrypt,
    bool TrustServerCertificate, string Version, int SourceCount);

public sealed class ErpServerVerificationException(CustomerErpConnectionResult result, string source) : Exception("ERP server verification failed.")
{
    public CustomerErpConnectionResult Result { get; } = result;
    /// <summary>Logical name of the database that failed, so the administrator knows which one to fix.</summary>
    public string Source { get; } = source;
}

public sealed class ErpServerStorePermissions(PlatformDbContext database)
{
    public Task RequireAsync(CancellationToken token) => new BindingStorePermissionVerifier(database).RequireProofAsync("""
        SELECT CASE WHEN
          HAS_PERMS_BY_NAME(N'aioffice.ErpServers',N'OBJECT',N'SELECT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServers',N'OBJECT',N'INSERT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServers',N'OBJECT',N'UPDATE')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServerSources',N'OBJECT',N'SELECT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServerSources',N'OBJECT',N'INSERT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServerSources',N'OBJECT',N'UPDATE')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServerAudits',N'OBJECT',N'INSERT')=1
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServerAudits',N'OBJECT',N'UPDATE')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServerAudits',N'OBJECT',N'DELETE')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServerAudits',N'OBJECT',N'ALTER')=0
          AND HAS_PERMS_BY_NAME(N'aioffice.ErpServerAudits',N'OBJECT',N'CONTROL')=0
          AND NOT EXISTS (SELECT 1 FROM sys.columns c WHERE c.object_id=OBJECT_ID(N'aioffice.ErpServerAudits')
            AND ISNULL(HAS_PERMS_BY_NAME(N'aioffice.ErpServerAudits',N'OBJECT',N'UPDATE',c.name,N'COLUMN'),1)<>0)
          THEN 1 ELSE 0 END;
        """, token);
}

internal static class ErpServerMapping
{
    public static void ConfigureErpServers(this ModelBuilder model)
    {
        model.Entity<ErpServerRecord>(entity =>
        {
            entity.ToTable("ErpServers", table => table.HasCheckConstraint("CK_ErpServers_Version", "[Version] > 0"));
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.Name }).IsUnique();
            entity.Property(row => row.Name).HasMaxLength(200);
            entity.Property(row => row.Ciphertext).HasMaxLength(8192);
            entity.Property(row => row.Nonce).HasMaxLength(12).IsFixedLength();
            entity.Property(row => row.Tag).HasMaxLength(16).IsFixedLength();
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasOne<CompanyRecord>().WithMany().HasForeignKey(row => new { row.TenantId, row.CompanyId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ErpServerSourceRecord>(entity =>
        {
            entity.ToTable("ErpServerSources");
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.SourceId });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.ServerId });
            entity.Property(row => row.Database).HasMaxLength(128);
            entity.HasOne<ProtectedDataSourceSecretRecord>().WithOne()
                .HasForeignKey<ErpServerSourceRecord>(row => new { row.TenantId, row.CompanyId, row.SourceId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ErpServerRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, row.ServerId }).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<ErpServerAuditRecord>(entity =>
        {
            entity.ToTable("ErpServerAudits", table => table.HasCheckConstraint("CK_ErpServerAudits_ServerVersion", "[ServerVersion] > 0"));
            entity.HasKey(row => new { row.TenantId, row.CompanyId, row.Id });
            entity.HasIndex(row => new { row.TenantId, row.CompanyId, row.OperationId }).IsUnique();
            entity.Property(row => row.RequestHash).HasMaxLength(64).IsFixedLength();
            entity.HasOne<ErpServerRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, row.CompanyId, row.ServerId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PlatformUserRecord>().WithMany()
                .HasForeignKey(row => new { row.TenantId, Id = row.ActorUserId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
