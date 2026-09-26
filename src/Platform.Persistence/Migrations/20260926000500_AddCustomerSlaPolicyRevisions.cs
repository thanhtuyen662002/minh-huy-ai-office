using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260926000500_AddCustomerSlaPolicyRevisions")]
public partial class AddCustomerSlaPolicyRevisions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(name: "CustomerSlaPolicyRevisions", schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                PolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                AuthorityVersion = table.Column<long>(type: "bigint", nullable: false),
                ServiceLabel = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                SchedulerPriority = table.Column<int>(type: "int", nullable: false),
                PriorityCeiling = table.Column<int>(type: "int", nullable: false),
                EffectiveAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_CustomerSlaPolicyRevisions", x => new { x.TenantId, x.CompanyId, x.UserId, x.PolicyVersion }));
        migrationBuilder.CreateIndex(name: "IX_CustomerSlaPolicyRevisions_Current", schema: PlatformDbContext.DefaultSchema,
            table: "CustomerSlaPolicyRevisions", columns: new[] { "TenantId", "CompanyId", "UserId", "EffectiveAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "CustomerSlaPolicyRevisions", schema: PlatformDbContext.DefaultSchema);
}
