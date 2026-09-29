using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caravel.Worker.Database.QueueMigrations
{
    /// <inheritdoc />
    public partial class CreateQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaravelQueueJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Queue = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeduplicationKey = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    JobType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AvailableAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "TEXT", nullable: true),
                    LeaseExpiresAt = table.Column<long>(type: "INTEGER", nullable: true),
                    LastFailure = table.Column<int>(type: "INTEGER", nullable: true),
                    ReplayCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaravelQueueJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaravelQueueJobs_DeduplicationKey",
                table: "CaravelQueueJobs",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaravelQueueJobs_Queue_State_AvailableAt",
                table: "CaravelQueueJobs",
                columns: new[] { "Queue", "State", "AvailableAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaravelQueueJobs");
        }
    }
}
