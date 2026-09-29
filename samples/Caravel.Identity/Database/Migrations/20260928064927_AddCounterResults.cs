using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caravel.Identity.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddCounterResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CounterResults",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<string>(type: "TEXT", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CounterResults", x => x.JobId);
                    table.ForeignKey(
                        name: "FK_CounterResults_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CounterResults_OwnerId_RecordedAt_JobId",
                table: "CounterResults",
                columns: new[] { "OwnerId", "RecordedAt", "JobId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CounterResults");
        }
    }
}
