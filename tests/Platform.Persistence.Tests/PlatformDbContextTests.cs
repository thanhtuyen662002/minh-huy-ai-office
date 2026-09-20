using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MinhHuy.AIOffice.Platform.Persistence.Tests;

public sealed class PlatformDbContextTests
{
    [Fact]
    public void Model_UsesExpectedSchemaAndTable()
    {
        using var context = CreateContext();

        var entity = context.Model.FindEntityType(typeof(PlatformMetadataRecord));

        Assert.NotNull(entity);
        Assert.Equal(PlatformDbContext.DefaultSchema, entity.GetSchema());
        Assert.Equal("PlatformMetadata", entity.GetTableName());
        Assert.Equal(200, entity.FindProperty(nameof(PlatformMetadataRecord.Key))?.GetMaxLength());
    }

    [Fact]
    public void IdentityModel_UsesTenantScopedCompositeBoundaries()
    {
        using var context = CreateContext();

        var user = context.Model.FindEntityType(typeof(PlatformUserRecord));
        var company = context.Model.FindEntityType(typeof(CompanyRecord));
        var membership = context.Model.FindEntityType(typeof(CompanyMembershipRecord));
        var roleAssignment = context.Model.FindEntityType(typeof(RoleAssignmentRecord));

        Assert.NotNull(user);
        Assert.NotNull(company);
        Assert.NotNull(membership);
        Assert.NotNull(roleAssignment);

        Assert.Equal(
            new[] { nameof(PlatformUserRecord.TenantId), nameof(PlatformUserRecord.Id) },
            user.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            new[] { nameof(CompanyRecord.TenantId), nameof(CompanyRecord.Id) },
            company.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            new[]
            {
                nameof(CompanyMembershipRecord.TenantId),
                nameof(CompanyMembershipRecord.CompanyId),
                nameof(CompanyMembershipRecord.UserId)
            },
            membership.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal(
            new[]
            {
                nameof(RoleAssignmentRecord.TenantId),
                nameof(RoleAssignmentRecord.CompanyId),
                nameof(RoleAssignmentRecord.UserId),
                nameof(RoleAssignmentRecord.RoleKey)
            },
            roleAssignment.FindPrimaryKey()!.Properties.Select(property => property.Name));

        Assert.Contains(
            membership.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(CompanyRecord)
                && foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(CompanyMembershipRecord.TenantId),
                            nameof(CompanyMembershipRecord.CompanyId)
                        }));

        Assert.Contains(
            membership.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(PlatformUserRecord)
                && foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(CompanyMembershipRecord.TenantId),
                            nameof(CompanyMembershipRecord.UserId)
                        }));

        Assert.Contains(
            roleAssignment.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(CompanyMembershipRecord)
                && foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(RoleAssignmentRecord.TenantId),
                            nameof(RoleAssignmentRecord.CompanyId),
                            nameof(RoleAssignmentRecord.UserId)
                        }));

        Assert.Contains(
            user.GetIndexes(),
            index =>
                index.IsUnique
                && index.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(PlatformUserRecord.TenantId),
                            nameof(PlatformUserRecord.IdentityProvider),
                            nameof(PlatformUserRecord.Subject)
                        }));

        Assert.Contains(
            company.GetIndexes(),
            index =>
                index.IsUnique
                && index.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(CompanyRecord.TenantId),
                            nameof(CompanyRecord.Code)
                        }));
    }

    [Fact]
    public void DataSourceModel_UsesTenantCompanyBoundaryAndLogicalNameUniqueness()
    {
        using var context = CreateContext();

        var dataSource = context.Model.FindEntityType(typeof(DataSourceRecord));

        Assert.NotNull(dataSource);
        Assert.Equal(PlatformDbContext.DefaultSchema, dataSource.GetSchema());
        Assert.Equal("DataSources", dataSource.GetTableName());
        Assert.Equal(
            new[]
            {
                nameof(DataSourceRecord.TenantId),
                nameof(DataSourceRecord.CompanyId),
                nameof(DataSourceRecord.Id)
            },
            dataSource.FindPrimaryKey()!.Properties.Select(property => property.Name));

        Assert.Contains(
            dataSource.GetForeignKeys(),
            foreignKey =>
                foreignKey.PrincipalEntityType.ClrType == typeof(CompanyRecord)
                && foreignKey.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(DataSourceRecord.TenantId),
                            nameof(DataSourceRecord.CompanyId)
                        }));

        Assert.Contains(
            dataSource.GetIndexes(),
            index =>
                index.IsUnique
                && index.Properties.Select(property => property.Name)
                    .SequenceEqual(
                        new[]
                        {
                            nameof(DataSourceRecord.TenantId),
                            nameof(DataSourceRecord.CompanyId),
                            nameof(DataSourceRecord.LogicalName)
                        }));

        Assert.Equal(
            DataSourceRecord.MaximumSecretReferenceLength,
            dataSource.FindProperty(nameof(DataSourceRecord.ConnectionSecretReference))?.GetMaxLength());
        Assert.Equal(
            200,
            dataSource.FindProperty(nameof(DataSourceRecord.LogicalName))?.GetMaxLength());
    }

    [Fact]
    public void ConnectionFactory_ReturnsClosedSqlConnection()
    {
        var factory = new SqlServerConnectionFactory();

        using var connection = factory.Create(
            "Server=localhost;Database=AIOffice_Model_Test;User Id=test;Password=test;TrustServerCertificate=true");

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }

    private static PlatformDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer(
                "Server=localhost;Database=AIOffice_Model_Test;User Id=test;Password=test;TrustServerCertificate=true")
            .Options;

        return new PlatformDbContext(options);
    }
}
