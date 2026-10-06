using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddScopedDataSourceSecretBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataSourceSecretBindings",
                schema: "aioffice",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CanonicalReference = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Label = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false, defaultValueSql: "SYSDATETIMEOFFSET()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSourceSecretBindings", x => new { x.TenantId, x.CompanyId, x.Id });
                    table.CheckConstraint("CK_DataSourceSecretBindings_Version", "[Version] > 0");
                    table.ForeignKey(
                        name: "FK_DataSourceSecretBindings_Companies_TenantId_CompanyId",
                        columns: x => new { x.TenantId, x.CompanyId },
                        principalSchema: "aioffice",
                        principalTable: "Companies",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DataSourceSecretBindings_TenantId_CompanyId_CanonicalReference",
                schema: "aioffice",
                table: "DataSourceSecretBindings",
                columns: new[] { "TenantId", "CompanyId", "CanonicalReference" },
                unique: true);

            // Runtime principals may read grants, but only an installation/operator
            // identity may provision them. Existing sources deliberately get no grant.
            migrationBuilder.Sql("""
                IF DATABASE_PRINCIPAL_ID(N'aioffice_binding_runtime') IS NULL
                    CREATE ROLE [aioffice_binding_runtime] AUTHORIZATION [dbo];
                IF DATABASE_PRINCIPAL_ID(N'aioffice_binding_operator_owner') IS NULL
                    CREATE USER [aioffice_binding_operator_owner] WITHOUT LOGIN;
                ALTER AUTHORIZATION ON OBJECT::[aioffice].[DataSourceSecretBindings] TO [aioffice_binding_operator_owner];
                GRANT SELECT ON OBJECT::[aioffice].[DataSourceSecretBindings] TO [aioffice_binding_runtime];
                GRANT VIEW DEFINITION TO [aioffice_binding_runtime];
                -- DENY CONTROL would also deny the required SELECT privilege.
                -- Effective CONTROL must be absent (verified at runtime), not denied.
                DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP
                    ON OBJECT::[aioffice].[DataSourceSecretBindings] TO [aioffice_binding_runtime];
                DENY UPDATE ON OBJECT::[aioffice].[DataSourceSecretBindings]
                    ([TenantId], [CompanyId], [Id], [CanonicalReference], [Label], [IsEnabled], [Version], [CreatedAtUtc])
                    TO [aioffice_binding_runtime];
                DENY ALTER ON ROLE::[aioffice_binding_runtime] TO [aioffice_binding_runtime];
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Scoped secret grants require a reviewed forward repair; security downgrade is not supported.");
        }
    }
}
