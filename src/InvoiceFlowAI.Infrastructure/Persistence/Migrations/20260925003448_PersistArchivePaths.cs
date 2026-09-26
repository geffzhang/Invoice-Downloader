using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvoiceFlowAI.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PersistArchivePaths : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FinalFilePath",
                table: "ArchivedArtifacts",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TempFilePath",
                table: "ArchivedArtifacts",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalFilePath",
                table: "ArchivedArtifacts");

            migrationBuilder.DropColumn(
                name: "TempFilePath",
                table: "ArchivedArtifacts");
        }
    }
}
