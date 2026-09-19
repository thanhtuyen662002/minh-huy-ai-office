using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260919154000_AddDurableTaskEngine")]
public partial class AddDurableTaskEngine : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Tasks",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                ConversationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                Status = table.Column<string>(
                    type: "nvarchar(40)",
                    maxLength: 40,
                    nullable: false,
                    defaultValue: "Pending"),
                WaitReason = table.Column<string>(
                    type: "nvarchar(1000)",
                    maxLength: 1000,
                    nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()"),
                UpdatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Tasks", x => new { x.TenantId, x.CompanyId, x.Id });
                table.ForeignKey(
                    name: "FK_Tasks_CompanyMemberships_TenantId_CompanyId_CreatedByUserId",
                    columns: x => new { x.TenantId, x.CompanyId, x.CreatedByUserId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "CompanyMemberships",
                    principalColumns: new[] { "TenantId", "CompanyId", "UserId" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "TaskSteps",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                StepKey = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: false),
                Status = table.Column<string>(
                    type: "nvarchar(40)",
                    maxLength: 40,
                    nullable: false,
                    defaultValue: "Pending"),
                Attempt = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()"),
                UpdatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_TaskSteps",
                    x => new { x.TenantId, x.CompanyId, x.TaskId, x.Id });
                table.ForeignKey(
                    name: "FK_TaskSteps_Tasks_TenantId_CompanyId_TaskId",
                    columns: x => new { x.TenantId, x.CompanyId, x.TaskId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "Tasks",
                    principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TaskDependencies",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                DependsOnStepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_TaskDependencies",
                    x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId, x.DependsOnStepId });
                table.ForeignKey(
                    name: "FK_TaskDependencies_TaskSteps_DependsOn",
                    columns: x => new { x.TenantId, x.CompanyId, x.TaskId, x.DependsOnStepId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "TaskSteps",
                    principalColumns: new[] { "TenantId", "CompanyId", "TaskId", "Id" });
                table.ForeignKey(
                    name: "FK_TaskDependencies_TaskSteps_Step",
                    columns: x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "TaskSteps",
                    principalColumns: new[] { "TenantId", "CompanyId", "TaskId", "Id" });
            });

        migrationBuilder.CreateTable(
            name: "TaskEvents",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Sequence = table.Column<long>(type: "bigint", nullable: false),
                StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                EventType = table.Column<string>(
                    type: "nvarchar(100)",
                    maxLength: 100,
                    nullable: false),
                PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                OccurredAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_TaskEvents",
                    x => new { x.TenantId, x.CompanyId, x.TaskId, x.Sequence });
                table.ForeignKey(
                    name: "FK_TaskEvents_TaskSteps_TenantId_CompanyId_TaskId_StepId",
                    columns: x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "TaskSteps",
                    principalColumns: new[] { "TenantId", "CompanyId", "TaskId", "Id" });
                table.ForeignKey(
                    name: "FK_TaskEvents_Tasks_TenantId_CompanyId_TaskId",
                    columns: x => new { x.TenantId, x.CompanyId, x.TaskId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "Tasks",
                    principalColumns: new[] { "TenantId", "CompanyId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TaskCheckpoints",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                Version = table.Column<long>(type: "bigint", nullable: false),
                PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()")
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_TaskCheckpoints",
                    x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId, x.Version });
                table.ForeignKey(
                    name: "FK_TaskCheckpoints_TaskSteps_TenantId_CompanyId_TaskId_StepId",
                    columns: x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "TaskSteps",
                    principalColumns: new[] { "TenantId", "CompanyId", "TaskId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Tasks_TenantId_CompanyId_CreatedByUserId",
            schema: PlatformDbContext.DefaultSchema,
            table: "Tasks",
            columns: new[] { "TenantId", "CompanyId", "CreatedByUserId" });

        migrationBuilder.CreateIndex(
            name: "IX_Tasks_TenantId_CompanyId_Status",
            schema: PlatformDbContext.DefaultSchema,
            table: "Tasks",
            columns: new[] { "TenantId", "CompanyId", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_TaskSteps_TenantId_CompanyId_TaskId_StepKey",
            schema: PlatformDbContext.DefaultSchema,
            table: "TaskSteps",
            columns: new[] { "TenantId", "CompanyId", "TaskId", "StepKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TaskDependencies_TenantId_CompanyId_TaskId_DependsOnStepId",
            schema: PlatformDbContext.DefaultSchema,
            table: "TaskDependencies",
            columns: new[] { "TenantId", "CompanyId", "TaskId", "DependsOnStepId" });

        migrationBuilder.CreateIndex(
            name: "IX_TaskEvents_TenantId_CompanyId_TaskId_StepId",
            schema: PlatformDbContext.DefaultSchema,
            table: "TaskEvents",
            columns: new[] { "TenantId", "CompanyId", "TaskId", "StepId" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TaskCheckpoints",
            schema: PlatformDbContext.DefaultSchema);

        migrationBuilder.DropTable(
            name: "TaskDependencies",
            schema: PlatformDbContext.DefaultSchema);

        migrationBuilder.DropTable(
            name: "TaskEvents",
            schema: PlatformDbContext.DefaultSchema);

        migrationBuilder.DropTable(
            name: "TaskSteps",
            schema: PlatformDbContext.DefaultSchema);

        migrationBuilder.DropTable(
            name: "Tasks",
            schema: PlatformDbContext.DefaultSchema);
    }
}
