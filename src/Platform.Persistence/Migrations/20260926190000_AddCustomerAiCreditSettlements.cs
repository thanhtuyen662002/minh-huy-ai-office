using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MinhHuy.AIOffice.Platform.Persistence.Migrations;

public partial class AddCustomerAiCreditSettlements : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE [aioffice].[CustomerAiCreditSettlements] (
                [SettlementId] nvarchar(200) NOT NULL,
                [ReservationId] nvarchar(200) NOT NULL,
                [TenantId] nvarchar(200) NOT NULL,
                [CompanyId] nvarchar(200) NOT NULL,
                [AuthorityVersion] bigint NOT NULL,
                [PricingPolicyId] nvarchar(200) NOT NULL,
                [PricingPolicyVersion] bigint NOT NULL,
                [SettledAiCredits] bigint NOT NULL,
                [ReleasedAiCredits] bigint NOT NULL,
                CONSTRAINT [PK_CustomerAiCreditSettlements] PRIMARY KEY ([SettlementId]),
                CONSTRAINT [UQ_CustomerAiCreditSettlements_ReservationId] UNIQUE ([ReservationId]),
                CONSTRAINT [CK_CustomerAiCreditSettlements_Credits] CHECK ([SettledAiCredits] >= 0 AND [ReleasedAiCredits] >= 0)
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE [aioffice].[CustomerAiCreditSettlements];");
}
