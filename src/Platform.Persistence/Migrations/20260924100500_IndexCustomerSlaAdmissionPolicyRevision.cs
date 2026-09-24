using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

/// <summary>
/// Makes the already-durable positive PolicyVersion part of the authority lookup access path.
/// The original admission migration enforces PolicyVersion > 0, so no permissive legacy
/// backfill is introduced: rows without a valid revision can never satisfy this lookup fence.
/// </summary>
[DbContext(typeof(PlatformDbContext))]
[Migration("20260924100500_IndexCustomerSlaAdmissionPolicyRevision")]
public partial class IndexCustomerSlaAdmissionPolicyRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CustomerSlaPriorityAdmissions_Authority",
            schema: PlatformDbContext.DefaultSchema,
            table: "CustomerSlaPriorityAdmissions");

        migrationBuilder.CreateIndex(
            name: "IX_CustomerSlaPriorityAdmissions_AuthorityPolicyRevision",
            schema: PlatformDbContext.DefaultSchema,
            table: "CustomerSlaPriorityAdmissions",
            columns: new[] { "TenantId", "CompanyId", "UserId", "AuthorityVersion", "PolicyVersion" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_CustomerSlaPriorityAdmissions_AuthorityPolicyRevision",
            schema: PlatformDbContext.DefaultSchema,
            table: "CustomerSlaPriorityAdmissions");

        migrationBuilder.CreateIndex(
            name: "IX_CustomerSlaPriorityAdmissions_Authority",
            schema: PlatformDbContext.DefaultSchema,
            table: "CustomerSlaPriorityAdmissions",
            columns: new[] { "TenantId", "CompanyId", "UserId" });
    }
}
