using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260924073500_AddCustomerSlaPriorityAdmissions")]
public partial class AddCustomerSlaPriorityAdmissions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "CustomerSlaPriorityAdmissions",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                AdmissionId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                TenantId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                CompanyId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                UserId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                AuthorityVersion = table.Column<long>(type: "bigint", nullable: false),
                PolicyId = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                PolicyVersion = table.Column<long>(type: "bigint", nullable: false),
                ServiceClass = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                SchedulerPriority = table.Column<int>(type: "int", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_CustomerSlaPriorityAdmissions", x => x.AdmissionId);
                table.CheckConstraint("CK_CustomerSlaPriorityAdmissions_AuthorityVersion", "[AuthorityVersion] > 0");
                table.CheckConstraint("CK_CustomerSlaPriorityAdmissions_PolicyVersion", "[PolicyVersion] > 0");
                table.CheckConstraint("CK_CustomerSlaPriorityAdmissions_SchedulerPriority", "[SchedulerPriority] >= 0");
            });

        migrationBuilder.CreateIndex(
            name: "IX_CustomerSlaPriorityAdmissions_Authority",
            schema: PlatformDbContext.DefaultSchema,
            table: "CustomerSlaPriorityAdmissions",
            columns: new[] { "TenantId", "CompanyId", "UserId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(
            name: "CustomerSlaPriorityAdmissions",
            schema: PlatformDbContext.DefaultSchema);
}
