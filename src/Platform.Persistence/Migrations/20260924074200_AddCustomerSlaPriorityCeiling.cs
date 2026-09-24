using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260924074200_AddCustomerSlaPriorityCeiling")]
public partial class AddCustomerSlaPriorityCeiling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE [aioffice].[CustomerSlaPriorityAdmissions]
                ADD [PriorityCeiling] int NULL;

            UPDATE [aioffice].[CustomerSlaPriorityAdmissions]
                SET [PriorityCeiling] = [SchedulerPriority]
                WHERE [PriorityCeiling] IS NULL;

            ALTER TABLE [aioffice].[CustomerSlaPriorityAdmissions]
                ALTER COLUMN [PriorityCeiling] int NOT NULL;

            ALTER TABLE [aioffice].[CustomerSlaPriorityAdmissions]
                ADD CONSTRAINT [CK_CustomerSlaPriorityAdmissions_PriorityCeiling]
                    CHECK ([PriorityCeiling] >= 0 AND [SchedulerPriority] <= [PriorityCeiling]);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE [aioffice].[CustomerSlaPriorityAdmissions]
                DROP CONSTRAINT [CK_CustomerSlaPriorityAdmissions_PriorityCeiling];

            ALTER TABLE [aioffice].[CustomerSlaPriorityAdmissions]
                DROP COLUMN [PriorityCeiling];
            """);
    }
}
