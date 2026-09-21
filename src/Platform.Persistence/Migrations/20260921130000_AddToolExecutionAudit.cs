using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

public partial class AddToolExecutionAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE [aioffice].[ToolExecutionAudit] (
                [AuditId] uniqueidentifier NOT NULL,
                [TenantId] uniqueidentifier NOT NULL,
                [CompanyId] uniqueidentifier NOT NULL,
                [UserId] uniqueidentifier NOT NULL,
                [TaskId] uniqueidentifier NOT NULL,
                [Resource] nvarchar(200) NOT NULL,
                [Action] nvarchar(100) NOT NULL,
                [Risk] nvarchar(40) NOT NULL,
                [Authorized] bit NOT NULL,
                [DecisionReason] nvarchar(200) NOT NULL,
                [OccurredAtUtc] datetimeoffset NOT NULL,
                [ExecutionId] nvarchar(200) NULL,
                CONSTRAINT [PK_ToolExecutionAudit] PRIMARY KEY ([AuditId])
            );
            CREATE INDEX [IX_ToolExecutionAudit_AuthorityTime]
                ON [aioffice].[ToolExecutionAudit] ([TenantId], [CompanyId], [UserId], [OccurredAtUtc]);
            CREATE INDEX [IX_ToolExecutionAudit_TaskTime]
                ON [aioffice].[ToolExecutionAudit] ([TenantId], [CompanyId], [TaskId], [OccurredAtUtc]);
            DENY UPDATE, DELETE ON OBJECT::[aioffice].[ToolExecutionAudit] TO public;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TABLE [aioffice].[ToolExecutionAudit];");
    }
}
