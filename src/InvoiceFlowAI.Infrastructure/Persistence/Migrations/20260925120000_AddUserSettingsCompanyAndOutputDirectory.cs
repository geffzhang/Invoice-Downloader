using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InvoiceFlowAI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(InvoiceFlowDbContext))]
[Migration("20260925120000_AddUserSettingsCompanyAndOutputDirectory")]
public sealed class AddUserSettingsCompanyAndOutputDirectory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CompanyName",
            table: "UserSettings",
            type: "TEXT",
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "LastOutputDirectory",
            table: "UserSettings",
            type: "TEXT",
            maxLength: 1024,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "CompanyName",
            table: "UserSettings");

        migrationBuilder.DropColumn(
            name: "LastOutputDirectory",
            table: "UserSettings");
    }
}