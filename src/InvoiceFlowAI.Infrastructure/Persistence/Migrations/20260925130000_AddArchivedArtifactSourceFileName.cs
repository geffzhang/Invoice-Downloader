using InvoiceFlowAI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InvoiceFlowAI.Infrastructure.Persistence.Migrations;

[DbContext(typeof(InvoiceFlowDbContext))]
[Migration("20260925130000_AddArchivedArtifactSourceFileName")]
public sealed class AddArchivedArtifactSourceFileName : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SourceFileName",
            table: "ArchivedArtifacts",
            type: "TEXT",
            maxLength: 256,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SourceFileName",
            table: "ArchivedArtifacts");
    }
}