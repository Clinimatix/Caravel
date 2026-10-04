using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Caravel.Identity.Database.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaravelOutboxMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Queue = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TenantId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DeduplicationKey = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    JobType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    AvailableAt = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    DispatchedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    QueueJobId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaravelOutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Workspace",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspace", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkItem",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Revision = table.Column<int>(type: "INTEGER", nullable: false),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItem", x => x.Id);
                    table.UniqueConstraint("AK_WorkItem_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_WorkItem_Workspace_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspace",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkspaceMember",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    CanComplete = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMember", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceMember_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceMember_Workspace_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspace",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemReceipt",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ScopeKey = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    Fingerprint = table.Column<string>(type: "TEXT", unicode: false, maxLength: 64, nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ActorId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Notice = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemReceipt", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkItemReceipt_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkItemReceipt_WorkItem_WorkspaceId_ItemId",
                        columns: x => new { x.WorkspaceId, x.ItemId },
                        principalTable: "WorkItem",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkItemChange",
                columns: table => new
                {
                    ReceiptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Comment = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    FromRevision = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkItemChange", x => x.ReceiptId);
                    table.ForeignKey(
                        name: "FK_WorkItemChange_WorkItemReceipt_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "WorkItemReceipt",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CaravelOutboxMessages_DeduplicationKey",
                table: "CaravelOutboxMessages",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CaravelOutboxMessages_DispatchedAt_CreatedAt",
                table: "CaravelOutboxMessages",
                columns: new[] { "DispatchedAt", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReceipt_ActorId",
                table: "WorkItemReceipt",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReceipt_ScopeKey",
                table: "WorkItemReceipt",
                column: "ScopeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkItemReceipt_WorkspaceId_ItemId",
                table: "WorkItemReceipt",
                columns: new[] { "WorkspaceId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMember_UserId",
                table: "WorkspaceMember",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMember_WorkspaceId_UserId",
                table: "WorkspaceMember",
                columns: new[] { "WorkspaceId", "UserId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaravelOutboxMessages");

            migrationBuilder.DropTable(
                name: "WorkItemChange");

            migrationBuilder.DropTable(
                name: "WorkspaceMember");

            migrationBuilder.DropTable(
                name: "WorkItemReceipt");

            migrationBuilder.DropTable(
                name: "WorkItem");

            migrationBuilder.DropTable(
                name: "Workspace");
        }
    }
}
