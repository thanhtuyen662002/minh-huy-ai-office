using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260925153500_AddDataSourceFailoverOperationPolicies")]
public partial class AddDataSourceFailoverOperationPolicies : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DataSourceFailoverOperationPolicies",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DataSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Operation = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                EffectiveAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DataSourceFailoverOperationPolicies", x => new { x.TenantId, x.CompanyId, x.DataSourceId, x.Operation, x.Version });
                table.ForeignKey("FK_DataSourceFailoverOperationPolicies_DataSources", x => new { x.TenantId, x.CompanyId, x.DataSourceId }, PlatformDbContext.DefaultSchema, "DataSources", new[] { "TenantId", "CompanyId", "Id" }, onDelete: ReferentialAction.Restrict);
                table.CheckConstraint("CK_DataSourceFailoverOperationPolicies_Version", "[Version] > 0");
            });
        migrationBuilder.CreateIndex(
            name: "IX_DataSourceFailoverOperationPolicies_Effective",
            schema: PlatformDbContext.DefaultSchema,
            table: "DataSourceFailoverOperationPolicies",
            columns: new[] { "TenantId", "CompanyId", "DataSourceId", "Operation", "EffectiveAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "DataSourceFailoverOperationPolicies", schema: PlatformDbContext.DefaultSchema);
}
