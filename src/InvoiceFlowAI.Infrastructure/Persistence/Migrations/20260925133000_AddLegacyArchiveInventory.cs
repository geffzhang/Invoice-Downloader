using InvoiceFlowAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InvoiceFlowAI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(InvoiceFlowDbContext))]
[Migration("20260925133000_AddLegacyArchiveInventory")]
public sealed class AddLegacyArchiveInventory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "LegacyArchiveInventory",
            columns: table => new
            {
                InventoryId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                RootKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                OriginalRelativePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                CurrentRelativePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                SourceFileName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                ContentHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                State = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                ReviewRunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table => table.PrimaryKey("PK_LegacyArchiveInventory", x => x.InventoryId));

        migrationBuilder.CreateIndex(
            name: "IX_LegacyArchiveInventory_RootKey_OriginalRelativePath_ContentHash",
            table: "LegacyArchiveInventory",
            columns: new[] { "RootKey", "OriginalRelativePath", "ContentHash" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LegacyArchiveInventory_RootKey_State",
            table: "LegacyArchiveInventory",
            columns: new[] { "RootKey", "State" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.DropTable(name: "LegacyArchiveInventory");
}