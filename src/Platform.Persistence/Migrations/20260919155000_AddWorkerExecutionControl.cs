using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

[DbContext(typeof(PlatformDbContext))]
[Migration("20260919155000_AddWorkerExecutionControl")]
public partial class AddWorkerExecutionControl : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TaskStepExecutions",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IdempotencyKey = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: false),
                Attempt = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                LeaseOwnerId = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: true),
                LeaseFenceToken = table.Column<long>(
                    type: "bigint",
                    nullable: false,
                    defaultValue: 0L),
                LeaseAcquiredAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: true),
                LeaseExpiresAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: true),
                NextAttemptAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: true),
                LastFailureClass = table.Column<string>(
                    type: "nvarchar(40)",
                    maxLength: 40,
                    nullable: true),
                DeadLetteredAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: true),
                UpdatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()"),
                RowVersion = table.Column<byte[]>(
                    type: "rowversion",
                    rowVersion: true,
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_TaskStepExecutions",
                    x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId });
                table.ForeignKey(
                    name: "FK_TaskStepExecutions_TaskSteps_TenantId_CompanyId_TaskId_StepId",
                    columns: x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "TaskSteps",
                    principalColumns: new[] { "TenantId", "CompanyId", "TaskId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "TaskDispatches",
            schema: PlatformDbContext.DefaultSchema,
            columns: table => new
            {
                TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TaskId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                StepId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                MessageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                IdempotencyKey = table.Column<string>(
                    type: "nvarchar(200)",
                    maxLength: 200,
                    nullable: false),
                Attempt = table.Column<int>(type: "int", nullable: false),
                CheckpointVersion = table.Column<long>(type: "bigint", nullable: true),
                State = table.Column<string>(
                    type: "nvarchar(40)",
                    maxLength: 40,
                    nullable: false,
                    defaultValue: "Pending"),
                AvailableAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false),
                CreatedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: false,
                    defaultValueSql: "SYSDATETIMEOFFSET()"),
                PublishedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: true),
                AcknowledgedAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: true),
                DeadLetteredAtUtc = table.Column<DateTimeOffset>(
                    type: "datetimeoffset",
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey(
                    "PK_TaskDispatches",
                    x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId, x.MessageId });
                table.ForeignKey(
                    name: "FK_TaskDispatches_TaskSteps_TenantId_CompanyId_TaskId_StepId",
                    columns: x => new { x.TenantId, x.CompanyId, x.TaskId, x.StepId },
                    principalSchema: PlatformDbContext.DefaultSchema,
                    principalTable: "TaskSteps",
                    principalColumns: new[] { "TenantId", "CompanyId", "TaskId", "Id" },
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_TaskStepExecutions_TenantId_CompanyId_IdempotencyKey",
            schema: PlatformDbContext.DefaultSchema,
            table: "TaskStepExecutions",
            columns: new[] { "TenantId", "CompanyId", "IdempotencyKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TaskStepExecutions_TenantId_CompanyId_LeaseExpiresAtUtc",
            schema: PlatformDbContext.DefaultSchema,
            table: "TaskStepExecutions",
            columns: new[] { "TenantId", "CompanyId", "LeaseExpiresAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_TaskDispatches_TenantId_CompanyId_TaskId_StepId_Attempt",
            schema: PlatformDbContext.DefaultSchema,
            table: "TaskDispatches",
            columns: new[] { "TenantId", "CompanyId", "TaskId", "StepId", "Attempt" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_TaskDispatches_TenantId_CompanyId_State_AvailableAtUtc",
            schema: PlatformDbContext.DefaultSchema,
            table: "TaskDispatches",
            columns: new[] { "TenantId", "CompanyId", "State", "AvailableAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "TaskDispatches",
            schema: PlatformDbContext.DefaultSchema);

        migrationBuilder.DropTable(
            name: "TaskStepExecutions",
            schema: PlatformDbContext.DefaultSchema);
    }
}
