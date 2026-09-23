using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceFlowAI.Infrastructure.Persistence.Migrations;

public partial class AddMailboxDefaultMailbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "DefaultMailbox",
            table: "MailboxAccounts",
            type: "TEXT",
            maxLength: 128,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "DefaultMailbox",
            table: "MailboxAccounts");
    }
}