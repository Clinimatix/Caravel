using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caravel.Worker.Database.ResultMigrations
{
    /// <inheritdoc />
    public partial class CreateResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProcessedQuantities",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessedQuantities", x => x.JobId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessedQuantities_TenantId",
                table: "ProcessedQuantities",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessedQuantities");
        }
    }
}
